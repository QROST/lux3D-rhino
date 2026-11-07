// Native isolated GLB import/block wrapping follows QROST/tripo-rhino (Apache-2.0).
// See repository NOTICE. Lux3D identity, placement, receipts and preview are independent.
using System.Security.Cryptography;
using Lux3D.Core;
using Rhino.Collections;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Display;
using Rhino.Render;
using RhinoDoc = global::Rhino.RhinoDoc;

namespace Lux3D.Rhino;

public sealed record PlacementIntent(Plane Plane, Point3d Point, double Height, double RotationDegrees)
{
    public Transform ToTransform(BoundingBox bounds)
    {
        double sourceHeight = bounds.Max.Z - bounds.Min.Z;
        if (!bounds.IsValid || !Plane.IsValid || !Point.IsValid || !double.IsFinite(Height) ||
            Height <= 0 || !double.IsFinite(RotationDegrees) || !double.IsFinite(sourceHeight) || sourceHeight <= 1e-12)
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Invalid placement plane, point, reference height or source model height."));
        Point3d anchor = new((bounds.Min.X + bounds.Max.X) / 2, (bounds.Min.Y + bounds.Max.Y) / 2, bounds.Min.Z);
        Plane target = new(Point, Plane.XAxis, Plane.YAxis);
        return Transform.PlaneToPlane(Plane.WorldXY, target) *
               Transform.Rotation(RotationDegrees * Math.PI / 180, Vector3d.ZAxis, Point3d.Origin) *
               Transform.Scale(Point3d.Origin, Height / sourceHeight) *
               Transform.Translation(Point3d.Origin - anchor);
    }
}

public sealed record ImportReceipt(Guid ImportId, Guid ObjectId, string DocumentSessionId, string AssetSha256, string State);
public sealed record AssetAudit(long Meshes, long Vertices, long Triangles, int Materials, int Textures, BoundingBox Bounds,
    long RemovedDegenerateFaces = 0, long RecomputedNormalMeshes = 0);

public sealed class PreparedAsset : IDisposable
{
    private readonly GlbImportSnapshot _snapshot;
    internal PreparedAsset(GlbImportSnapshot snapshot, RhinoDoc staging, AssetAudit audit, string documentSessionId, uint documentSerial)
    {
        _snapshot = snapshot; SnapshotPath = snapshot.Path; Sha256 = snapshot.Sha256; Staging = staging; Audit = audit;
        DocumentSessionId = documentSessionId; DocumentSerial = documentSerial;
    }
    public string Sha256 { get; }
    public AssetAudit Audit { get; }
    public string DocumentSessionId { get; }
    public uint DocumentSerial { get; }
    internal string SnapshotPath { get; }
    internal RhinoDoc Staging { get; }
    internal void Verify()
    {
        if (!File.Exists(SnapshotPath))
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The preview asset is missing or changed. Reload it."));
        using var stream = File.OpenRead(SnapshotPath);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The preview asset is missing or changed. Reload it."));
    }
    public void Dispose()
    {
        try { Staging.Dispose(); }
        finally { _snapshot.Dispose(); }
    }
}

/// <summary>All entry points run on Rhino's UI thread. No background callback mutates a document.</summary>
public sealed class Lux3DHostService : IDisposable
{
    private readonly Dictionary<uint, string> _sessions = new();
    private readonly Dictionary<(uint, Guid), ImportReceipt> _receipts = new();
    private readonly Dictionary<(uint, Guid), string> _deletedStates = new();
    private readonly Dictionary<(uint, Guid), string> _fingerprints = new();
    private readonly string? _importStoreRoot;
    public Lux3DHostService(string? importStoreRoot = null)
    {
        if (importStoreRoot is not null && !Path.IsPathRooted(importStoreRoot)) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("The imported asset directory must be an absolute path."));
        _importStoreRoot = importStoreRoot;
        RhinoDoc.DeleteRhinoObject += OnDeleted;
    }
    private void OnDeleted(object? sender, RhinoObjectEventArgs e)
    {
        RhinoDoc doc = e.TheObject.Document;
        if (doc is not null && Guid.TryParse(e.TheObject.Attributes.GetUserString("Lux3D.ImportId"), out Guid importId) &&
            _receipts.TryGetValue((doc.RuntimeSerialNumber, importId), out ImportReceipt? receipt) && receipt.ObjectId == e.ObjectId)
            _deletedStates[(doc.RuntimeSerialNumber, e.ObjectId)] = doc.UndoActive ? "Undone" : "Missing";
    }
    public void Dispose() { RhinoDoc.DeleteRhinoObject -= OnDeleted; }
    private static void RequireUiThread()
    {
        if (global::Rhino.RhinoApp.InvokeRequired) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Rhino document operations must run on the UI thread."));
    }
    public string Session(RhinoDoc doc)
    {
        if (!_sessions.TryGetValue(doc.RuntimeSerialNumber, out string? session))
            _sessions.Add(doc.RuntimeSerialNumber, session = Guid.NewGuid().ToString("D"));
        return session;
    }
    public void Forget(uint serial)
    {
        _sessions.Remove(serial);
        foreach (var key in _receipts.Keys.Where(k => k.Item1 == serial).ToArray()) _receipts.Remove(key);
        foreach (var key in _fingerprints.Keys.Where(k => k.Item1 == serial).ToArray()) _fingerprints.Remove(key);
        foreach (var key in _deletedStates.Keys.Where(k => k.Item1 == serial).ToArray()) _deletedStates.Remove(key);
    }
    public PreparedAsset Prepare(string path, string? expectedSha256, RhinoDoc target, HostImportTrace? trace = null)
    {
        RequireUiThread();
        GlbImportSnapshot snapshot = GlbImportSnapshot.Create(path, expectedSha256, trace);
        try { return Prepare(snapshot, target, trace); }
        catch { snapshot.Dispose(); throw; }
    }
    internal PreparedAsset Prepare(GlbImportSnapshot snapshot, RhinoDoc target, HostImportTrace? trace = null)
    {
        RequireUiThread();
        trace?.Record("preflight_start");
        RhinoDoc? stage = null;
        try
        {
            stage = RhinoDoc.CreateHeadless(null) ?? throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Could not create an independent preview document."));
            stage.ModelUnitSystem = target.ModelUnitSystem;
            stage.ModelAbsoluteTolerance = target.ModelAbsoluteTolerance;
            HashSet<Guid> existingDefinitions = stage.InstanceDefinitions.GetList(true).Select(d => d.Id).ToHashSet();
            trace?.Record("preflight_native_import_start");
            bool imported;
            try { imported = stage.Import(snapshot.Path, new ArchivableDictionary()); }
            catch (Exception ex)
            {
                trace?.Record("preflight_native_import_exception", ex.Message);
                throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Preflight: Rhino's native GLB import threw an exception before mesh repair. ") + ex.Message, ex);
            }
            trace?.Record("preflight_native_import_result", imported ? "true" : "false");
            if (!imported) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Preflight: Rhino's native GLB import failed before mesh repair."));
            RhinoObject[] roots = Objects(stage);
            using MeshRepairs repairs = CreateMeshRepairs(roots, trace);
            ApplyDefinitionRepairs(stage, roots, repairs, existingDefinitions, trace);
            // Replacement may create a proxy undo record. It is confined to the disposable headless document.
            foreach (RhinoObject root in roots)
                if (repairs.Meshes.TryGetValue(root.Id, out Mesh? repaired) && !stage.Objects.Replace(root.Id, repaired))
                    throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Preflight: could not replace the repaired temporary mesh."));
            AssetAudit audit = Audit(Objects(stage)) with
            {
                RemovedDegenerateFaces = repairs.RemovedFaces,
                RecomputedNormalMeshes = repairs.Meshes.Count,
            };
            trace?.Record("preflight_audit_complete", AuditDetail(audit));
            return new PreparedAsset(snapshot, stage, audit, Session(target), target.RuntimeSerialNumber);
        }
        catch (Exception ex)
        {
            trace?.Record("preflight_failed", ex.Message);
            try { stage?.Dispose(); } catch { }
            snapshot.Dispose();
            throw;
        }
    }
    internal ImportReceipt? FindReceipt(RhinoDoc doc, Guid importId)
    {
        RequireUiThread();
        if (_receipts.ContainsKey((doc.RuntimeSerialNumber, importId))) return InspectReceipt(doc, importId);
        RhinoObject[] matches = Objects(doc).Where(o => o.Attributes.GetUserString("Lux3D.ImportId") == importId.ToString("D")).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || matches[0] is not InstanceObject)
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The document has duplicate or invalid task import markers. Inspect existing objects first."));
        string? sha = matches[0].Attributes.GetUserString("Lux3D.AssetSha256");
        if (string.IsNullOrWhiteSpace(sha)) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("An imported object already exists, but its asset verification marker is missing. No duplicate was inserted."));
        var receipt = new ImportReceipt(importId, matches[0].Id, Session(doc), sha, "Imported");
        _receipts[(doc.RuntimeSerialNumber, importId)] = receipt;
        return receipt;
    }
    public ImportReceipt InspectReceipt(RhinoDoc doc, Guid importId)
    {
        if (!_receipts.TryGetValue((doc.RuntimeSerialNumber, importId), out ImportReceipt? receipt))
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The import ID has not been committed."));
        RhinoObject? obj = doc.Objects.FindId(receipt.ObjectId);
        string missing = _deletedStates.TryGetValue((doc.RuntimeSerialNumber, receipt.ObjectId), out string? observed) ? observed : "Missing";
        string state = obj is not null && !obj.IsDeleted && obj.Attributes.GetUserString("Lux3D.ImportId") == importId.ToString("D")
            ? "Imported" : missing;
        return receipt with { State = state };
    }
    public ImportReceipt Commit(PreparedAsset asset, PlacementIntent placement, Guid importId, Guid? generationOperationId = null, HostImportTrace? trace = null)
    {
        RequireUiThread();
        trace?.Record("commit_start");
        if (importId == Guid.Empty) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("The import ID cannot be empty."), nameof(importId));
        RhinoDoc doc = RhinoDoc.FromRuntimeSerialNumber(asset.DocumentSerial) ?? throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The target document is closed."));
        if (Session(doc) != asset.DocumentSessionId) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The target document session has expired."));
        if (doc.ModelUnitSystem != asset.Staging.ModelUnitSystem) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The target document units changed. Preview again."));
        Transform plannedTransform = placement.ToTransform(asset.Audit.Bounds);
        string fingerprint = asset.Sha256 + "|" + string.Join("|", Enumerable.Range(0, 4).SelectMany(r => Enumerable.Range(0, 4).Select(c => plannedTransform[r, c].ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        if (_fingerprints.TryGetValue((doc.RuntimeSerialNumber, importId), out string? previous))
        {
            if (previous != fingerprint) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The same import ID refers to different assets or placement settings."));
            if (_receipts.ContainsKey((doc.RuntimeSerialNumber, importId))) return InspectReceipt(doc, importId);
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("This import failed and cannot be replayed automatically. Inspect the document before explicitly creating a new import."));
        }
        if (doc.UndoRecordingIsActive) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Finish the current Rhino command before accepting a candidate."));
        int targetLayerIndex = doc.Layers.CurrentLayerIndex;
        asset.Verify();
        string durableImportPath = PersistImportSnapshot(asset);
        trace?.Record("commit_snapshot_verified");
        Transform transform = placement.ToTransform(asset.Audit.Bounds);
        HashSet<Guid> before = Objects(doc).Select(o => o.Id).ToHashSet();
        HashSet<Guid> definitionsBefore = doc.InstanceDefinitions.GetList(true).Select(d => d.Id).ToHashSet();
        string beforeState = DocumentState(doc);
        uint undo = doc.BeginUndoRecord(global::Lux3D.Core.LocalText.Get("Accept Lux3D candidate"));
        if (undo == 0) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Could not create an undo record. Nothing was imported."));
        List<GeometryBase> geometry = new();
        List<ObjectAttributes> attributes = new();
        int definitionIndex = -1;
        Guid instanceId = Guid.Empty;
        Exception? failure = null;
        bool undoEnded;
        _fingerprints.Add((doc.RuntimeSerialNumber, importId), fingerprint);
        try
        {
            using (var marker = new global::Rhino.Geometry.Point(Point3d.Origin))
            using (var markerAttributes = new ObjectAttributes())
                definitionIndex = doc.InstanceDefinitions.Add("Lux3D_" + importId.ToString("N"), "Lux3D native GLB; SHA256 " + asset.Sha256, Point3d.Origin, new[] { marker }, new[] { markerAttributes });
            if (definitionIndex < 0) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Could not create the Lux3D block."));
            trace?.Record("commit_native_import_start");
            bool importedNative;
            try { importedNative = doc.Import(durableImportPath, new ArchivableDictionary()); }
            catch (Exception ex)
            {
                trace?.Record("commit_native_import_exception", ex.Message);
                throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Document import: Rhino's native GLB import threw an exception before mesh repair. ") + ex.Message, ex);
            }
            trace?.Record("commit_native_import_result", importedNative ? "true" : "false");
            if (!importedNative) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Document import: Rhino's native GLB import failed before mesh repair."));
            VerifyHash(durableImportPath, asset.Sha256);
            asset.Verify();
            RhinoObject[] added = Objects(doc).Where(o => !before.Contains(o.Id)).ToArray();
            using MeshRepairs repairs = CreateMeshRepairs(added, trace);
            ApplyDefinitionRepairs(doc, added, repairs, definitionsBefore, trace);
            added = Objects(doc).Where(o => !before.Contains(o.Id)).ToArray();
            // Top-level meshes are repaired only in the copies used for the new Block.
            // Do not call ObjectTable.Replace on the user's document: its proxy command may add an undo record.
            AssetAudit imported = Audit(added, repairs.Meshes) with
            {
                RemovedDegenerateFaces = repairs.RemovedFaces,
                RecomputedNormalMeshes = repairs.Meshes.Count,
            };
            if (imported.Meshes != asset.Audit.Meshes || imported.Vertices != asset.Audit.Vertices || imported.Triangles != asset.Audit.Triangles ||
                !imported.Bounds.Min.EpsilonEquals(asset.Audit.Bounds.Min, doc.ModelAbsoluteTolerance) ||
                !imported.Bounds.Max.EpsilonEquals(asset.Audit.Bounds.Max, doc.ModelAbsoluteTolerance))
                throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Imported geometry does not match the preview."));
            trace?.Record("commit_audit_complete", AuditDetail(imported));
            foreach (RhinoObject obj in added)
            {
                geometry.Add(repairs.Meshes.TryGetValue(obj.Id, out Mesh? repaired) ? repaired.DuplicateMesh() : obj.Geometry.Duplicate());
                ObjectAttributes attr = obj.Attributes.Duplicate();
                attr.SetUserString("Lux3D.AssetSha256", asset.Sha256);
                attributes.Add(attr);
            }
            if (!doc.InstanceDefinitions.ModifyGeometry(definitionIndex, geometry, attributes)) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Could not write Lux3D block geometry."));
            trace?.Record("commit_block_geometry_ready");
            using ObjectAttributes instanceAttributes = doc.CreateDefaultAttributes();
            instanceAttributes.Name = "Lux3D";
            instanceAttributes.LayerIndex = targetLayerIndex;
            instanceAttributes.Space = ActiveSpace.ModelSpace;
            instanceAttributes.ViewportId = Guid.Empty;
            instanceAttributes.SetUserString("Lux3D.ImportId", importId.ToString("D"));
            instanceAttributes.SetUserString("Lux3D.AssetSha256", asset.Sha256);
            instanceAttributes.SetUserString("Lux3D.DocumentSession", asset.DocumentSessionId);
            if (generationOperationId.HasValue) instanceAttributes.SetUserString("Lux3D.GenerationOperationId", generationOperationId.Value.ToString("D"));
            instanceId = doc.Objects.AddInstanceObject(definitionIndex, transform, instanceAttributes);
            if (instanceId == Guid.Empty) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Could not place the Lux3D block."));
            trace?.Record("commit_instance_added", instanceId.ToString("D"));
            if (doc.Objects.Delete(added.Select(o => o.Id), true) != added.Length) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Could not remove temporary top-level objects from this import."));
            if (Objects(doc).Count(o => !before.Contains(o.Id)) != 1) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The import created unexpected top-level objects."));
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            foreach (GeometryBase item in geometry) item.Dispose();
            foreach (ObjectAttributes item in attributes) item.Dispose();
            undoEnded = doc.EndUndoRecord(undo);
        }
        if (failure is not null)
        {
            trace?.Record("commit_rollback_start", failure.Message);
            // The record was started here and no concurrent UI commands can run inside this synchronous block.
            bool restored = DocumentState(doc) == beforeState;
            if (!restored && undoEnded && doc.Undo())
            {
                doc.ClearRedoRecords();
                restored = DocumentState(doc) == beforeState;
            }
            trace?.Record("commit_rollback_result", restored ? "restored" : "unconfirmed");
            if (!restored) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Import failed and automatic rollback could not be confirmed. Inspect the document and Undo history."), failure);
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Import failed; this operation's document changes were undone. ") + failure.Message, failure);
        }
        if (!undoEnded) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The imported object exists, but its undo record could not be confirmed. Inspect the document; do not import again."));
        ImportReceipt receipt = new(importId, instanceId, asset.DocumentSessionId, asset.Sha256, "Imported");
        _receipts.Add((doc.RuntimeSerialNumber, importId), receipt);
        trace?.Record("commit_complete", instanceId.ToString("D"));
        try { doc.Views.Redraw(); }
        catch { /* View refresh cannot turn a completed document import into a failed import. */ }
        return receipt;
    }
    private string PersistImportSnapshot(PreparedAsset asset)
    {
        // Final native import must not depend on a candidate's disposable preview directory.
        string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Could not locate the persistent local asset directory."));
        string directory = Path.Combine(_importStoreRoot ?? Path.Combine(root, "Lux3D-Rhino"), "host-imports", asset.Sha256.ToLowerInvariant());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "asset.glb");
        if (!File.Exists(path))
        {
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                File.Copy(asset.SnapshotPath, temporary, overwrite: false);
                VerifyHash(temporary, asset.Sha256);
                try { File.Move(temporary, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        VerifyHash(path, asset.Sha256);
        return path;
    }
    private static void VerifyHash(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Persistent import asset verification failed; the target document was not changed."));
    }
    private static string DocumentState(RhinoDoc doc)
    {
        // A failed import must not undo an earlier user action if the native importer made no change.
        var parts = new List<string>();
        parts.AddRange(Objects(doc).Select(x => "o" + x.Id));
        parts.AddRange(doc.InstanceDefinitions.GetList(true).Select(x => "d" + x.Id));
        for (int i = 0; i < doc.Materials.Count; i++) if (!doc.Materials[i].IsDeleted) parts.Add("m" + doc.Materials[i].Id);
        for (int i = 0; i < doc.Layers.Count; i++) if (!doc.Layers[i].IsDeleted) parts.Add("l" + doc.Layers[i].Id);
        parts.AddRange(doc.RenderMaterials.Select(x => "r" + x.Id));
        parts.AddRange(doc.RenderTextures.Select(x => "t" + x.Id));
        parts.AddRange(doc.GetEmbeddedFilesList(false).Select(x => "f" + x));
        parts.Add("units" + doc.ModelUnitSystem); parts.Add("layer" + doc.Layers.CurrentLayerIndex);
        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }
    internal static RhinoObject[] Objects(RhinoDoc doc) => doc.Objects.GetObjectList(new ObjectEnumeratorSettings
    {
        NormalObjects = true, LockedObjects = true, HiddenObjects = true, IdefObjects = false, DeletedObjects = false,
        ActiveObjects = true, ReferenceObjects = false, IncludeLights = true, IncludeGrips = true, IncludePhantoms = true,
    }).ToArray();

    internal static IEnumerable<(Mesh Mesh, Transform Transform, RhinoObject Source)> Meshes(IEnumerable<RhinoObject> objects, Transform transform)
    {
        // A definition may appear in multiple instances; only ancestry repetition is a cycle.
        var ancestry = new HashSet<Guid>();
        var pending = new Stack<(IEnumerator<RhinoObject> Objects, Transform Transform, Guid? Definition)>();
        pending.Push((objects.GetEnumerator(), transform, null));
        try
        {
            while (pending.Count > 0)
            {
                var frame = pending.Peek();
                if (!frame.Objects.MoveNext())
                {
                    frame.Objects.Dispose();
                    pending.Pop();
                    if (frame.Definition.HasValue) ancestry.Remove(frame.Definition.Value);
                    continue;
                }
                RhinoObject obj = frame.Objects.Current;
                if (obj.Geometry is Mesh mesh) yield return (mesh, frame.Transform, obj);
                else if (obj is InstanceObject instance)
                {
                    InstanceDefinition definition = instance.InstanceDefinition ?? throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("A GLB block references a missing definition."));
                    if (!ancestry.Add(definition.Id)) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("A GLB block contains a cyclic reference."));
                    Transform combined = frame.Transform * instance.InstanceXform;
                    if (!combined.IsValid) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Invalid GLB block transform."));
                    pending.Push((((IEnumerable<RhinoObject>)definition.GetObjects()).GetEnumerator(), combined, definition.Id));
                }
                else throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The GLB contains unsupported non-mesh objects."));
            }
        }
        finally { while (pending.Count > 0) pending.Pop().Objects.Dispose(); }
    }
    private sealed class MeshRepairs : IDisposable
    {
        public Dictionary<Guid, Mesh> Meshes { get; } = new();
        public long RemovedFaces { get; set; }
        public void Dispose() { foreach (Mesh mesh in Meshes.Values) mesh.Dispose(); }
    }
    private static MeshRepairs CreateMeshRepairs(IEnumerable<RhinoObject> roots, HostImportTrace? trace)
    {
        var repairs = new MeshRepairs();
        var inspected = new HashSet<Guid>();
        try
        {
            foreach (var (mesh, _, source) in Meshes(roots, Transform.Identity))
            {
                if (!inspected.Add(source.Id) || mesh.IsValidWithLog(out string beforeLog)) continue;
                trace?.Record("mesh_repair_start", "mesh=" + source.Id.ToString("D") + "; reason=" + beforeLog);
                Mesh? repaired = RepairMeshCopy(mesh, out int removed);
                try
                {
                    repairs.RemovedFaces += removed;
                    repairs.Meshes.Add(source.Id, repaired);
                    repaired = null; // Ownership moves to the repair set until document copies have been created.
                    trace?.Record("mesh_repair_valid", $"removedDegenerateFaces={removed}; recomputedNormalMeshes=1");
                }
                finally { repaired?.Dispose(); }
            }
            trace?.Record("mesh_repair_summary", $"removedDegenerateFaces={repairs.RemovedFaces}; recomputedNormalMeshes={repairs.Meshes.Count}");
            return repairs;
        }
        catch { repairs.Dispose(); throw; }
    }
    internal static Mesh RepairMeshCopy(Mesh mesh, out int removedFaces)
    {
        removedFaces = 0;
        bool sourceValid = mesh.IsValidWithLog(out string beforeLog);
        Mesh repaired = mesh.DuplicateMesh();
        if (sourceValid) return repaired;
        try
        {
            removedFaces = repaired.Faces.CullDegenerateFaces();
            bool normalsComputed = repaired.Normals.ComputeNormals();
            bool valid = repaired.IsValidWithLog(out string afterLog);
            if (!normalsComputed || !valid)
                throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Limited mesh repair did not pass validation. Original reason: ") + beforeLog + global::Lux3D.Core.LocalText.Get(" After repair: ") + afterLog);
            if (repaired.Vertices.Count != mesh.Vertices.Count || repaired.TextureCoordinates.Count != mesh.TextureCoordinates.Count ||
                repaired.VertexColors.Count != mesh.VertexColors.Count || repaired.Faces.Count != mesh.Faces.Count - removedFaces)
                throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Mesh repair changed more than degenerate-face removal and normal recomputation; the repair was rejected."));
            return repaired;
        }
        catch { repaired.Dispose(); throw; }
    }
    private static void ApplyDefinitionRepairs(RhinoDoc doc, IEnumerable<RhinoObject> roots, MeshRepairs repairs,
        HashSet<Guid> protectedDefinitions, HostImportTrace? trace)
    {
        // Native imports can contain nested Blocks. Only definitions created by this import may be modified.
        // CreateMeshRepairs has already traversed the complete graph and rejected cyclic references.
        var pending = new Stack<InstanceDefinition>();
        var visited = new HashSet<Guid>();
        foreach (RhinoObject root in roots)
            if (root is InstanceObject instance) pending.Push(instance.InstanceDefinition);
        while (pending.Count > 0)
        {
            InstanceDefinition definition = pending.Pop();
            if (!visited.Add(definition.Id)) continue;
            RhinoObject[] members = definition.GetObjects();
            foreach (RhinoObject member in members)
                if (member is InstanceObject instance) pending.Push(instance.InstanceDefinition);
            if (!members.Any(m => repairs.Meshes.ContainsKey(m.Id))) continue;
            if (protectedDefinitions.Contains(definition.Id))
                throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("This import references an invalid mesh definition already in the document. The existing block was not changed; inspect its definition first."));
            var geometry = new List<GeometryBase>();
            var attributes = new List<ObjectAttributes>();
            try
            {
                foreach (RhinoObject member in members)
                {
                    geometry.Add(repairs.Meshes.TryGetValue(member.Id, out Mesh? repaired) ? repaired.DuplicateMesh() : member.Geometry.Duplicate());
                    attributes.Add(member.Attributes.Duplicate());
                }
                if (!doc.InstanceDefinitions.ModifyGeometry(definition.Index, geometry, attributes))
                    throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Could not write the repaired mesh into the newly imported block definition."));
                trace?.Record("mesh_definition_repaired", definition.Id.ToString("D"));
            }
            finally
            {
                foreach (GeometryBase item in geometry) item.Dispose();
                foreach (ObjectAttributes item in attributes) item.Dispose();
            }
        }
    }
    private static string AuditDetail(AssetAudit audit) =>
        $"meshes={audit.Meshes}; vertices={audit.Vertices}; triangles={audit.Triangles}; removedDegenerateFaces={audit.RemovedDegenerateFaces}; recomputedNormalMeshes={audit.RecomputedNormalMeshes}";

    internal static AssetAudit Audit(IEnumerable<RhinoObject> objects, IReadOnlyDictionary<Guid, Mesh>? replacements = null)
    {
        long count = 0, vertices = 0, triangles = 0;
        BoundingBox bounds = BoundingBox.Empty;
        HashSet<Guid> materials = new();
        HashSet<string> textures = new(StringComparer.Ordinal);
        foreach (var (mesh, transform, source) in Meshes(objects, Transform.Identity))
        {
            Mesh inspected = replacements is not null && replacements.TryGetValue(source.Id, out Mesh? repaired) ? repaired : mesh;
            if (!inspected.IsValidWithLog(out string log)) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Invalid GLB mesh: ") + log);
            count++;
            vertices = checked(vertices + inspected.Vertices.Count);
            triangles = checked(triangles + inspected.Faces.Sum(f => f.IsQuad ? 2L : 1L));
            bounds.Union(inspected.GetBoundingBox(transform));
            Material material = source.GetMaterial(true);
            if (material is not null)
            {
                materials.Add(material.Id);
                foreach (Texture texture in material.GetTextures()) if (!string.IsNullOrWhiteSpace(texture.FileName)) textures.Add(texture.FileName);
            }
            RenderMaterial renderMaterial = source.GetRenderMaterial(true);
            if (renderMaterial is not null)
            {
                materials.Add(renderMaterial.Id);
                foreach (string path in renderMaterial.GetEmbeddedFilesList()) textures.Add(path);
            }
        }
        if (count == 0 || !bounds.IsValid) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The GLB contains no valid importable mesh."));
        return new AssetAudit(count, vertices, triangles, materials.Count, textures.Count, bounds);
    }
}

/// <summary>Conduit preview never adds document objects or undo records. It previews shape/base color, not full PBR.</summary>
public sealed class CandidatePreview : DisplayConduit, IDisposable
{
    private readonly object _sync = new();
    private PreviewGeometry? _geometry;
    private Transform _modelTransform = Transform.Identity;
    private BoundingBox _bounds = BoundingBox.Empty;
    private uint _documentSerial;
    private bool _visible;
    private bool _disposed;

    public void Show(PreparedAsset asset, PlacementIntent placement)
    {
        RequireUiThread();
        ArgumentNullException.ThrowIfNull(asset);
        lock (_sync)
            if (_disposed) throw new ObjectDisposedException(nameof(CandidatePreview));
        Transform placementTransform = placement.ToTransform(asset.Audit.Bounds);
        PreviewGeometry? current;
        lock (_sync) current = _geometry;
        PreviewGeometry next = current is not null && ReferenceEquals(current.Asset, asset)
            ? current : PreviewGeometry.Create(asset);
        BoundingBox bounds = next.SourceBounds;
        if (!bounds.Transform(placementTransform) || !bounds.IsValid)
        {
            if (!ReferenceEquals(next, current)) next.Dispose();
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Invalid transformed preview bounding box."));
        }
        uint previousDocument;
        lock (_sync)
        {
            previousDocument = _documentSerial;
            _geometry = next;
            _modelTransform = placementTransform;
            _bounds = bounds;
            _documentSerial = asset.DocumentSerial;
            _visible = true;
        }
        // Draw callbacks hold _sync while using a cache. Once it has been swapped
        // out here, no callback can still be drawing its meshes or materials.
        if (!ReferenceEquals(current, next)) current?.Dispose();
        if (!Enabled) Enabled = true;
        if (previousDocument != 0 && previousDocument != asset.DocumentSerial)
            Redraw(previousDocument);
        Redraw(asset.DocumentSerial);
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        lock (_sync)
            if (_visible && e.RhinoDoc?.RuntimeSerialNumber == _documentSerial)
                e.IncludeBoundingBox(_bounds);
    }

    protected override void CalculateBoundingBoxZoomExtents(CalculateBoundingBoxEventArgs e) => CalculateBoundingBox(e);

    protected override void PostDrawObjects(DrawEventArgs e)
    {
        lock (_sync)
        {
            if (!_visible || _geometry is null || e.RhinoDoc?.RuntimeSerialNumber != _documentSerial) return;
            // PushModelTransform sets, rather than multiplies, the current matrix.
            // Keep any enclosing display transform and always restore its stack.
            e.Display.PushModelTransform(e.Display.ModelTransform * _modelTransform);
            try
            {
                foreach (var item in _geometry.Items)
                    e.Display.DrawMeshShaded(item.Mesh, item.Material);
            }
            finally { e.Display.PopModelTransform(); }
        }
    }

    public void Clear()
    {
        RequireUiThread();
        lock (_sync)
            if (_disposed || (!_visible && _geometry is null)) return;
        Enabled = false;
        PreviewGeometry? previous;
        uint previousDocument;
        lock (_sync)
        {
            previous = _geometry;
            previousDocument = _documentSerial;
            _geometry = null;
            _bounds = BoundingBox.Empty;
            _modelTransform = Transform.Identity;
            _documentSerial = 0;
            _visible = false;
        }
        try { previous?.Dispose(); }
        finally { Redraw(previousDocument); }
    }

    public void Dispose()
    {
        RequireUiThread();
        lock (_sync)
            if (_disposed) return;
        try { Clear(); }
        finally
        {
            lock (_sync) _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // Read-only lifecycle evidence for the in-Rhino self-test; never used on the draw path.
    internal PreviewState CaptureState()
    {
        lock (_sync)
            return new PreviewState(_geometry, _modelTransform, _geometry?.SourceBounds ?? BoundingBox.Empty,
                _bounds, _documentSerial, _geometry?.Items.Length ?? 0, _visible, _disposed);
    }

    internal readonly record struct PreviewState(object? GeometryCache, Transform ModelTransform,
        BoundingBox SourceBounds, BoundingBox Bounds, uint DocumentSerial, int MeshCount, bool Visible, bool Disposed);

    private static void RequireUiThread()
    {
        if (global::Rhino.RhinoApp.InvokeRequired)
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Candidate previews must be updated or released on Rhino's UI thread."));
    }

    private static void Redraw(uint documentSerial)
    {
        if (documentSerial != 0) RhinoDoc.FromRuntimeSerialNumber(documentSerial)?.Views.Redraw();
    }

    private sealed class PreviewGeometry : IDisposable
    {
        private PreviewGeometry(PreparedAsset asset, (Mesh Mesh, DisplayMaterial Material)[] items, BoundingBox bounds)
        {
            Asset = asset; Items = items; SourceBounds = bounds;
        }
        public PreparedAsset Asset { get; }
        public (Mesh Mesh, DisplayMaterial Material)[] Items { get; private set; }
        public BoundingBox SourceBounds { get; }

        public static PreviewGeometry Create(PreparedAsset asset)
        {
            // Verify on first load/switch only. Commit independently verifies the
            // snapshot again; slider changes use this immutable geometry cache.
            asset.Verify();
            var items = new List<(Mesh Mesh, DisplayMaterial Material)>();
            BoundingBox bounds = BoundingBox.Empty;
            try
            {
                foreach (var (source, transform, obj) in Lux3DHostService.Meshes(Lux3DHostService.Objects(asset.Staging), Transform.Identity))
                {
                    Mesh? mesh = source.DuplicateMesh();
                    DisplayMaterial? material = null;
                    try
                    {
                        // Bake only the asset's node transform, once. Placement
                        // scale/rotation/translation stays on the display stack.
                        if (!mesh.Transform(transform)) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The candidate mesh node transform failed."));
                        bounds.Union(mesh.GetBoundingBox(true));
                        material = new DisplayMaterial(obj.GetMaterial(true));
                        items.Add((mesh, material));
                        mesh = null; material = null;
                    }
                    finally { try { mesh?.Dispose(); } finally { material?.Dispose(); } }
                }
                if (items.Count == 0 || !bounds.IsValid) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The candidate contains no previewable mesh."));
                return new PreviewGeometry(asset, items.ToArray(), bounds);
            }
            catch
            {
                DisposeItems(items);
                throw;
            }
        }

        public void Dispose()
        {
            var items = Items;
            Items = Array.Empty<(Mesh, DisplayMaterial)>();
            DisposeItems(items);
        }

        private static void DisposeItems(IEnumerable<(Mesh Mesh, DisplayMaterial Material)> items)
        {
            Exception? failure = null;
            foreach (var item in items)
            {
                try { item.Mesh.Dispose(); } catch (Exception ex) { failure ??= ex; }
                try { item.Material.Dispose(); } catch (Exception ex) { failure ??= ex; }
            }
            if (failure is not null) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Candidate preview resources could not be fully released."), failure);
        }
    }
}

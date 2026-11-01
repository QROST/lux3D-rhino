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
            throw new InvalidOperationException("放置平面、点、参考高度或源模型高度无效。");
        Point3d anchor = new((bounds.Min.X + bounds.Max.X) / 2, (bounds.Min.Y + bounds.Max.Y) / 2, bounds.Min.Z);
        Plane target = new(Point, Plane.XAxis, Plane.YAxis);
        return Transform.PlaneToPlane(Plane.WorldXY, target) *
               Transform.Rotation(RotationDegrees * Math.PI / 180, Vector3d.ZAxis, Point3d.Origin) *
               Transform.Scale(Point3d.Origin, Height / sourceHeight) *
               Transform.Translation(Point3d.Origin - anchor);
    }
}

public sealed record ImportReceipt(Guid ImportId, Guid ObjectId, string DocumentSessionId, string AssetSha256, string State);
public sealed record AssetAudit(long Meshes, long Vertices, long Triangles, int Materials, int Textures, BoundingBox Bounds);

public sealed class PreparedAsset : IDisposable
{
    internal PreparedAsset(string snapshotPath, string sha256, RhinoDoc staging, AssetAudit audit, string documentSessionId, uint documentSerial)
    {
        SnapshotPath = snapshotPath; Sha256 = sha256; Staging = staging; Audit = audit;
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
            throw new InvalidOperationException("预览资产已丢失或发生变化，请重新加载。");
        using var stream = File.OpenRead(SnapshotPath);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("预览资产已丢失或发生变化，请重新加载。");
    }
    public void Dispose()
    {
        Staging.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(SnapshotPath)!, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
        if (importStoreRoot is not null && !Path.IsPathRooted(importStoreRoot)) throw new ArgumentException("导入资产目录必须为绝对路径。");
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
        if (global::Rhino.RhinoApp.InvokeRequired) throw new InvalidOperationException("Rhino 文档操作必须在 UI 线程执行。");
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
    public PreparedAsset Prepare(string path, string? expectedSha256, RhinoDoc target)
    {
        RequireUiThread();
        if (!File.Exists(path)) throw new FileNotFoundException("本地 GLB 不存在。", path);
        byte[] bytes = File.ReadAllBytes(path);
        GlbContainerValidator.Validate(bytes);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        if (expectedSha256 is not null && !string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GLB 哈希与任务记录不一致。");
        string directory = Path.Combine(Path.GetTempPath(), "Lux3D-Rhino", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string snapshot = Path.Combine(directory, "asset.glb");
        File.WriteAllBytes(snapshot, bytes);
        RhinoDoc? stage = null;
        try
        {
            stage = RhinoDoc.CreateHeadless(null) ?? throw new InvalidOperationException("无法创建独立预览文档。");
            stage.ModelUnitSystem = target.ModelUnitSystem;
            stage.ModelAbsoluteTolerance = target.ModelAbsoluteTolerance;
            if (!stage.Import(snapshot, new ArchivableDictionary())) throw new InvalidOperationException("Rhino 无法导入此 GLB。");
            AssetAudit audit = Audit(Objects(stage));
            return new PreparedAsset(snapshot, sha, stage, audit, Session(target), target.RuntimeSerialNumber);
        }
        catch
        {
            stage?.Dispose();
            Directory.Delete(directory, true);
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
            throw new InvalidOperationException("文档内存在重复或无效的任务导入标记，请先核对已有对象。");
        string? sha = matches[0].Attributes.GetUserString("Lux3D.AssetSha256");
        if (string.IsNullOrWhiteSpace(sha)) throw new InvalidOperationException("文档已有导入对象，但资产校验标记缺失；未重复插入。");
        var receipt = new ImportReceipt(importId, matches[0].Id, Session(doc), sha, "Imported");
        _receipts[(doc.RuntimeSerialNumber, importId)] = receipt;
        return receipt;
    }
    public ImportReceipt InspectReceipt(RhinoDoc doc, Guid importId)
    {
        if (!_receipts.TryGetValue((doc.RuntimeSerialNumber, importId), out ImportReceipt? receipt))
            throw new InvalidOperationException("导入编号尚未提交。");
        RhinoObject? obj = doc.Objects.FindId(receipt.ObjectId);
        string missing = _deletedStates.TryGetValue((doc.RuntimeSerialNumber, receipt.ObjectId), out string? observed) ? observed : "Missing";
        string state = obj is not null && !obj.IsDeleted && obj.Attributes.GetUserString("Lux3D.ImportId") == importId.ToString("D")
            ? "Imported" : missing;
        return receipt with { State = state };
    }
    public ImportReceipt Commit(PreparedAsset asset, PlacementIntent placement, Guid importId, Guid? generationOperationId = null)
    {
        RequireUiThread();
        if (importId == Guid.Empty) throw new ArgumentException("导入编号不能为空。", nameof(importId));
        RhinoDoc doc = RhinoDoc.FromRuntimeSerialNumber(asset.DocumentSerial) ?? throw new InvalidOperationException("目标文档已关闭。");
        if (Session(doc) != asset.DocumentSessionId) throw new InvalidOperationException("目标文档会话已失效。");
        if (doc.ModelUnitSystem != asset.Staging.ModelUnitSystem) throw new InvalidOperationException("目标文档单位已改变，请重新预览。");
        Transform plannedTransform = placement.ToTransform(asset.Audit.Bounds);
        string fingerprint = asset.Sha256 + "|" + string.Join("|", Enumerable.Range(0, 4).SelectMany(r => Enumerable.Range(0, 4).Select(c => plannedTransform[r, c].ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        if (_fingerprints.TryGetValue((doc.RuntimeSerialNumber, importId), out string? previous))
        {
            if (previous != fingerprint) throw new InvalidOperationException("相同导入编号对应了不同资产或放置参数。");
            if (_receipts.ContainsKey((doc.RuntimeSerialNumber, importId))) return InspectReceipt(doc, importId);
            throw new InvalidOperationException("此导入已失败，不能自动重放；请核对文档后明确建立新导入。");
        }
        if (doc.UndoRecordingIsActive) throw new InvalidOperationException("请等待当前 Rhino 命令结束后接受候选。");
        asset.Verify();
        string durableImportPath = PersistImportSnapshot(asset);
        Transform transform = placement.ToTransform(asset.Audit.Bounds);
        HashSet<Guid> before = Objects(doc).Select(o => o.Id).ToHashSet();
        string beforeState = DocumentState(doc);
        uint undo = doc.BeginUndoRecord("接受 Lux3D 候选");
        if (undo == 0) throw new InvalidOperationException("无法创建撤销记录，未导入。");
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
            if (definitionIndex < 0) throw new InvalidOperationException("创建 Lux3D Block 失败。");
            if (!doc.Import(durableImportPath, new ArchivableDictionary())) throw new InvalidOperationException("Rhino 导入失败。");
            VerifyHash(durableImportPath, asset.Sha256);
            asset.Verify();
            RhinoObject[] added = Objects(doc).Where(o => !before.Contains(o.Id)).ToArray();
            AssetAudit imported = Audit(added);
            if (imported.Meshes != asset.Audit.Meshes || imported.Vertices != asset.Audit.Vertices || imported.Triangles != asset.Audit.Triangles ||
                !imported.Bounds.Min.EpsilonEquals(asset.Audit.Bounds.Min, doc.ModelAbsoluteTolerance) ||
                !imported.Bounds.Max.EpsilonEquals(asset.Audit.Bounds.Max, doc.ModelAbsoluteTolerance))
                throw new InvalidOperationException("导入几何与预览结果不一致。");
            foreach (RhinoObject obj in added)
            {
                geometry.Add(obj.Geometry.Duplicate());
                ObjectAttributes attr = obj.Attributes.Duplicate();
                attr.SetUserString("Lux3D.AssetSha256", asset.Sha256);
                attributes.Add(attr);
            }
            if (!doc.InstanceDefinitions.ModifyGeometry(definitionIndex, geometry, attributes)) throw new InvalidOperationException("写入 Lux3D Block 几何失败。");
            ObjectAttributes instanceAttributes = new() { Name = "Lux3D" };
            instanceAttributes.SetUserString("Lux3D.ImportId", importId.ToString("D"));
            instanceAttributes.SetUserString("Lux3D.AssetSha256", asset.Sha256);
            instanceAttributes.SetUserString("Lux3D.DocumentSession", asset.DocumentSessionId);
            if (generationOperationId.HasValue) instanceAttributes.SetUserString("Lux3D.GenerationOperationId", generationOperationId.Value.ToString("D"));
            instanceId = doc.Objects.AddInstanceObject(definitionIndex, transform, instanceAttributes);
            if (instanceId == Guid.Empty) throw new InvalidOperationException("放置 Lux3D Block 失败。");
            if (doc.Objects.Delete(added.Select(o => o.Id), true) != added.Length) throw new InvalidOperationException("无法移除本次导入的临时顶层对象。");
            if (Objects(doc).Count(o => !before.Contains(o.Id)) != 1) throw new InvalidOperationException("导入生成了非预期顶层对象。");
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
            // The record was started here and no concurrent UI commands can run inside this synchronous block.
            bool restored = DocumentState(doc) == beforeState;
            if (!restored && undoEnded && doc.Undo())
            {
                doc.ClearRedoRecords();
                restored = DocumentState(doc) == beforeState;
            }
            if (!restored) throw new InvalidOperationException("导入失败，自动回滚未能确认；请检查文档 Undo。", failure);
            throw new InvalidOperationException("导入失败；本次文档修改已撤销。" + failure.Message, failure);
        }
        if (!undoEnded) throw new InvalidOperationException("导入对象已建立，但无法确认撤销记录；请检查文档，不要重复导入。");
        ImportReceipt receipt = new(importId, instanceId, asset.DocumentSessionId, asset.Sha256, "Imported");
        _receipts.Add((doc.RuntimeSerialNumber, importId), receipt);
        try { doc.Views.Redraw(); }
        catch { /* View refresh cannot turn a completed document import into a failed import. */ }
        return receipt;
    }
    private string PersistImportSnapshot(PreparedAsset asset)
    {
        // Final native import must not depend on a candidate's disposable preview directory.
        string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("无法定位本机持久资产目录。");
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
            throw new InvalidOperationException("持久导入资产校验失败；没有修改目标文档。");
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
                    InstanceDefinition definition = instance.InstanceDefinition ?? throw new InvalidOperationException("GLB Block 引用了不存在的定义。");
                    if (!ancestry.Add(definition.Id)) throw new InvalidOperationException("GLB Block 存在循环引用。");
                    Transform combined = frame.Transform * instance.InstanceXform;
                    if (!combined.IsValid) throw new InvalidOperationException("GLB Block 变换无效。");
                    pending.Push((((IEnumerable<RhinoObject>)definition.GetObjects()).GetEnumerator(), combined, definition.Id));
                }
                else throw new InvalidOperationException("GLB 含不支持的非 Mesh 对象。");
            }
        }
        finally { while (pending.Count > 0) pending.Pop().Objects.Dispose(); }
    }
    internal static AssetAudit Audit(IEnumerable<RhinoObject> objects)
    {
        long count = 0, vertices = 0, triangles = 0;
        BoundingBox bounds = BoundingBox.Empty;
        HashSet<Guid> materials = new();
        HashSet<string> textures = new(StringComparer.Ordinal);
        foreach (var (mesh, transform, source) in Meshes(objects, Transform.Identity))
        {
            if (!mesh.IsValid) throw new InvalidOperationException("GLB Mesh 无效。");
            count++;
            vertices = checked(vertices + mesh.Vertices.Count);
            triangles = checked(triangles + mesh.Faces.Sum(f => f.IsQuad ? 2L : 1L));
            bounds.Union(mesh.GetBoundingBox(transform));
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
        if (count == 0 || !bounds.IsValid) throw new InvalidOperationException("GLB 没有可导入的有效网格。");
        return new AssetAudit(count, vertices, triangles, materials.Count, textures.Count, bounds);
    }
}

/// <summary>Conduit preview never adds document objects or undo records. It previews shape/base color, not full PBR.</summary>
public sealed class CandidatePreview : DisplayConduit, IDisposable
{
    private readonly List<(Mesh Mesh, DisplayMaterial Material)> _items = new();
    private BoundingBox _bounds = BoundingBox.Empty;
    private uint _documentSerial;
    public void Show(PreparedAsset asset, PlacementIntent placement)
    {
        Clear();
        asset.Verify();
        Transform placementTransform = placement.ToTransform(asset.Audit.Bounds);
        _documentSerial = asset.DocumentSerial;
        foreach (var (source, transform, obj) in Lux3DHostService.Meshes(Lux3DHostService.Objects(asset.Staging), Transform.Identity))
        {
            Mesh mesh = source.DuplicateMesh();
            mesh.Transform(placementTransform * transform);
            _bounds.Union(mesh.GetBoundingBox(true));
            _items.Add((mesh, new DisplayMaterial(obj.GetMaterial(true))));
        }
        Enabled = true;
        RhinoDoc.FromRuntimeSerialNumber(_documentSerial)?.Views.Redraw();
    }
    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (e.RhinoDoc?.RuntimeSerialNumber == _documentSerial) e.IncludeBoundingBox(_bounds);
    }
    protected override void PostDrawObjects(DrawEventArgs e)
    {
        if (e.RhinoDoc?.RuntimeSerialNumber != _documentSerial) return;
        foreach (var item in _items) e.Display.DrawMeshShaded(item.Mesh, item.Material);
    }
    public void Clear()
    {
        Enabled = false;
        foreach (var item in _items) { item.Mesh.Dispose(); item.Material.Dispose(); }
        _items.Clear(); _bounds = BoundingBox.Empty;
        RhinoDoc.FromRuntimeSerialNumber(_documentSerial)?.Views.Redraw();
    }
    public void Dispose() { Clear(); GC.SuppressFinalize(this); }
}

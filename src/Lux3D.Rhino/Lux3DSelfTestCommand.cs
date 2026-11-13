using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.Render;
using RhinoDoc = global::Rhino.RhinoDoc;

namespace Lux3D.Rhino;

[Guid("35EB39B2-683C-4D20-A9A7-593D7830CE0F")]
public sealed class Lux3DSelfTestCommand : Command
{
    public override string EnglishName => "Lux3DSelfTest";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        using var fixtureGet = new GetString();
        fixtureGet.SetCommandPrompt(global::Lux3D.Core.LocalText.Get("Absolute path to the Lux3D asymmetric GLB fixture"));
        if (fixtureGet.Get() != global::Rhino.Input.GetResult.String) return Result.Cancel;
        using var outputGet = new GetString();
        outputGet.SetCommandPrompt(global::Lux3D.Core.LocalText.Get("Output directory for redacted acceptance JSON and test .3dm (a new subfolder is created; existing files are not overwritten)"));
        if (outputGet.Get() != global::Rhino.Input.GetResult.String) return Result.Cancel;
        try
        {
            string report = Lux3DSelfTest.Run(fixtureGet.StringResult(), outputGet.StringResult());
            using JsonDocument result = JsonDocument.Parse(File.ReadAllText(report));
            bool passed = result.RootElement.GetProperty("automaticChecksPassed").GetBoolean();
            global::Rhino.RhinoApp.WriteLine(global::Lux3D.Core.LocalText.Get("Lux3D self-test report: ") + report);
            global::Rhino.RhinoApp.WriteLine(passed ? global::Lux3D.Core.LocalText.Get("Automated checks passed; manual P0 acceptance is still pending.") : global::Lux3D.Core.LocalText.Get("Automated checks failed. Inspect the report; this candidate has not passed acceptance."));
            return passed ? Result.Success : Result.Failure;
        }
        catch (Exception ex)
        {
            global::Rhino.RhinoApp.WriteLine(global::Lux3D.Core.LocalText.Get("Lux3D self-test did not complete: ") + ex.Message);
            return Result.Failure;
        }
    }
}

/// <summary>Run inside Rhino. Creates only disposable headless documents and explicitly requested new evidence files.</summary>
public static class Lux3DSelfTest
{
    private sealed record Check(string Name, bool Passed, object? Evidence, string? Error);
    public static string Run(string fixturePath, string outputDirectory)
    {
        if (!Path.IsPathRooted(fixturePath) || !Path.IsPathRooted(outputDirectory)) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Fixture and output directories must be absolute paths."));
        byte[] fixture = File.ReadAllBytes(fixturePath);
        Lux3D.Core.GlbContainerValidator.Validate(fixture);
        string sha = Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant();
        bool requiresTextures = sha == "54b0beb3e3248667cd24aa3afc97373b94cb22ac4101fb2663ffc414f1fc8382";
        bool canonicalFixture = sha is "54b0beb3e3248667cd24aa3afc97373b94cb22ac4101fb2663ffc414f1fc8382" or "97d19134ec64335bd18bf634092d7af21d4e6de3554a85683f294c97dc7ba131";
        if (!canonicalFixture) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("This deterministic test only accepts the repository's fixed-SHA-256 asymmetric fixture. Load ordinary GLB files from the panel."));
        string directory = Path.Combine(outputDirectory, "lux3d-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        List<Check> checks = new();
        List<object> evidence = new();
        CheckValue(checks, "mesh-repair-copy-preserves-source-uv-colors", VerifyMeshRepair);
        using var host = new Lux3DHostService(directory);
        foreach (global::Rhino.UnitSystem units in new[] { global::Rhino.UnitSystem.Meters, global::Rhino.UnitSystem.Millimeters })
        {
            string label = units.ToString();
            try
            {
                using RhinoDoc document = RhinoDoc.CreateHeadless(null);
                document.ModelUnitSystem = units;
                document.ModelAbsoluteTolerance = units == global::Rhino.UnitSystem.Meters ? 0.000001 : 0.001;
                document.UndoRecordingEnabled = true;
                double height = units == global::Rhino.UnitSystem.Meters ? 1 : 1000;
                double tolerance = Math.Max(document.ModelAbsoluteTolerance, 1e-6 * height);
                using PreparedAsset asset = host.Prepare(fixturePath, sha, document);
                double scale = global::Rhino.RhinoMath.UnitScale(global::Rhino.UnitSystem.Meters, units);
                BoundingBox bounds = asset.Audit.Bounds;
                CheckValue(checks, label + "/native-axis-units-nodes", () =>
                {
                    Require(bounds.Min.DistanceTo(new Point3d(11, -33, 22) * scale) <= tolerance &&
                        bounds.Max.DistanceTo(new Point3d(11.5, -29, 25) * scale) <= tolerance,
                        global::Lux3D.Core.LocalText.Get("Rhino's native node/axis/unit import differs from the expected glTF (x,-z,y) meters-to-document mapping. This check must not be reported as passed."));
                    return new { minimum = Coordinates(bounds.Min), maximum = Coordinates(bounds.Max), tolerance };
                });
                CheckValue(checks, label + "/triangle-count", () =>
                {
                    Require(asset.Audit.Triangles == 8 && asset.Audit.Meshes > 0, global::Lux3D.Core.LocalText.Get("The fixture must retain 8 triangles."));
                    return new { asset.Audit.Meshes, asset.Audit.Vertices, asset.Audit.Triangles };
                });
                Plane[] planes =
                {
                    Plane.WorldXY,
                    new(Point3d.Origin, Vector3d.YAxis, -Vector3d.XAxis),
                    new(Point3d.Origin, new Vector3d(1, 1, 0), new Vector3d(-1, 1, 2)),
                };
                foreach (var (plane, index) in planes.Select((p, i) => (p, i)))
                foreach (double angle in new[] { 0.0, 37.0 })
                {
                    var placement = new PlacementIntent(plane, new Point3d(7 * height, 11 * height, 13 * height), height, angle);
                    CheckValue(checks, label + $"/placement-plane{index}-angle{angle}", () => VerifyPlacement(asset, placement, tolerance));
                }
                var intent = new PlacementIntent(Plane.WorldXY, new Point3d(2 * height, -3 * height, height), height, 37);
                CheckValue(checks, label + "/preview-cache-transform-lifecycle", () => VerifyPreview(document, asset, intent, tolerance));
                CheckValue(checks, label + "/prepared-asset-close-during-use", () =>
                {
                    using PreparedAsset retained = host.Prepare(fixturePath, sha, document);
                    string snapshot = retained.SnapshotPath;
                    using (IDisposable use = retained.Retain())
                    {
                        retained.Dispose(); // Simulate panel cleanup during native import.
                        retained.Verify();
                        Require(File.Exists(snapshot), "Panel cleanup removed an in-use import snapshot.");
                        bool refusedNewUse = false;
                        try { using IDisposable unexpected = retained.Retain(); }
                        catch (ObjectDisposedException) { refusedNewUse = true; }
                        Require(refusedNewUse, "A closing candidate accepted a new import.");
                    }
                    Require(!File.Exists(snapshot), "The import snapshot was not released after its final user finished.");
                    retained.Dispose();
                    return new { retainedDuringImport = true, releasedAfterImport = true };
                });
                CheckValue(checks, label + "/reject-nested-undo-before-mutation", () =>
                {
                    Guid[] before = Lux3DHostService.Objects(document).Select(o => o.Id).ToArray();
                    uint undo = document.BeginUndoRecord("Lux3D self-test guarded import");
                    Require(undo != 0, "Could not start the guard-test undo record.");
                    bool rejected = false;
                    try
                    {
                        try { host.Commit(asset, intent, Guid.NewGuid()); }
                        catch (InvalidOperationException) { rejected = true; }
                    }
                    finally { document.EndUndoRecord(undo); }
                    Require(rejected && before.SequenceEqual(Lux3DHostService.Objects(document).Select(o => o.Id)),
                        "Import during an existing undo record was not rejected without mutation.");
                    return new { rejectedBeforeMutation = true };
                });
                Guid importId = Guid.NewGuid();
                ImportReceipt receipt = host.Commit(asset, intent, importId);
                CheckValue(checks, label + "/one-block-and-replay", () =>
                {
                    Require(Lux3DHostService.Objects(document).Length == 1, global::Lux3D.Core.LocalText.Get("Accepting a candidate must create exactly one top-level block."));
                    Require(document.Objects.FindId(receipt.ObjectId) is InstanceObject, global::Lux3D.Core.LocalText.Get("The result is not a block."));
                    Require(host.Commit(asset, intent, importId).ObjectId == receipt.ObjectId && Lux3DHostService.Objects(document).Length == 1, global::Lux3D.Core.LocalText.Get("Replaying the import ID created duplicate objects."));
                    return new { receipt.ObjectId, receipt.State };
                });
                CheckValue(checks, label + "/undo-replay-redo", () =>
                {
                    Require(document.Undo(), global::Lux3D.Core.LocalText.Get("One Undo failed."));
                    Require(Lux3DHostService.Objects(document).Length == 0, global::Lux3D.Core.LocalText.Get("One Undo did not reverse candidate acceptance."));
                    ImportReceipt undone = host.Commit(asset, intent, importId);
                    Require(undone.State == "Undone", global::Lux3D.Core.LocalText.Get("Expected Undone receipt state after Undo; got ") + undone.State);
                    Require(Lux3DHostService.Objects(document).Length == 0, global::Lux3D.Core.LocalText.Get("Replay after Undo automatically inserted the object again."));
                    Require(document.Redo(), global::Lux3D.Core.LocalText.Get("Redo failed."));
                    Require(host.InspectReceipt(document, importId).State == "Imported" && Lux3DHostService.Objects(document).Length == 1, global::Lux3D.Core.LocalText.Get("Redo did not restore the same object."));
                    return new { undo = undone.State, redo = host.InspectReceipt(document, importId).State };
                });
                object materialBefore = MaterialEvidence(Lux3DHostService.Objects(document));
                string saved = Path.Combine(directory, label + ".3dm");
                CheckValue(checks, label + "/save-reopen", () =>
                {
                    int resolvedTexturesBefore = VerifyMaterialResources(materialBefore, requiresTextures);
                    using var options = new FileWriteOptions { IncludeBitmapTable = true, IncludeRenderMeshes = true,
                        SuppressDialogBoxes = true, SuppressAllInput = true, UpdateDocumentPath = false, WriteUserData = true };
                    Require(document.Write3dmFile(saved, options), global::Lux3D.Core.LocalText.Get("Could not write the test 3dm."));
                    string movedDirectory = Path.Combine(directory, "copied");
                    Directory.CreateDirectory(movedDirectory);
                    string moved = Path.Combine(movedDirectory, label + ".3dm");
                    File.Copy(saved, moved, overwrite: false);
                    using RhinoDoc reopened = RhinoDoc.OpenHeadless(moved);
                    Require(reopened is not null, global::Lux3D.Core.LocalText.Get("Could not reopen the test 3dm."));
                    RhinoObject[] objects = Lux3DHostService.Objects(reopened!);
                    Require(objects.Length == 1 && objects[0] is InstanceObject, global::Lux3D.Core.LocalText.Get("The reopened document did not retain a single block."));
                    AssetAudit audit = Lux3DHostService.Audit(objects);
                    Require(audit.Triangles == asset.Audit.Triangles, global::Lux3D.Core.LocalText.Get("Triangle count changed after saving and reopening."));
                    object after = MaterialEvidence(objects);
                    int resolvedTexturesAfter = VerifyMaterialResources(after, requiresTextures);
                    string first = JsonSerializer.Serialize(materialBefore), second = JsonSerializer.Serialize(after);
                    Require(first == second, global::Lux3D.Core.LocalText.Get("Material/texture evidence differs after reopening."));
                    return new { file = Path.GetFileName(moved), audit.Triangles, material = after,
                        resolvedTexturesBefore, resolvedTexturesAfter,
                        crossProcessMigrationVerified = false, renderedVisualVerified = false };
                });
                CheckValue(checks, label + "/manual-delete-receipt", () =>
                {
                    uint undo = document.BeginUndoRecord("Lux3D self-test delete");
                    try { Require(document.Objects.Delete(receipt.ObjectId, true), global::Lux3D.Core.LocalText.Get("Test deletion failed.")); }
                    finally { if (undo != 0) document.EndUndoRecord(undo); }
                    Require(host.InspectReceipt(document, importId).State == "Missing", global::Lux3D.Core.LocalText.Get("Manual deletion must report Missing."));
                    Require(host.Commit(asset, intent, importId).State == "Missing" && Lux3DHostService.Objects(document).Length == 0, global::Lux3D.Core.LocalText.Get("Replay after deletion must not insert the object again."));
                    return new { state = "Missing" };
                });
                evidence.Add(new { units = label, tolerance, nativeBounds = new { min = Coordinates(bounds.Min), max = Coordinates(bounds.Max) }, materials = materialBefore });
            }
            catch (Exception ex) { checks.Add(new Check(label + "/execution", false, null, ex.Message)); }
        }
        string report = Path.Combine(directory, "report.json");
        var payload = new
        {
            schemaVersion = 1, createdUtc = DateTimeOffset.UtcNow, rhinoVersion = global::Rhino.RhinoApp.Version.ToString(),
            pluginVersion = Lux3DPlugin.DisplayVersion, fixture = Path.GetFileName(fixturePath), fixtureSha256 = sha,
            automaticChecksPassed = checks.All(c => c.Passed), fullP0Acceptance = false,
            remaining = new[] { global::Lux3D.Core.LocalText.Get("Real Lux3D text/photo generation"), global::Lux3D.Core.LocalText.Get("Actual OAuth user authorization and permissions"), global::Lux3D.Core.LocalText.Get("Rendered appearance and each PBR channel mapping"), global::Lux3D.Core.LocalText.Get("Reopen after closing Rhino without the original texture cache"), global::Lux3D.Core.LocalText.Get("Switch active documents A/B and verify plugin coexistence") },
            checks, evidence,
        };
        File.WriteAllText(report, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }
    private static void CheckValue(List<Check> checks, string name, Func<object> action)
    {
        try { checks.Add(new Check(name, true, action(), null)); }
        catch (Exception ex) { checks.Add(new Check(name, false, null, ex.Message)); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static double[] Coordinates(Point3d p) => new[] { p.X, p.Y, p.Z };
    private static object VerifyMeshRepair()
    {
        using var source = new Mesh();
        foreach (Point3d point in new[] { Point3d.Origin, new Point3d(1, 0, 0), new Point3d(0, 1, 0), Point3d.Origin, new Point3d(1, -1, 0) })
            source.Vertices.Add(point);
        source.Faces.AddFace(0, 1, 2);
        source.Faces.AddFace(3, 4, 1);
        source.Faces.AddFace(0, 3, 1); // Different indices, coincident positions: a collapsed triangle.
        for (int i = 0; i < source.Vertices.Count; i++)
        {
            source.Normals.Add(0, 0, 0);
            source.TextureCoordinates.Add(i / 4f, i / 8f);
            source.VertexColors.Add(System.Drawing.Color.FromArgb(255, 10 + i, 20 + i, 30 + i));
        }
        Require(!source.IsValid, global::Lux3D.Core.LocalText.Get("The repair fixture must contain invalid normals and degenerate faces."));
        using Mesh repaired = Lux3DHostService.RepairMeshCopy(source, out int removed);
        Require(removed == 1 && repaired.Faces.Count == 2 && repaired.IsValid, global::Lux3D.Core.LocalText.Get("Limited repair did not produce the two valid original triangles."));
        Require(source.Faces.Count == 3 && !source.IsValid, global::Lux3D.Core.LocalText.Get("Repair modified the original mesh."));
        for (int i = 0; i < source.Vertices.Count; i++)
        {
            Require(source.Vertices[i].Equals(repaired.Vertices[i]), global::Lux3D.Core.LocalText.Get("Repair changed vertex positions."));
            Require(source.TextureCoordinates[i].Equals(repaired.TextureCoordinates[i]), global::Lux3D.Core.LocalText.Get("Repair changed UVs."));
            Require(source.VertexColors[i].Equals(repaired.VertexColors[i]), global::Lux3D.Core.LocalText.Get("Repair changed vertex colors."));
        }
        using Mesh unchanged = Lux3DHostService.RepairMeshCopy(repaired, out int validRemoved);
        Require(validRemoved == 0 && unchanged.DataCRC(0) == repaired.DataCRC(0), global::Lux3D.Core.LocalText.Get("A valid mesh must not be recomputed or simplified."));
        return new { removedDegenerateFaces = removed, vertices = repaired.Vertices.Count, triangles = repaired.Faces.Count, preservedSource = true, preservedUvAndColors = true };
    }
    private static object VerifyPreview(RhinoDoc document, PreparedAsset asset, PlacementIntent intent, double tolerance)
    {
        Guid[] ids = Lux3DHostService.Objects(document).Select(obj => obj.Id).ToArray();
        uint undoNumber = document.NextUndoRecordSerialNumber;
        Mesh[] sourceMeshes = Lux3DHostService.Meshes(Lux3DHostService.Objects(asset.Staging), Transform.Identity)
            .Select(item => item.Mesh).ToArray();
        uint[] sourceCrcs = sourceMeshes.Select(mesh => mesh.DataCRC(0)).ToArray();
        using var preview = new CandidatePreview();
        preview.Show(asset, intent);
        CandidatePreview.PreviewState initial = preview.CaptureState();
        Require(initial.Visible && initial.GeometryCache is not null && initial.MeshCount > 0, global::Lux3D.Core.LocalText.Get("The initial preview did not create a visible cache."));

        // Only move the disposable prepared snapshot, never the user's fixture.
        // Successful slider updates now prove that they do not reopen/hash it.
        string parkedSnapshot = asset.SnapshotPath + ".preview-test-" + Guid.NewGuid().ToString("N");
        File.Move(asset.SnapshotPath, parkedSnapshot);
        double maximumBoundsError = 0;
        try
        {
            for (int index = 0; index < 20; index++)
            {
                PlacementIntent next = intent with
                {
                    Height = intent.Height * (1 + index * 0.05),
                    RotationDegrees = index % 2 == 0 ? 37 : -22,
                    Plane = index % 2 == 0 ? Plane.WorldXY : new Plane(Point3d.Origin, new Vector3d(1, 1, 0), new Vector3d(-1, 1, 2))
                };
                preview.Show(asset, next);
                CandidatePreview.PreviewState state = preview.CaptureState();
                Require(ReferenceEquals(initial.GeometryCache, state.GeometryCache) && state.MeshCount == initial.MeshCount,
                    global::Lux3D.Core.LocalText.Get("Changing placement alone recreated the preview mesh or material cache."));
                Require(state.DocumentSerial == asset.DocumentSerial, global::Lux3D.Core.LocalText.Get("The preview lost its target-document filter."));
                Transform expected = next.ToTransform(asset.Audit.Bounds);
                for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    Require(Math.Abs(state.ModelTransform[row, column] - expected[row, column]) <= 1e-12,
                        global::Lux3D.Core.LocalText.Get("The preview draw matrix differs from the placement intent."));
                Point3d[] corners = state.SourceBounds.GetCorners();
                for (int corner = 0; corner < corners.Length; corner++) corners[corner].Transform(expected);
                BoundingBox expectedBounds = new(corners);
                maximumBoundsError = Math.Max(maximumBoundsError, Math.Max(
                    expectedBounds.Min.DistanceTo(state.Bounds.Min), expectedBounds.Max.DistanceTo(state.Bounds.Max)));
            }
            Require(maximumBoundsError <= tolerance, global::Lux3D.Core.LocalText.Get("The transformed preview bounds do not contain all source bounding-box corners."));
            using var uncachedPreview = new CandidatePreview();
            bool rejectedMissingSnapshot = false;
            try { uncachedPreview.Show(asset, intent); }
            catch (InvalidOperationException) { rejectedMissingSnapshot = true; }
            Require(rejectedMissingSnapshot && !uncachedPreview.Enabled && uncachedPreview.CaptureState().GeometryCache is null,
                global::Lux3D.Core.LocalText.Get("Initial preview loading did not detect the missing snapshot, or left resources after failure."));
        }
        finally { File.Move(parkedSnapshot, asset.SnapshotPath); }

        preview.Clear();
        preview.Clear();
        CandidatePreview.PreviewState cleared = preview.CaptureState();
        Require(!preview.Enabled && !cleared.Visible && cleared.GeometryCache is null && cleared.MeshCount == 0 && !cleared.Bounds.IsValid,
            global::Lux3D.Core.LocalText.Get("Clear did not release the cache, clear the bounds or disable display."));
        preview.Show(asset, intent);
        Require(!ReferenceEquals(initial.GeometryCache, preview.CaptureState().GeometryCache), global::Lux3D.Core.LocalText.Get("A released cache was incorrectly reused after Clear."));
        preview.Dispose();
        preview.Dispose();
        CandidatePreview.PreviewState disposed = preview.CaptureState();
        Require(disposed.Disposed && !disposed.Visible && disposed.GeometryCache is null, global::Lux3D.Core.LocalText.Get("Dispose did not release preview resources."));
        bool rejectedAfterDispose = false;
        try { preview.Show(asset, intent); }
        catch (ObjectDisposedException) { rejectedAfterDispose = true; }
        Require(rejectedAfterDispose, global::Lux3D.Core.LocalText.Get("A disposed preview must not be enabled again."));
        Require(sourceCrcs.SequenceEqual(sourceMeshes.Select(mesh => mesh.DataCRC(0))), global::Lux3D.Core.LocalText.Get("Preview changed the source mesh, normals or UVs."));
        Require(ids.SequenceEqual(Lux3DHostService.Objects(document).Select(obj => obj.Id)), global::Lux3D.Core.LocalText.Get("Preview changed document objects."));
        Require(document.NextUndoRecordSerialNumber == undoNumber, global::Lux3D.Core.LocalText.Get("Preview changed the Undo sequence."));
        return new { updates = 20, cacheReused = true, sourceUnchanged = true, lifecycleVerified = true,
            transformUpdatesWithoutSnapshotIo = true, maximumBoundsError, tolerance, generationRequests = 0 };
    }

    private static object VerifyPlacement(PreparedAsset asset, PlacementIntent placement, double tolerance)
    {
        BoundingBox b = asset.Audit.Bounds;
        Point3d anchor = new((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2, b.Min.Z);
        Transform transform = placement.ToTransform(b);
        Point3d mapped = anchor; mapped.Transform(transform);
        Require(mapped.DistanceTo(placement.Point) <= tolerance, global::Lux3D.Core.LocalText.Get("The bottom center did not reach the placement point."));
        double radians = placement.RotationDegrees * Math.PI / 180;
        double scale = placement.Height / (b.Max.Z - b.Min.Z);
        double maxError = 0;
        foreach (Point3d source in b.GetCorners())
        {
            Vector3d q = (source - anchor) * scale;
            double x = Math.Cos(radians) * q.X - Math.Sin(radians) * q.Y;
            double y = Math.Sin(radians) * q.X + Math.Cos(radians) * q.Y;
            Point3d expected = placement.Point + x * placement.Plane.XAxis + y * placement.Plane.YAxis + q.Z * placement.Plane.Normal;
            Point3d actual = source; actual.Transform(transform);
            maxError = Math.Max(maxError, actual.DistanceTo(expected));
        }
        Require(maxError <= tolerance, global::Lux3D.Core.LocalText.Get("The transform differs from the expected full U/V/N plane basis."));
        return new { maxError, tolerance, height = placement.Height, angle = placement.RotationDegrees };
    }
    private static int VerifyMaterialResources(object evidence, bool requiresTextures)
    {
        // Equality alone lets missing-before == missing-after pass. This checks
        // resolvable resources, not the channel mapping or Rendered appearance.
        int count = CountResolvedTextures(JsonSerializer.SerializeToElement(evidence));
        Require(!requiresTextures || count > 0,
            global::Lux3D.Core.LocalText.Get("The PBR fixture has no resolvable texture evidence. Material acceptance remains incomplete."));
        return count;
    }
    private static int CountResolvedTextures(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Sum(CountResolvedTextures);
        if (value.ValueKind != JsonValueKind.Object) return 0;
        int count = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name is "sha256" or "textureSha256")
            {
                // Null denotes no file-backed resource, e.g. a procedural material.
                if (property.Value.ValueKind == JsonValueKind.Null) continue;
                string? hash = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                Require(hash is not null && hash.Length == 64 && hash.All(Uri.IsHexDigit),
                    global::Lux3D.Core.LocalText.Get("A material texture resource is missing or unresolved. Equal missing resources do not pass save/reopen verification."));
                count++;
            }
            else count += CountResolvedTextures(property.Value);
        }
        return count;
    }
    private static object MaterialEvidence(IEnumerable<RhinoObject> roots)
    {
        var items = new List<object>();
        foreach (var (_, _, source) in Lux3DHostService.Meshes(roots, Transform.Identity))
        {
            Material m = source.GetMaterial(true);
            var textures = m.GetTextures().Select(t => new { type = t.TextureType.ToString(), enabled = t.Enabled,
                sha256 = File.Exists(t.FileName) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(t.FileName))) : "missing" }).OrderBy(t => t.type).ThenBy(t => t.sha256).ToArray();
            items.Add(new { pbr = m.IsPhysicallyBased, metallic = m.IsPhysicallyBased ? m.PhysicallyBased.Metallic : (double?)null,
                roughness = m.IsPhysicallyBased ? m.PhysicallyBased.Roughness : (double?)null,
                textures, render = RenderEvidence(source.GetRenderMaterial(true), 0) });
        }
        return items;
    }
    private static object? RenderEvidence(RenderContent? content, int depth)
    {
        if (content is null) return null;
        if (depth > 32) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Material node nesting is too deep."));
        var children = new List<object?>();
        for (RenderContent? child = content.FirstChild; child is not null; child = child.NextSibling) children.Add(RenderEvidence(child, depth + 1));
        string? sha = string.IsNullOrEmpty(content.Filename) ? null : File.Exists(content.Filename)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(content.Filename))) : "missing";
        return new { typeId = content.TypeId, slot = content.ChildSlotName, textureSha256 = sha, children };
    }
}

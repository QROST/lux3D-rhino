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
        fixtureGet.SetCommandPrompt("Lux3D 非对称 GLB 夹具的绝对路径");
        if (fixtureGet.Get() != global::Rhino.Input.GetResult.String) return Result.Cancel;
        using var outputGet = new GetString();
        outputGet.SetCommandPrompt("脱敏验收 JSON 和测试 .3dm 的输出目录（新建子目录，不覆盖现有文件）");
        if (outputGet.Get() != global::Rhino.Input.GetResult.String) return Result.Cancel;
        try
        {
            string report = Lux3DSelfTest.Run(fixtureGet.StringResult(), outputGet.StringResult());
            using JsonDocument result = JsonDocument.Parse(File.ReadAllText(report));
            bool passed = result.RootElement.GetProperty("automaticChecksPassed").GetBoolean();
            global::Rhino.RhinoApp.WriteLine("Lux3D 自测记录：" + report);
            global::Rhino.RhinoApp.WriteLine(passed ? "自动检查通过；人工 P0 验收仍未完成。" : "自动检查存在失败；请检查报告，当前候选未通过验收。");
            return passed ? Result.Success : Result.Failure;
        }
        catch (Exception ex)
        {
            global::Rhino.RhinoApp.WriteLine("Lux3D 自测未完成：" + ex.Message);
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
        if (!Path.IsPathRooted(fixturePath) || !Path.IsPathRooted(outputDirectory)) throw new ArgumentException("夹具和输出目录必须为绝对路径。");
        byte[] fixture = File.ReadAllBytes(fixturePath);
        Lux3D.Core.GlbContainerValidator.Validate(fixture);
        string sha = Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant();
        bool canonicalFixture = sha is "54b0beb3e3248667cd24aa3afc97373b94cb22ac4101fb2663ffc414f1fc8382" or "97d19134ec64335bd18bf634092d7af21d4e6de3554a85683f294c97dc7ba131";
        if (!canonicalFixture) throw new InvalidOperationException("确定性自测仅接受本仓库固定 SHA-256 的非对称夹具；普通 GLB 请从面板加载。");
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
                        "Rhino 原生导入的节点/轴向/单位与 glTF (x,-z,y) 米→文档单位预期不符；禁止直接声明此项通过。");
                    return new { minimum = Coordinates(bounds.Min), maximum = Coordinates(bounds.Max), tolerance };
                });
                CheckValue(checks, label + "/triangle-count", () =>
                {
                    Require(asset.Audit.Triangles == 8 && asset.Audit.Meshes > 0, "夹具应保留 8 个三角形。");
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
                Guid importId = Guid.NewGuid();
                ImportReceipt receipt = host.Commit(asset, intent, importId);
                CheckValue(checks, label + "/one-block-and-replay", () =>
                {
                    Require(Lux3DHostService.Objects(document).Length == 1, "接受候选应仅创建一个顶层 Block。");
                    Require(document.Objects.FindId(receipt.ObjectId) is InstanceObject, "结果不是 Block。");
                    Require(host.Commit(asset, intent, importId).ObjectId == receipt.ObjectId && Lux3DHostService.Objects(document).Length == 1, "导入编号重放产生重复对象。");
                    return new { receipt.ObjectId, receipt.State };
                });
                CheckValue(checks, label + "/undo-replay-redo", () =>
                {
                    Require(document.Undo(), "一次 Undo 失败。");
                    Require(Lux3DHostService.Objects(document).Length == 0, "一次 Undo 未撤销接受候选。");
                    ImportReceipt undone = host.Commit(asset, intent, importId);
                    Require(undone.State == "Undone", "Undo 回执状态应为 Undone，实际 " + undone.State);
                    Require(Lux3DHostService.Objects(document).Length == 0, "Undo 后重放自动重新插入。");
                    Require(document.Redo(), "Redo 失败。");
                    Require(host.InspectReceipt(document, importId).State == "Imported" && Lux3DHostService.Objects(document).Length == 1, "Redo 未恢复同一对象。");
                    return new { undo = undone.State, redo = host.InspectReceipt(document, importId).State };
                });
                object materialBefore = MaterialEvidence(Lux3DHostService.Objects(document));
                string saved = Path.Combine(directory, label + ".3dm");
                CheckValue(checks, label + "/save-reopen", () =>
                {
                    using var options = new FileWriteOptions { IncludeBitmapTable = true, IncludeRenderMeshes = true,
                        SuppressDialogBoxes = true, SuppressAllInput = true, UpdateDocumentPath = false, WriteUserData = true };
                    Require(document.Write3dmFile(saved, options), "写入测试 3dm 失败。");
                    string movedDirectory = Path.Combine(directory, "copied");
                    Directory.CreateDirectory(movedDirectory);
                    string moved = Path.Combine(movedDirectory, label + ".3dm");
                    File.Copy(saved, moved, overwrite: false);
                    using RhinoDoc reopened = RhinoDoc.OpenHeadless(moved);
                    Require(reopened is not null, "重开测试 3dm 失败。");
                    RhinoObject[] objects = Lux3DHostService.Objects(reopened!);
                    Require(objects.Length == 1 && objects[0] is InstanceObject, "重开文档未保留单 Block。");
                    AssetAudit audit = Lux3DHostService.Audit(objects);
                    Require(audit.Triangles == asset.Audit.Triangles, "保存重开后三角形数量改变。");
                    object after = MaterialEvidence(objects);
                    string first = JsonSerializer.Serialize(materialBefore), second = JsonSerializer.Serialize(after);
                    Require(first == second, "重开后的材质/纹理证据不一致。");
                    return new { file = Path.GetFileName(moved), audit.Triangles, material = after,
                        crossProcessMigrationVerified = false, renderedVisualVerified = false };
                });
                CheckValue(checks, label + "/manual-delete-receipt", () =>
                {
                    uint undo = document.BeginUndoRecord("Lux3D self-test delete");
                    try { Require(document.Objects.Delete(receipt.ObjectId, true), "测试删除失败。"); }
                    finally { if (undo != 0) document.EndUndoRecord(undo); }
                    Require(host.InspectReceipt(document, importId).State == "Missing", "手工删除应报告 Missing。");
                    Require(host.Commit(asset, intent, importId).State == "Missing" && Lux3DHostService.Objects(document).Length == 0, "删除后重放不应重新插入。");
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
            remaining = new[] { "真实 Lux3D 文字/照片生成", "OAuth 用户实际授权及权限", "Rendered 视觉及 PBR 每个通道映射", "关闭 Rhino 后在无原纹理缓存环境重开", "活动文档 A/B 切换与双插件共存" },
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
        Require(!source.IsValid, "修复夹具必须包含无效法向及退化面。");
        using Mesh repaired = Lux3DHostService.RepairMeshCopy(source, out int removed);
        Require(removed == 1 && repaired.Faces.Count == 2 && repaired.IsValid, "有限修复没有得到有效的两个原有三角面。");
        Require(source.Faces.Count == 3 && !source.IsValid, "修复修改了原始网格。");
        for (int i = 0; i < source.Vertices.Count; i++)
        {
            Require(source.Vertices[i].Equals(repaired.Vertices[i]), "修复改变了顶点位置。");
            Require(source.TextureCoordinates[i].Equals(repaired.TextureCoordinates[i]), "修复改变了 UV。");
            Require(source.VertexColors[i].Equals(repaired.VertexColors[i]), "修复改变了顶点颜色。");
        }
        using Mesh unchanged = Lux3DHostService.RepairMeshCopy(repaired, out int validRemoved);
        Require(validRemoved == 0 && unchanged.DataCRC(0) == repaired.DataCRC(0), "有效网格不应重新计算或简化。");
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
        Require(initial.Visible && initial.GeometryCache is not null && initial.MeshCount > 0, "首次预览没有建立可见缓存。");

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
                    "只改变放置参数时重新创建了预览网格或材质缓存。");
                Require(state.DocumentSerial == asset.DocumentSerial, "预览失去了目标文档过滤。");
                Transform expected = next.ToTransform(asset.Audit.Bounds);
                for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    Require(Math.Abs(state.ModelTransform[row, column] - expected[row, column]) <= 1e-12,
                        "预览绘制矩阵不符合放置意图。");
                Point3d[] corners = state.SourceBounds.GetCorners();
                for (int corner = 0; corner < corners.Length; corner++) corners[corner].Transform(expected);
                BoundingBox expectedBounds = new(corners);
                maximumBoundsError = Math.Max(maximumBoundsError, Math.Max(
                    expectedBounds.Min.DistanceTo(state.Bounds.Min), expectedBounds.Max.DistanceTo(state.Bounds.Max)));
            }
            Require(maximumBoundsError <= tolerance, "变换后的预览包围盒不包含源包围盒的全部角点。");
            using var uncachedPreview = new CandidatePreview();
            bool rejectedMissingSnapshot = false;
            try { uncachedPreview.Show(asset, intent); }
            catch (InvalidOperationException) { rejectedMissingSnapshot = true; }
            Require(rejectedMissingSnapshot && !uncachedPreview.Enabled && uncachedPreview.CaptureState().GeometryCache is null,
                "首次加载预览没有校验缺失快照，或失败后遗留资源。");
        }
        finally { File.Move(parkedSnapshot, asset.SnapshotPath); }

        preview.Clear();
        preview.Clear();
        CandidatePreview.PreviewState cleared = preview.CaptureState();
        Require(!preview.Enabled && !cleared.Visible && cleared.GeometryCache is null && cleared.MeshCount == 0 && !cleared.Bounds.IsValid,
            "Clear 没有释放缓存、清除包围盒或关闭显示。");
        preview.Show(asset, intent);
        Require(!ReferenceEquals(initial.GeometryCache, preview.CaptureState().GeometryCache), "Clear 后错误地复用了已释放的缓存。");
        preview.Dispose();
        preview.Dispose();
        CandidatePreview.PreviewState disposed = preview.CaptureState();
        Require(disposed.Disposed && !disposed.Visible && disposed.GeometryCache is null, "Dispose 没有释放预览资源。");
        bool rejectedAfterDispose = false;
        try { preview.Show(asset, intent); }
        catch (ObjectDisposedException) { rejectedAfterDispose = true; }
        Require(rejectedAfterDispose, "已释放的预览不应重新启用。");
        Require(sourceCrcs.SequenceEqual(sourceMeshes.Select(mesh => mesh.DataCRC(0))), "预览改变了源网格、法向或 UV。");
        Require(ids.SequenceEqual(Lux3DHostService.Objects(document).Select(obj => obj.Id)), "预览改变了文档对象。");
        Require(document.NextUndoRecordSerialNumber == undoNumber, "预览改变了 Undo 序列。");
        return new { updates = 20, cacheReused = true, sourceUnchanged = true, lifecycleVerified = true,
            transformUpdatesWithoutSnapshotIo = true, maximumBoundsError, tolerance, generationRequests = 0 };
    }

    private static object VerifyPlacement(PreparedAsset asset, PlacementIntent placement, double tolerance)
    {
        BoundingBox b = asset.Audit.Bounds;
        Point3d anchor = new((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2, b.Min.Z);
        Transform transform = placement.ToTransform(b);
        Point3d mapped = anchor; mapped.Transform(transform);
        Require(mapped.DistanceTo(placement.Point) <= tolerance, "底部中心未落在放置点。");
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
        Require(maxError <= tolerance, "变换与完整 U/V/N 平面基预期不一致。");
        return new { maxError, tolerance, height = placement.Height, angle = placement.RotationDegrees };
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
        if (depth > 32) throw new InvalidOperationException("材质节点过深。");
        var children = new List<object?>();
        for (RenderContent? child = content.FirstChild; child is not null; child = child.NextSibling) children.Add(RenderEvidence(child, depth + 1));
        string? sha = string.IsNullOrEmpty(content.Filename) ? null : File.Exists(content.Filename)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(content.Filename))) : "missing";
        return new { typeId = content.TypeId, slot = content.ChildSlotName, textureSha256 = sha, children };
    }
}

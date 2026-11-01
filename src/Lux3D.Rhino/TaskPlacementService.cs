using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Eto.Forms;
using Lux3D.Core;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoDoc = global::Rhino.RhinoDoc;

namespace Lux3D.Rhino;

internal sealed record TaskPlacementTarget(uint DocumentSerial, string DocumentSession, global::Rhino.UnitSystem Units, PlacementIntent Placement);
internal sealed record TaskPlacementResult(string State, string Message, Guid? ObjectId = null);

internal static class TaskPlacementPolicy
{
    // The same generated task has one direct-placement identity in each document.
    // Session identity remains a separate guard on every asynchronous request.
    public static Guid ImportId(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("任务操作编号不能为空。");
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("Lux3D.DirectPlacement.v1:" + operationId.ToString("D")));
        return new Guid(digest.AsSpan(0, 16));
    }
    public static bool CanRefresh(TaskRecord task) => !string.IsNullOrWhiteSpace(task.TaskId) && task.State is not ("Failed" or "ProviderCancelled" or "SubmissionUnknown");
    public static bool HasCachedAsset(TaskRecord task) => task.State == "Ready" && !string.IsNullOrWhiteSpace(task.AssetPath) &&
        !string.IsNullOrWhiteSpace(task.AssetSha256) && File.Exists(task.AssetPath);
    public static string NotReadyMessage(TaskRecord task) => task.State switch
    {
        "Ready" => "本地模型缓存或校验记录缺失；请查询原任务以重新下载，不必重新生成。",
        "Rejected" => "模型已生成，但下载资产未通过校验。请查询原任务尝试恢复；无需重新创建付费任务。" + Detail(task.Error),
        "DownloadFailed" => "模型已生成，下载尚未成功；请查询原任务重试下载。" + Detail(task.Error),
        "Queued" or "Running" => "原任务仍在生成中；可继续等待，完成后再放入。",
        "Succeeded" or "Downloading" or "Validating" => "原任务已生成，模型仍在下载或校验中，请稍后再放入。",
        "SubmissionUnknown" => "创建结果未知；请先核对提供方任务记录，不要直接重新生成。",
        "StatusUnknown" => "提供方任务状态尚未确认；请稍后查询原任务。" + Detail(task.Error),
        "Failed" => "提供方生成失败，当前没有可放入的模型。" + Detail(task.Error),
        "ProviderCancelled" => "提供方已取消此任务，当前没有可放入的模型。",
        _ => "此任务还没有可放入的已校验模型。" + Detail(task.Error),
    };
    public static string ErrorMessage(Exception exception)
    {
        string message = exception switch
        {
            OutOfMemoryException => "系统内存不足，未能完成模型放入；原任务和资产记录仍保留。请先检查当前文档，再释放内存并重试原任务，无需重新生成。",
            AssetValidationException validation => "模型校验未通过（" + validation.Code + "）：" + validation.Message + "；原任务和资产记录仍保留，请勿重复生成。",
            FileNotFoundException or DirectoryNotFoundException => "模型缓存不存在；请查询原任务重新下载后再放入。",
            _ => "未能放入模型：" + exception.Message,
        };
        return exception.Data["Lux3D.DiagnosticPath"] is string report ? message + " 诊断记录：" + report : message;
    }
    private static string Detail(string? error) => string.IsNullOrWhiteSpace(error) ? string.Empty : " 原因：" + error;
}

/// <summary>Task lookup/download is asynchronous; all Rhino access stays on the UI thread.</summary>
internal sealed class TaskPlacementService
{
    private readonly Lux3DHostService _host;
    private readonly Lux3DClient _client;
    private readonly ConcurrentDictionary<(uint, Guid), byte> _inFlight = new();
    public TaskPlacementService(Lux3DHostService host, Lux3DClient client) { _host = host; _client = client; }

    public TaskPlacementTarget Capture(RhinoDoc doc, PlacementIntent placement)
    {
        if (global::Rhino.RhinoApp.InvokeRequired) throw new InvalidOperationException("请从 Rhino 界面发起放置。");
        if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != doc.RuntimeSerialNumber)
            throw new InvalidOperationException("请先激活面板绑定的目标文档，再放入模型。");
        // IsLocked means this document owns the on-disk file lock, not that it is read-only.
        if (doc.IsClosing || doc.IsReadOnly) throw new InvalidOperationException("目标文档当前不可写。");
        RequireModelViewport(doc);
        return new TaskPlacementTarget(doc.RuntimeSerialNumber, _host.Session(doc), doc.ModelUnitSystem, placement);
    }
    public static PlacementIntent DefaultPlacement(RhinoDoc doc)
    {
        double height = global::Rhino.RhinoMath.UnitScale(global::Rhino.UnitSystem.Meters, doc.ModelUnitSystem);
        if (!double.IsFinite(height) || height <= 0) height = 1;
        Plane plane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
        return new PlacementIntent(plane, plane.Origin, height, 0);
    }
    public async Task<TaskPlacementResult> PlaceAsync(Guid operationId, TaskPlacementTarget target, Func<bool>? stillAllowed = null)
    {
        var key = (target.DocumentSerial, operationId);
        if (!_inFlight.TryAdd(key, 0)) return new TaskPlacementResult("Deferred", "此任务正在放入，请等待本次操作完成。");
        var trace = NewTrace(operationId, target);
        try
        {
            trace.Record("read-task");
            TaskRecord task = await _client.GetTaskAsync(operationId).ConfigureAwait(false);
            TaskPlacementResult? existing = await OnUiAsync(() => stillAllowed is not null && !stillAllowed()
                ? new TaskPlacementResult("Deferred", "放置已暂停；请重新显示面板后手动放入。") : FindExisting(target, task)).ConfigureAwait(false);
            if (existing is not null) return Finish(trace, existing);
            if (!TaskPlacementPolicy.HasCachedAsset(task) && TaskPlacementPolicy.CanRefresh(task))
            {
                trace.Record("recover-original-task");
                task = await _client.RefreshAsync(operationId).ConfigureAwait(false);
            }
            return Finish(trace, await OnUiAsync(() => PlaceOnUiThread(task, target, stillAllowed, trace)).ConfigureAwait(false));
        }
        catch (Exception ex) { RecordFailure(trace, ex); throw; }
        finally { _inFlight.TryRemove(key, out _); }
    }
    public async Task<TaskPlacementResult> PlaceReadyAsync(TaskRecord task, TaskPlacementTarget target, Func<bool> stillAllowed)
    {
        var key = (target.DocumentSerial, task.OperationId);
        if (!_inFlight.TryAdd(key, 0)) return new TaskPlacementResult("Deferred", "此任务正在放入，请等待本次操作完成。");
        var trace = NewTrace(task.OperationId, target);
        try { return Finish(trace, await OnUiAsync(() => PlaceOnUiThread(task, target, stillAllowed, trace)).ConfigureAwait(false)); }
        catch (Exception ex) { RecordFailure(trace, ex); throw; }
        finally { _inFlight.TryRemove(key, out _); }
    }
    private static HostImportTrace NewTrace(Guid operationId, TaskPlacementTarget target) => new(operationId,
        target.DocumentSerial, typeof(Lux3DPlugin).Assembly.GetName().Version?.ToString() ?? "unknown");
    private static TaskPlacementResult Finish(HostImportTrace trace, TaskPlacementResult result)
    {
        trace.Record("result", result.Message);
        if (result.ObjectId.HasValue) trace.Record("object", result.ObjectId.Value.ToString("D"));
        trace.Complete(result.State);
        return result;
    }
    private static void RecordFailure(HostImportTrace trace, Exception exception)
    {
        trace.Fail(exception);
        if (trace.IsPersisted) exception.Data["Lux3D.DiagnosticPath"] = trace.ReportPath;
    }
    private static void RequireModelViewport(RhinoDoc doc)
    {
        if (doc.Views.ActiveView is global::Rhino.Display.RhinoPageView { PageIsActive: true })
            throw new InvalidOperationException("当前处于布局纸面；请切换到模型视图或激活布局中的模型细节视口，再放入模型。");
    }
    private RhinoDoc ResolveTarget(TaskPlacementTarget target)
    {
        RhinoDoc doc = RhinoDoc.FromRuntimeSerialNumber(target.DocumentSerial) ?? throw new InvalidOperationException("提交时的目标文档已关闭；请在目标文档手动放入此任务。");
        if (_host.Session(doc) != target.DocumentSession) throw new InvalidOperationException("目标文档会话已改变；请手动重新选择目标文档。");
        if (doc.ModelUnitSystem != target.Units) throw new InvalidOperationException("目标文档单位已改变；请重新确认参考高度后手动放入。");
        if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != target.DocumentSerial) throw new InvalidOperationException("已切换到其他文档，未自动放入。请激活原文档后点击“放入当前文档”。");
        if (doc.IsClosing || doc.IsReadOnly) throw new InvalidOperationException("目标文档当前不可写；请恢复后手动放入。");
        RequireModelViewport(doc);
        return doc;
    }
    private TaskPlacementResult? FindExisting(TaskPlacementTarget target, TaskRecord task)
    {
        RhinoDoc doc = ResolveTarget(target);
        if (doc.IsCommandRunning || doc.UndoRecordingIsActive)
            return new TaskPlacementResult("Deferred", "请先结束当前 Rhino 命令；自动放置会在后续轮询重试，手动放置请再次点击。");
        ImportReceipt? receipt = _host.FindReceipt(doc, TaskPlacementPolicy.ImportId(task.OperationId));
        if (receipt is null) return null;
        if (receipt.State == "Undone") return new TaskPlacementResult("Undone", "此任务的 Block 已撤销；请使用 Redo 恢复。再次点击不会自动重新插入。", receipt.ObjectId);
        if (receipt.State != "Imported") return new TaskPlacementResult("Missing", "此前放入的 Block 已删除或缺失；不会自动重新插入。需要新实例时，请加入候选并明确接受。", receipt.ObjectId);
        if (task.AssetSha256 is not null && !string.Equals(task.AssetSha256, receipt.AssetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("同一任务的资产哈希与文档内已有 Block 不一致，请先核对任务记录。");
        string zoom = ZoomToObject(doc, receipt.ObjectId);
        return new TaskPlacementResult("Existing", "此任务已在当前文档中；没有重复插入。" + (zoom.Length == 0 ? "已定位已有 Block。" : zoom), receipt.ObjectId);
    }
    private TaskPlacementResult PlaceOnUiThread(TaskRecord task, TaskPlacementTarget target, Func<bool>? stillAllowed, HostImportTrace trace)
    {
        if (stillAllowed is not null && !stillAllowed()) return new TaskPlacementResult("Deferred", "自动放置已暂停；重新显示面板可继续。");
        TaskPlacementResult? existing = FindExisting(target, task);
        if (existing is not null) return existing;
        if (!TaskPlacementPolicy.HasCachedAsset(task)) throw new InvalidOperationException(TaskPlacementPolicy.NotReadyMessage(task));
        RhinoDoc doc = ResolveTarget(target);
        trace.Record("target", $"document={doc.RuntimeSerialNumber}; units={doc.ModelUnitSystem}; layer={doc.Layers.CurrentLayerIndex}; fileLockOwned={doc.IsLocked}");
        PreparedAsset prepared = _host.Prepare(task.AssetPath!, task.AssetSha256, doc, trace);
        try
        {
            // Native preflight may pump host events. Recheck intent and target before the actual mutation.
            if (stillAllowed is not null && !stillAllowed()) return new TaskPlacementResult("Deferred", "自动放置已暂停；重新显示面板可继续。");
            doc = ResolveTarget(target);
            if (doc.IsCommandRunning || doc.UndoRecordingIsActive) return new TaskPlacementResult("Deferred", "Rhino 正在执行命令；结束后可继续放入模型。");
            ImportReceipt receipt = _host.Commit(prepared, target.Placement, TaskPlacementPolicy.ImportId(task.OperationId), task.OperationId, trace);
            if (receipt.State != "Imported") return new TaskPlacementResult(receipt.State, "此任务之前已被撤销或删除，未重复插入；可用 Redo 或候选方式恢复。", receipt.ObjectId);
            string zoom = ZoomToObject(doc, receipt.ObjectId);
            trace.Record("view", zoom.Length == 0 ? "zoomed-to-block" : zoom);
            string repairs = prepared.Audit.RemovedDegenerateFaces > 0 || prepared.Audit.RecomputedNormalMeshes > 0
                ? $" 已移除 {prepared.Audit.RemovedDegenerateFaces} 个退化面，并为 {prepared.Audit.RecomputedNormalMeshes} 个网格重算法线；原 GLB 保留。" : string.Empty;
            return new TaskPlacementResult("Placed", "模型已放入当前文档并创建独立 Block；一次 Undo 可撤销。" + repairs + zoom, receipt.ObjectId);
        }
        finally
        {
            try { prepared.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Lux3D preview cleanup: " + ex.GetType().Name); }
        }
    }
    private static string ZoomToObject(RhinoDoc doc, Guid objectId)
    {
        // A view failure must never turn a successful import into a retryable import failure.
        try
        {
            var view = doc.Views.ActiveView;
            RhinoObject? item = doc.Objects.FindId(objectId);
            if (view is null || item is null) return " 视图未自动定位，可选中该 Block 后执行 ZoomSelected。";
            if (item.IsHidden || !doc.Layers[item.Attributes.LayerIndex].IsVisible)
                return " Block 已存在，但对象或所属图层已隐藏；请显示该对象/图层后再定位。";
            BoundingBox bounds = BoundingBox.Empty;
            foreach (var (mesh, transform, _) in Lux3DHostService.Meshes(new[] { item }, Transform.Identity))
                bounds.Union(mesh.GetBoundingBox(transform));
            if (!bounds.IsValid) return " 视图未自动定位，可手动执行 ZoomSelected。";
            bounds.Inflate(Math.Max(bounds.Diagonal.Length * 0.08, doc.ModelAbsoluteTolerance));
            bool zoomed = view.ActiveViewport.ZoomBoundingBox(bounds);
            view.Redraw();
            return zoomed ? string.Empty : " 视图未自动定位，可手动执行 ZoomSelected。";
        }
        catch { return " 视图定位未完成，Block 已成功导入；请手动缩放视图。"; }
    }
    internal static Task<T> OnUiAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Instance.AsyncInvoke(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }
}

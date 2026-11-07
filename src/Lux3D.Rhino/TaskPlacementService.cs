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
internal sealed record TaskPlacementProgress(Guid OperationId, string Stage, string Message, TimeSpan Elapsed)
{
    public uint DocumentSerial { get; init; }
}

internal static class TaskPlacementPolicy
{
    // The same generated task has one direct-placement identity in each document.
    // Session identity remains a separate guard on every asynchronous request.
    public static Guid ImportId(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("The task operation ID cannot be empty."));
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("Lux3D.DirectPlacement.v1:" + operationId.ToString("D")));
        return new Guid(digest.AsSpan(0, 16));
    }
    public static bool CanRefresh(TaskRecord task) => !string.IsNullOrWhiteSpace(task.TaskId) && task.State is not ("Failed" or "ProviderCancelled" or "SubmissionUnknown");
    public static bool HasCachedAsset(TaskRecord task) => task.State == "Ready" && !string.IsNullOrWhiteSpace(task.AssetPath) &&
        !string.IsNullOrWhiteSpace(task.AssetSha256) && File.Exists(task.AssetPath);
    public static string NotReadyMessage(TaskRecord task) => task.State switch
    {
        "Ready" => global::Lux3D.Core.LocalText.Get("The local model cache or validation record is missing. Query the original task to download it again; no new generation is needed."),
        "Rejected" => global::Lux3D.Core.LocalText.Get("The model was generated, but its download failed validation. Query the original task to recover it; no new paid task is needed.") + Detail(task.Error),
        "DownloadFailed" => global::Lux3D.Core.LocalText.Get("The model was generated, but its download did not succeed. Query the original task to retry.") + Detail(task.Error),
        "Queued" or "Running" => global::Lux3D.Core.LocalText.Get("The original task is still generating. Wait for it to finish before placing."),
        "Succeeded" or "Downloading" or "Validating" => global::Lux3D.Core.LocalText.Get("The original task was generated; its model is still downloading or validating. Try placement later."),
        "SubmissionUnknown" => global::Lux3D.Core.LocalText.Get("The creation result is unknown. Check the provider's task record before generating again."),
        "StatusUnknown" => global::Lux3D.Core.LocalText.Get("The provider task state is not confirmed yet. Query the original task later.") + Detail(task.Error),
        "Failed" => global::Lux3D.Core.LocalText.Get("Provider generation failed; no model is available to place.") + Detail(task.Error),
        "ProviderCancelled" => global::Lux3D.Core.LocalText.Get("The provider cancelled this task; no model is available to place."),
        _ => global::Lux3D.Core.LocalText.Get("This task has no validated model to place yet.") + Detail(task.Error),
    };
    public static string ErrorMessage(Exception exception)
    {
        string message = exception switch
        {
            OutOfMemoryException => global::Lux3D.Core.LocalText.Get("Not enough memory to place the model. The original task and asset records are retained. Inspect the current document, free memory and retry the original task without regenerating."),
            AssetValidationException validation => global::Lux3D.Core.LocalText.Get("Model validation failed (") + validation.Code + "）：" + validation.Message + global::Lux3D.Core.LocalText.Get(". The original task and asset records are retained; do not regenerate."),
            FileNotFoundException or DirectoryNotFoundException => global::Lux3D.Core.LocalText.Get("The model cache is missing. Query the original task to download it again before placing."),
            _ => global::Lux3D.Core.LocalText.Get("Could not place the model: ") + exception.Message,
        };
        return exception.Data["Lux3D.DiagnosticPath"] is string report ? message + global::Lux3D.Core.LocalText.Get(" Diagnostic log: ") + report : message;
    }
    private static string Detail(string? error) => string.IsNullOrWhiteSpace(error) ? string.Empty : global::Lux3D.Core.LocalText.Get(" Reason: ") + error;
}

/// <summary>Task lookup/download is asynchronous; all Rhino access stays on the UI thread.</summary>
internal sealed class TaskPlacementService
{
    private readonly Lux3DHostService _host;
    private readonly Lux3DClient _client;
    private readonly ConcurrentDictionary<(uint, Guid), byte> _inFlight = new();
    public event Action<TaskPlacementProgress>? ProgressChanged;
    public TaskPlacementService(Lux3DHostService host, Lux3DClient client) { _host = host; _client = client; }

    public TaskPlacementTarget Capture(RhinoDoc doc, PlacementIntent placement)
    {
        if (global::Rhino.RhinoApp.InvokeRequired) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Start placement from Rhino's interface."));
        if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != doc.RuntimeSerialNumber)
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Activate the document linked to this panel before placing the model."));
        // IsLocked means this document owns the on-disk file lock, not that it is read-only.
        if (doc.IsClosing || doc.IsReadOnly) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The target document is not writable."));
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
        if (!_inFlight.TryAdd(key, 0)) return new TaskPlacementResult("Deferred", global::Lux3D.Core.LocalText.Get("This task is already being placed. Wait for the current operation."));
        var trace = NewTrace(operationId, target);
        try
        {
            trace.Record("read-task");
            TaskRecord task = await _client.GetTaskAsync(operationId).ConfigureAwait(false);
            TaskPlacementResult? existing = await OnUiAsync(() => stillAllowed is not null && !stillAllowed()
                ? new TaskPlacementResult("Deferred", global::Lux3D.Core.LocalText.Get("Placement is paused. Show the panel again and place manually.")) : FindExisting(target, task)).ConfigureAwait(false);
            if (existing is not null) return Finish(trace, existing);
            if (!TaskPlacementPolicy.HasCachedAsset(task) && TaskPlacementPolicy.CanRefresh(task))
            {
                trace.Record("recover-original-task");
                task = await _client.RefreshAsync(operationId).ConfigureAwait(false);
            }
            return Finish(trace, await PrepareAndPlaceAsync(task, target, stillAllowed, trace).ConfigureAwait(false));
        }
        catch (Exception ex) { RecordFailure(trace, ex); throw; }
        finally { _inFlight.TryRemove(key, out _); }
    }
    public async Task<TaskPlacementResult> PlaceReadyAsync(TaskRecord task, TaskPlacementTarget target, Func<bool> stillAllowed)
    {
        var key = (target.DocumentSerial, task.OperationId);
        if (!_inFlight.TryAdd(key, 0)) return new TaskPlacementResult("Deferred", global::Lux3D.Core.LocalText.Get("This task is already being placed. Wait for the current operation."));
        var trace = NewTrace(task.OperationId, target);
        try { return Finish(trace, await PrepareAndPlaceAsync(task, target, stillAllowed, trace).ConfigureAwait(false)); }
        catch (Exception ex) { RecordFailure(trace, ex); throw; }
        finally { _inFlight.TryRemove(key, out _); }
    }
    private HostImportTrace NewTrace(Guid operationId, TaskPlacementTarget target)
    {
        var trace = new HostImportTrace(operationId, target.DocumentSerial, Lux3DPlugin.DisplayVersion);
        trace.StageChanged += (stage, elapsed) =>
        {
            string? message = stage switch
            {
                "read-task" => global::Lux3D.Core.LocalText.Get("Read local task record"),
                "recover-original-task" => global::Lux3D.Core.LocalText.Get("Query original task and recover download (no new generation)"),
                "preflight_file_start" => global::Lux3D.Core.LocalText.Get("Verify model file and prepare import snapshot"),
                "preflight_start" or "preflight_native_import_start" => global::Lux3D.Core.LocalText.Get("Rhino is preflighting the model"),
                "preflight_native_import_result" => global::Lux3D.Core.LocalText.Get("Check and repair preflight meshes"),
                "commit_start" => global::Lux3D.Core.LocalText.Get("Prepare final import and undo record"),
                "commit_native_import_start" => global::Lux3D.Core.LocalText.Get("Rhino is importing into the current document"),
                "commit_native_import_result" => global::Lux3D.Core.LocalText.Get("Check imported meshes and create block"),
                "commit_block_geometry_ready" => global::Lux3D.Core.LocalText.Get("Place model and prepare view framing"),
                "view" => global::Lux3D.Core.LocalText.Get("Model imported; finishing records"),
                _ => null,
            };
            if (message is null) return;
            var progress = new TaskPlacementProgress(operationId, stage, message, elapsed) { DocumentSerial = target.DocumentSerial };
            foreach (Delegate handler in ProgressChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { ((Action<TaskPlacementProgress>)handler)(progress); } catch { }
        };
        return trace;
    }
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
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The current view is layout paper space. Switch to a model view or activate a layout detail before placing the model."));
    }
    private RhinoDoc ResolveTarget(TaskPlacementTarget target)
    {
        RhinoDoc doc = RhinoDoc.FromRuntimeSerialNumber(target.DocumentSerial) ?? throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The document used at submission is closed. Place this task manually in the target document."));
        if (_host.Session(doc) != target.DocumentSession) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The target document session changed. Select the target document again manually."));
        if (doc.ModelUnitSystem != target.Units) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The target document units changed. Confirm the reference height again before placing manually."));
        if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != target.DocumentSerial) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Another document is active; automatic placement was skipped. Activate the original document and click Place in current document."));
        if (doc.IsClosing || doc.IsReadOnly) throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The target document is not writable. Restore access and place manually."));
        RequireModelViewport(doc);
        return doc;
    }
    private TaskPlacementResult? FindExisting(TaskPlacementTarget target, TaskRecord task)
    {
        RhinoDoc doc = ResolveTarget(target);
        if (doc.IsCommandRunning || doc.UndoRecordingIsActive)
            return new TaskPlacementResult("Deferred", global::Lux3D.Core.LocalText.Get("Finish the current Rhino command. Automatic placement will retry on a later poll; click again for manual placement."));
        ImportReceipt? receipt = _host.FindReceipt(doc, TaskPlacementPolicy.ImportId(task.OperationId));
        if (receipt is null) return null;
        if (receipt.State == "Undone") return new TaskPlacementResult("Undone", global::Lux3D.Core.LocalText.Get("This task's block was undone. Use Redo to restore it; clicking again does not insert another copy."), receipt.ObjectId);
        if (receipt.State != "Imported") return new TaskPlacementResult("Missing", global::Lux3D.Core.LocalText.Get("The previous block was deleted or is missing; it will not be inserted again automatically. Add and explicitly accept a candidate for a new instance."), receipt.ObjectId);
        if (task.AssetSha256 is not null && !string.Equals(task.AssetSha256, receipt.AssetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The task's asset hash differs from its existing block in the document. Check the task record first."));
        string zoom = ZoomToObject(doc, receipt.ObjectId);
        return new TaskPlacementResult("Existing", global::Lux3D.Core.LocalText.Get("This task is already in the current document; no duplicate was inserted.") + (zoom.Length == 0 ? global::Lux3D.Core.LocalText.Get("Located the existing block.") : zoom), receipt.ObjectId);
    }
    private async Task<TaskPlacementResult> PrepareAndPlaceAsync(TaskRecord task, TaskPlacementTarget target, Func<bool>? stillAllowed, HostImportTrace trace)
    {
        TaskPlacementResult? existing = await OnUiAsync(() => stillAllowed is not null && !stillAllowed()
            ? new TaskPlacementResult("Deferred", global::Lux3D.Core.LocalText.Get("Automatic placement is paused; show the panel again to continue.")) : FindExisting(target, task)).ConfigureAwait(false);
        if (existing is not null) return existing;
        if (!TaskPlacementPolicy.HasCachedAsset(task)) throw new InvalidOperationException(TaskPlacementPolicy.NotReadyMessage(task));
        // Pure file/managed work only: no RhinoDoc, geometry or UI access on this worker.
        using GlbImportSnapshot snapshot = await Task.Run(() => GlbImportSnapshot.Create(task.AssetPath!, task.AssetSha256, trace)).ConfigureAwait(false);
        return await OnUiAsync(() => PlaceOnUiThread(task, target, stillAllowed, trace, snapshot)).ConfigureAwait(false);
    }
    private TaskPlacementResult PlaceOnUiThread(TaskRecord task, TaskPlacementTarget target, Func<bool>? stillAllowed, HostImportTrace trace, GlbImportSnapshot snapshot)
    {
        if (stillAllowed is not null && !stillAllowed()) return new TaskPlacementResult("Deferred", global::Lux3D.Core.LocalText.Get("Automatic placement is paused; show the panel again to continue."));
        TaskPlacementResult? existing = FindExisting(target, task);
        if (existing is not null) return existing;
        RhinoDoc doc = ResolveTarget(target);
        trace.Record("target", $"document={doc.RuntimeSerialNumber}; units={doc.ModelUnitSystem}; layer={doc.Layers.CurrentLayerIndex}; fileLockOwned={doc.IsLocked}");
        PreparedAsset prepared = _host.Prepare(snapshot, doc, trace);
        try
        {
            // Native preflight may pump host events. Recheck intent and target before the actual mutation.
            if (stillAllowed is not null && !stillAllowed()) return new TaskPlacementResult("Deferred", global::Lux3D.Core.LocalText.Get("Automatic placement is paused; show the panel again to continue."));
            doc = ResolveTarget(target);
            if (doc.IsCommandRunning || doc.UndoRecordingIsActive) return new TaskPlacementResult("Deferred", global::Lux3D.Core.LocalText.Get("Rhino is running a command. Finish it before placing the model."));
            ImportReceipt receipt = _host.Commit(prepared, target.Placement, TaskPlacementPolicy.ImportId(task.OperationId), task.OperationId, trace);
            if (receipt.State != "Imported") return new TaskPlacementResult(receipt.State, global::Lux3D.Core.LocalText.Get("This task was previously undone or deleted; no duplicate was inserted. Restore with Redo or through a candidate."), receipt.ObjectId);
            string zoom = ZoomToObject(doc, receipt.ObjectId);
            trace.Record("view", zoom.Length == 0 ? "zoomed-to-block" : zoom);
            string repairs = prepared.Audit.RemovedDegenerateFaces > 0 || prepared.Audit.RecomputedNormalMeshes > 0
                ? global::Lux3D.Core.LocalText.Format(" Removed {0} degenerate faces and recomputed normals for {1} meshes; the original GLB is retained.", prepared.Audit.RemovedDegenerateFaces, prepared.Audit.RecomputedNormalMeshes) : string.Empty;
            return new TaskPlacementResult("Placed", global::Lux3D.Core.LocalText.Get("The model was placed in the current document as an independent block; one Undo reverses it.") + repairs + zoom, receipt.ObjectId);
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
            if (view is null || item is null) return global::Lux3D.Core.LocalText.Get(" The view was not framed automatically. Select the block and run ZoomSelected.");
            if (item.IsHidden || !doc.Layers[item.Attributes.LayerIndex].IsVisible)
                return global::Lux3D.Core.LocalText.Get(" The block exists, but the object or its layer is hidden. Show it before locating it.");
            BoundingBox bounds = BoundingBox.Empty;
            foreach (var (mesh, transform, _) in Lux3DHostService.Meshes(new[] { item }, Transform.Identity))
                bounds.Union(mesh.GetBoundingBox(transform));
            if (!bounds.IsValid) return global::Lux3D.Core.LocalText.Get(" The view was not framed automatically. Run ZoomSelected manually.");
            bounds.Inflate(Math.Max(bounds.Diagonal.Length * 0.08, doc.ModelAbsoluteTolerance));
            bool zoomed = view.ActiveViewport.ZoomBoundingBox(bounds);
            view.Redraw();
            return zoomed ? string.Empty : global::Lux3D.Core.LocalText.Get(" The view was not framed automatically. Run ZoomSelected manually.");
        }
        catch { return global::Lux3D.Core.LocalText.Get("View framing did not complete, but the block was imported successfully. Adjust the view manually."); }
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

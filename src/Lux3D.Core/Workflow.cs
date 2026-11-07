using System.Security.Cryptography;
using System.Text;

namespace Lux3D.Core;

public sealed record RemoteTask(string TaskId, string State, int RawStatus, Uri? GlbUri);
public sealed class SubmissionRejectedException : Exception
{
    public SubmissionRejectedException(string message) : base(message) { }
}
public interface IGenerationProvider
{
    string Region { get; }
    string ConnectionId { get; }
    Task<string> CreateAsync(GenerationInput input, CancellationToken cancellationToken);
    Task<RemoteTask> QueryAsync(string taskId, CancellationToken cancellationToken);
    Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class Workflow
{
    private readonly TaskStore _store;
    public Workflow(TaskStore store) { _store = store; }
    public async Task<TaskRecord> GenerateAsync(IGenerationProvider provider, GenerationInput input, CancellationToken ct = default)
    {
        ValidateInput(input);
        using var mutation = _store.LockMutation();
        if (_store.HasUnknownSubmissions() && !input.AcknowledgeUnknownSubmission)
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("A task has an unknown submission result. Check the original account's history and explicitly confirm that another generation may incur a duplicate charge."));
        string? image = null;
        string inputHash;
        if (!string.IsNullOrWhiteSpace(input.ImagePath))
        {
            var snapshot = _store.SnapshotImage(input.ImagePath);
            image = snapshot.Path; inputHash = snapshot.Hash;
        }
        else inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Prompt))).ToLowerInvariant();
        input = input with { ImagePath = image };
        var task = new TaskRecord(Guid.NewGuid(), provider.Region, input.Prompt, image, input.Model, input.FaceCount,
            "Submitting", null, null, null, null, DateTimeOffset.UtcNow, provider.ConnectionId, input.EnablePbr, inputHash);
        // The durable preflight must finish before any upload or paid create call.
        _store.Save(task);
        try
        {
            string id = await provider.CreateAsync(input, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(id)) throw new IOException(global::Lux3D.Core.LocalText.Get("The creation response contains no reliable task ID."));
            task = task with { TaskId = id, State = "Queued" };
            _store.Save(task);
            return task;
        }
        catch (SubmissionRejectedException ex)
        {
            task = task with { State = "Failed", Error = ex.Message };
            _store.Save(task);
            return task;
        }
        catch (Exception)
        {
            // Do not echo remote bodies, signed URLs or credentials. Never retry POST.
            task = task with { State = "SubmissionUnknown", Error = global::Lux3D.Core.LocalText.Get("No reliable creation receipt was received. Check the original region's task history; another generation may incur a duplicate charge.") };
            _store.Save(task);
            return task;
        }
    }
    public async Task<TaskRecord> RefreshAsync(IGenerationProvider provider, Guid operationId, CancellationToken ct = default)
    {
        using var mutation = _store.LockMutation();
        TaskRecord task = _store.Get(operationId);
        if (task.Region != provider.Region || task.ConnectionId != provider.ConnectionId)
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Restore the region and connection used to submit this task; it cannot be queried with another account."));
        if (task.TaskId is null)
            return task with { State = "SubmissionUnknown", Error = global::Lux3D.Core.LocalText.Get("The task ID is missing; automatic resubmission is disabled. Check the original account's history.") };
        if (task.State == "Ready" && task.AssetPath is not null && File.Exists(task.AssetPath))
        {
            await using var cached = File.OpenRead(task.AssetPath);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(cached, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (hash == task.AssetSha256) return task;
            task = task with { State = "Missing", Error = global::Lux3D.Core.LocalText.Get("The asset hash differs; retrieving the original task result again.") };
        }
        bool succeeded = task.State is "Succeeded" or "DownloadFailed" or "Downloading";
        byte[]? downloaded = null;
        try
        {
            RemoteTask remote = await provider.QueryAsync(task.TaskId, ct).ConfigureAwait(false);
            task = task with { State = remote.State, Error = null };
            _store.Save(task);
            succeeded = remote.State == "Succeeded";
            if (!succeeded) return task;
            if (remote.GlbUri is null) throw new IOException(global::Lux3D.Core.LocalText.Get("The successful task contains no GLB result."));
            task = task with { State = "Downloading" }; _store.Save(task);
            downloaded = await provider.DownloadAsync(remote.GlbUri, ct).ConfigureAwait(false);
            var asset = _store.StoreAsset(downloaded);
            task = task with { State = "Ready", AssetPath = asset.Path, AssetSha256 = asset.Hash, Error = null,
                ValidationCode = null, RejectedAssetPath = null, RejectedAssetSha256 = null };
        }
        catch (AssetValidationException ex)
        {
            // Validator messages are local diagnostics, never provider bodies or signed URLs.
            // Keep the exact structural validation cause; geometry counts are not rejection criteria.
            string detail = ex.Message.Length <= 512 ? ex.Message : ex.Message[..512];
            task = task with { State = "Rejected", AssetPath = null, AssetSha256 = null,
                ValidationCode = ex.Code, Error = global::Lux3D.Core.LocalText.Format("Generation finished, but local GLB validation failed ({0}): {1} Retry retrieving the original task; no new generation is needed.", ex.Code, detail) };
            if (downloaded is not null)
            {
                try
                {
                    var rejected = _store.PreserveRejectedAsset(downloaded);
                    task = task with { RejectedAssetPath = rejected.Path, RejectedAssetSha256 = rejected.Hash };
                }
                catch (Exception io) when (io is IOException or UnauthorizedAccessException)
                { task = task with { Error = task.Error + global::Lux3D.Core.LocalText.Get(" A diagnostic copy could not be retained.") }; }
            }
        }
        catch (OutOfMemoryException)
        {
            task = task with { State = succeeded ? "DownloadFailed" : "StatusUnknown",
                Error = global::Lux3D.Core.LocalText.Get("There is not enough memory to download or read the original result. This does not mean the format is invalid. Free memory and recover the original task without regenerating.") };
        }
        catch (Exception)
        {
            task = task with { State = succeeded ? "DownloadFailed" : "StatusUnknown", Error = succeeded ? global::Lux3D.Core.LocalText.Get("Remote generation finished, but the download did not. Retry querying and downloading; no new task will be created.") : global::Lux3D.Core.LocalText.Get("The status query is temporarily unavailable. Query the original task again; no new task will be created.") };
        }
        _store.Save(task);
        return task;
    }
    private static void ValidateInput(GenerationInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Prompt) && string.IsNullOrWhiteSpace(input.ImagePath)) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Enter text or choose one PNG/JPEG image."));
        if (!string.IsNullOrWhiteSpace(input.Prompt) && !string.IsNullOrWhiteSpace(input.ImagePath)) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Choose either text or one image; clear the other input."));
        if (input.Prompt.Length > 4000) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("The prompt is too long (maximum 4000 characters)."));
        if (input.FaceCount is < 10000 or > 300000) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Target face count must be between 10000 and 300000."));
        if (input.Model is not ("G1" or "G1-Turbo")) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Unsupported model version."));
    }
}

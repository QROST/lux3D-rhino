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
            throw new InvalidOperationException("存在提交结果未知的任务。请核对原账户历史，并明确确认再次生成可能重复扣费。");
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
            if (string.IsNullOrWhiteSpace(id)) throw new IOException("创建响应没有可靠任务编号。");
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
            task = task with { State = "SubmissionUnknown", Error = "未取得可靠创建回执。请在原区域核对任务历史；再次生成可能重复扣费。" };
            _store.Save(task);
            return task;
        }
    }
    public async Task<TaskRecord> RefreshAsync(IGenerationProvider provider, Guid operationId, CancellationToken ct = default)
    {
        using var mutation = _store.LockMutation();
        TaskRecord task = _store.Get(operationId);
        if (task.Region != provider.Region || task.ConnectionId != provider.ConnectionId)
            throw new InvalidOperationException("请恢复该任务提交时的区域和连接；不能改用另一个账户查询。");
        if (task.TaskId is null)
            return task with { State = "SubmissionUnknown", Error = "缺少任务编号，禁止自动重新提交。请核对原账户历史。" };
        if (task.State == "Ready" && task.AssetPath is not null && File.Exists(task.AssetPath))
        {
            await using var cached = File.OpenRead(task.AssetPath);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(cached, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (hash == task.AssetSha256) return task;
            task = task with { State = "Missing", Error = "资产哈希不一致，重新获取原任务结果。" };
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
            if (remote.GlbUri is null) throw new IOException("成功任务没有 GLB 结果。");
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
                ValidationCode = ex.Code, Error = $"生成已完成，本地 GLB 校验未通过（{ex.Code}）：{detail} 可重试获取原任务，不必重新生成。" };
            if (downloaded is not null)
            {
                try
                {
                    var rejected = _store.PreserveRejectedAsset(downloaded);
                    task = task with { RejectedAssetPath = rejected.Path, RejectedAssetSha256 = rejected.Hash };
                }
                catch (Exception io) when (io is IOException or UnauthorizedAccessException)
                { task = task with { Error = task.Error + " 未能保留诊断副本。" }; }
            }
        }
        catch (OutOfMemoryException)
        {
            task = task with { State = succeeded ? "DownloadFailed" : "StatusUnknown",
                Error = "当前系统内存不足，无法完成原任务结果的下载或读取；这不是模型格式无效。释放内存后可恢复原任务，无需重新生成。" };
        }
        catch (Exception)
        {
            task = task with { State = succeeded ? "DownloadFailed" : "StatusUnknown", Error = succeeded ? "远程生成已完成，下载未完成。可重试查询与下载，不会重新创建任务。" : "查询暂不可用；可继续查询原任务，不会重新创建。" };
        }
        _store.Save(task);
        return task;
    }
    private static void ValidateInput(GenerationInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Prompt) && string.IsNullOrWhiteSpace(input.ImagePath)) throw new ArgumentException("请输入文字或选择一张 PNG/JPEG 图片。");
        if (!string.IsNullOrWhiteSpace(input.Prompt) && !string.IsNullOrWhiteSpace(input.ImagePath)) throw new ArgumentException("文字和单图请选择一种输入；请清空另一项。");
        if (input.Prompt.Length > 4000) throw new ArgumentException("提示词过长（上限 4000 字符）。");
        if (input.FaceCount is < 10000 or > 300000) throw new ArgumentException("目标面数应在 10000 到 300000 之间。");
        if (input.Model is not ("G1" or "G1-Turbo")) throw new ArgumentException("不支持的模型版本。");
    }
}

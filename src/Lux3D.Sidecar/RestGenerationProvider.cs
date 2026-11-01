using Lux3D.Core;
using Lux3D.Provider;

namespace Lux3D.Sidecar;

internal sealed class RestGenerationProvider : IGenerationProvider
{
    private readonly Lux3DApiClient _api;
    private readonly HttpClient _download;
    public string Region { get; }
    public string ConnectionId { get; }
    public RestGenerationProvider(HttpClient http, HttpClient download, ApiKeyCredential credential)
    {
        _api = new(http, credential); _download = download;
        Region = credential.Region == LuxRegion.China ? "cn" : "global"; ConnectionId = credential.ConnectionId;
    }
    public async Task<string> CreateAsync(GenerationInput input, CancellationToken ct)
    {
        try
        {
            if (input.ImagePath is null)
                return await _api.CreateTextAsync(input.Prompt, input.Model, input.FaceCount, input.EnablePbr, ct).ConfigureAwait(false);
            Uri image = await _api.UploadImageAsync(input.ImagePath, ct).ConfigureAwait(false);
            return await _api.CreateImageAsync(image, input.Model, input.FaceCount, input.EnablePbr, ct).ConfigureAwait(false);
        }
        catch (ProviderException ex) when (!ex.SubmissionMayHaveSucceeded)
        { throw new SubmissionRejectedException(ex.Message); }
    }
    public async Task<RemoteTask> QueryAsync(string taskId, CancellationToken ct)
    {
        ProviderTask task = await _api.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        string state = task.State switch
        {
            "queued" => "Queued", "running" => "Running", "succeeded" => "Succeeded", "failed" => "Failed", "cancelled" => "ProviderCancelled", _ => "StatusUnknown"
        };
        return new(task.TaskId, state, task.Status, task.GlbUri);
    }
    public async Task<byte[]> DownloadAsync(Uri uri, CancellationToken ct)
    {
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            if (uri.Scheme != "https" || uri.IsLoopback || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo)) throw new IOException("资产地址不支持。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is null) throw new IOException("下载重定向无效。");
                uri = new Uri(uri, response.Headers.Location); continue;
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream(); byte[] buffer = new byte[64 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        throw new IOException("资产下载重定向次数过多。");
    }
}

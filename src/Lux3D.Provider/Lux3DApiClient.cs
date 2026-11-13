using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lux3D.Provider;

/// <summary>Regional REST adapter. Never retries creates and never sends a gateway key to OUS or a CDN.</summary>
public sealed class Lux3DApiClient
{
    private readonly HttpClient _http;
    private readonly ApiKeyCredential _credential;
    private const int JsonLimit = 2 * 1024 * 1024;
    public Lux3DApiClient(HttpClient http, ApiKeyCredential credential) { _http = http; _credential = credential; }
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(90) };

    public async Task VerifyConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var request = Gateway(HttpMethod.Get, "lux3d/v1/generate/task/list?page=1&pagesize=1");
        _ = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> CreateTextAsync(string prompt, string model = "G1-Turbo", int faceCount = 50000, bool enablePbr = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 4000) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Enter a description of 1–4000 characters."), nameof(prompt));
        var body = GenerationBody(model, faceCount, enablePbr); body["prompt"] = prompt.Trim();
        return CreateAsync("text-to-3d", body, cancellationToken);
    }
    public Task<string> CreateImageAsync(Uri uploadedImage, string model = "G1-Turbo", int faceCount = 50000, bool enablePbr = true, CancellationToken cancellationToken = default)
    {
        var body = GenerationBody(model, faceCount, enablePbr); body["img"] = ProviderValidation.HttpsUri(uploadedImage.AbsoluteUri).AbsoluteUri;
        return CreateAsync("img-to-3d", body, cancellationToken);
    }
    private static Dictionary<string, object> GenerationBody(string model, int faceCount, bool pbr)
    {
        if (model is not ("G1" or "G1-Turbo")) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("The model must be G1 or G1-Turbo."), nameof(model));
        if (faceCount is < 10000 or > 300000) throw new ArgumentOutOfRangeException(nameof(faceCount), global::Lux3D.Core.LocalText.Get("Target face count must be between 10000 and 300000."));
        var body = new Dictionary<string, object> { ["version"] = model, ["faceCount"] = faceCount, ["outputFormat"] = new[] { "glb" }, ["aiPredictSize"] = false };
        // G1 always provides a PBR GLB; its contract forbids the enablePbr field.
        if (model == "G1-Turbo") body["enablePbr"] = pbr;
        return body;
    }
    private async Task<string> CreateAsync(string operation, Dictionary<string, object> body, CancellationToken ct)
    {
        using var request = Gateway(HttpMethod.Post, $"lux3d/v1/generate/{operation}/task/create");
        request.Content = JsonContent.Create(body);
        try
        {
            var data = await SendAsync(request, ct, isCreate: true).ConfigureAwait(false);
            return ProviderValidation.TaskId(Scalar(data));
        }
        catch (ProviderException ex) when (!ex.SubmissionMayHaveSucceeded && ex.Code == "invalid_task_id")
        { throw new ProviderException("submission_unknown", global::Lux3D.Core.LocalText.Get("The generation request has an unknown result. Check task history; do not resubmit."), true); }
    }
    public async Task<ProviderTask> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        string canonical = ProviderValidation.TaskId(taskId);
        using var request = Gateway(HttpMethod.Get, "lux3d/v1/generate/task/get?taskid=" + canonical);
        var data = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("taskId", out var id) || ProviderValidation.TaskId(Scalar(id)) != canonical ||
            !data.TryGetProperty("bizId", out var biz) || biz.ValueKind != JsonValueKind.String || biz.GetString() != "LUX_3D" || !data.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.Number || !state.TryGetInt32(out int status))
            throw new ProviderException("invalid_task", global::Lux3D.Core.LocalText.Get("The task response identity or state does not match."));
        string localState = status switch { 0 => "queued", 1 or 2 => "running", 3 => "succeeded", 4 or 5 => "failed", 6 => "cancelled", _ => "unknown" };
        if (status != 3) return new(canonical, status, localState, null);
        try { return new(canonical, status, localState, ParseGlb(data)); }
        catch (ProviderException ex)
        {
            // A validated remote success remains success even when its artifact is unusable.
            // The workflow can now record DownloadFailed without losing the known task status.
            return new(canonical, status, localState, null, ex.Message);
        }
    }
    private static Uri ParseGlb(JsonElement task)
    {
        if (!task.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array)
            throw new ProviderException("output_invalid", global::Lux3D.Core.LocalText.Get("Generation succeeded, but no recognizable GLB was returned. Do not regenerate."));
        var glbs = new List<Uri>();
        foreach (var item in outputs.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("content", out var content)) continue;
            if (content.ValueKind != JsonValueKind.String) throw new ProviderException("output_invalid", global::Lux3D.Core.LocalText.Get("GLB output content does not match the contract."));
            string? value = content.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("NOT_REQUESTED", StringComparison.OrdinalIgnoreCase) || value.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
            var uri = ProviderValidation.HttpsUri(value);
            if (uri.AbsolutePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) glbs.Add(uri);
        }
        if (glbs.Count != 1) throw new ProviderException("output_invalid", global::Lux3D.Core.LocalText.Get("Generation succeeded, but no unique recognizable GLB was returned. Keep the task and inspect its output."));
        return glbs[0];
    }

    public async Task<Uri> UploadImageAsync(string path, CancellationToken cancellationToken = default)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp")) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("The image must be PNG, JPEG or WebP."), nameof(path));
        // One snapshot is hashed and uploaded, preventing source-file changes between the two operations.
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 20 * 1024 * 1024) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Image size must be between 1 byte and 20 MiB."), nameof(path));
        byte[] bytes;
        await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous))
        using (var snapshot = new MemoryStream())
        {
            byte[] chunk = new byte[8192]; int count;
            while ((count = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (snapshot.Length + count > 20 * 1024 * 1024) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("The image exceeds the size limit."), nameof(path));
                snapshot.Write(chunk, 0, count);
            }
            bytes = snapshot.ToArray();
        }
        if (bytes.Length == 0) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("The image cannot be empty."), nameof(path));
        string md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        using var tokenRequest = Gateway(HttpMethod.Get, "asset/v1/token");
        var token = await SendAsync(tokenRequest, cancellationToken).ConfigureAwait(false);
        string ousToken = RequiredString(token, "ousToken");
        Uri host = ProviderValidation.HttpsUri(RequiredString(token, "globalDomain"));
        if (host.AbsolutePath != "/" || !string.IsNullOrEmpty(host.Query) || !string.IsNullOrEmpty(host.Fragment)) throw new ProviderException("invalid_upload", global::Lux3D.Core.LocalText.Get("Invalid upload domain."));
        if (!token.TryGetProperty("blockSize", out var block) || !long.TryParse(Scalar(block), out long size) || size <= 0)
            throw new ProviderException("invalid_upload", global::Lux3D.Core.LocalText.Get("Invalid upload chunk size."));
        if (bytes.LongLength <= size)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(md5), "md5"); form.Add(new ByteArrayContent(bytes), "file", Path.GetFileName(path));
            await SendOusAsync(host, ousToken, "single/upload", form, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            int blockSize = checked((int)Math.Min(size, bytes.Length));
            int total = (int)((bytes.LongLength + blockSize - 1) / blockSize);
            using var init = new FormUrlEncodedContent(new Dictionary<string, string> { ["md5"] = md5, ["blocks"] = total.ToString(CultureInfo.InvariantCulture), ["size"] = bytes.Length.ToString(CultureInfo.InvariantCulture), ["name"] = Path.GetFileName(path) });
            var initialized = await SendOusAsync(host, ousToken, "block/upload/init", init, cancellationToken).ConfigureAwait(false);
            if (initialized.ValueKind != JsonValueKind.Object) throw new ProviderException("invalid_upload", global::Lux3D.Core.LocalText.Get("Invalid upload status format."));
            bool deduplicated = initialized.TryGetProperty("deduplicated", out var dedup) && dedup.ValueKind == JsonValueKind.True;
            if (!deduplicated)
            {
                // Upload all parts. This is valid for a fresh token; omitted/resumed parts are unnecessary here.
                for (int n = 1; n <= total; n++)
                {
                    using var part = new MultipartFormDataContent();
                    part.Add(new StringContent(n.ToString(CultureInfo.InvariantCulture)), "block");
                    int offset = (n - 1) * blockSize;
                    part.Add(new ByteArrayContent(bytes, offset, Math.Min(blockSize, bytes.Length - offset)), "file", Path.GetFileName(path) + ".part-" + n);
                    await SendOusAsync(host, ousToken, "block/upload/part", part, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        for (int attempt = 0; attempt < 120; attempt++)
        {
            var result = await SendOusAsync(host, ousToken, "upload/status", null, cancellationToken).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out int value)) throw new ProviderException("invalid_upload", global::Lux3D.Core.LocalText.Get("Invalid upload status format."));
            if (value == 5) return ProviderValidation.HttpsUri(RequiredString(result, "url"));
            if (value is 6 or 8) throw new ProviderException("upload_failed", global::Lux3D.Core.LocalText.Get("Image upload failed."));
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        throw new ProviderException("upload_timeout", global::Lux3D.Core.LocalText.Get("Image upload did not finish before the timeout."));
    }
    private async Task<JsonElement> SendOusAsync(Uri host, string token, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, new Uri(host, "ous/api/v2/" + path));
        request.Headers.Add("ous-token-v2", token); request.Content = content;
        return await SendAsync(request, ct).ConfigureAwait(false);
    }
    private HttpRequestMessage Gateway(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(LuxRegions.ApiBase(_credential.Region), path));
        request.Headers.Add("Authorization", _credential.Value); return request;
    }
    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct, bool isCreate = false)
    {
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string code = response.StatusCode == HttpStatusCode.Unauthorized ? "unauthenticated" : response.StatusCode == HttpStatusCode.TooManyRequests ? "rate_limited" : "http_error";
                throw new ProviderException(code, code == "unauthenticated" ? global::Lux3D.Core.LocalText.Get("The API key for the selected region is invalid or expired.") : global::Lux3D.Core.LocalText.Format("Service request failed (HTTP {0}).", (int)response.StatusCode), isCreate && (int)response.StatusCode >= 500,
                    retryAt: !isCreate ? RetryDeadline(response) : null);
            }
            if (response.Content.Headers.ContentLength > JsonLimit) throw new JsonException();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream(); byte[] chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            { if (buffer.Length + read > JsonLimit) throw new JsonException(); buffer.Write(chunk, 0, read); }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("c", out var codeElement) || !root.TryGetProperty("d", out var data)) throw new JsonException();
            // A malformed envelope is not proof that a billable create was rejected.
            string? responseCode = Scalar(codeElement);
            if (string.IsNullOrWhiteSpace(responseCode)) throw new JsonException();
            if (responseCode != "0") throw new ProviderException("business_rejected", global::Lux3D.Core.LocalText.Get("The service rejected the request. Check account permissions, balance and parameters on the official platform."));
            return data.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            if (isCreate) throw new ProviderException("submission_unknown", global::Lux3D.Core.LocalText.Get("The generation request has an unknown result. Check task history; do not resubmit."), true);
            if (ex is OperationCanceledException) throw;
            throw new ProviderException("request_failed", global::Lux3D.Core.LocalText.Get("Service request failed or returned an invalid response."));
        }
    }
    private static DateTimeOffset? RetryDeadline(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values) ||
            !System.Net.Http.Headers.RetryConditionHeaderValue.TryParse(values.FirstOrDefault(), out var retry)) return null;
        if (retry.Date is { } date) return date;
        if (retry.Delta is not { } delay) return null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return delay >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
    }
    private static string RequiredString(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString()) ? field.GetString()! : throw new ProviderException("invalid_response", global::Lux3D.Core.LocalText.Get("The service response is missing a required field."));
    private static string? Scalar(JsonElement value) => value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null };
}

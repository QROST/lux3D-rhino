using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lux3D.Core;
using Lux3D.Provider;
using Lux3D.Sidecar;

// Dedicated inherited stdin/stdout pipes: no listening TCP control port and no discovery secret.
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
using var http = Lux3DApiClient.CreateHttpClient();
if (args.Length == 2 && args[0] == "--oauth-discovery")
{
    var metadata = await new ManycoreOAuthService(http).DiscoverAsync(LuxRegions.Parse(args[1]));
    Console.WriteLine(JsonSerializer.Serialize(metadata, LuxJson.Options));
    return;
}
using var download = new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
    ConnectCallback = PublicNetworkConnector.ConnectAsync
}) { Timeout = TimeSpan.FromMinutes(3) };
var store = new TaskStore(TaskStore.DefaultRoot);
var workflow = new Workflow(store);
var credentials = CredentialStoreFactory.CreateDefault();
var sessionKeys = new Dictionary<string, ApiKeyCredential>();
var sessionTokens = new Dictionary<string, OAuthToken>();

if (args.Length == 2 && args[0] == "--recover-task")
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Guid operationId = Guid.Parse(args[1]);
        var result = (TaskRecord)await Dispatch(new RpcRequest(1, Guid.NewGuid(), "refresh", LuxJson.Element(new { operationId })), timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { result.OperationId, result.TaskId, result.State, result.AssetPath,
            result.AssetSha256, result.ValidationCode, result.Error }, LuxJson.Options));
        if (result.State != "Ready") Environment.ExitCode = 2;
    }
    catch (Exception ex)
    {
        string message = ex is ProviderException or InvalidOperationException ? ex.Message : "原任务恢复未完成，请检查任务编号、已保存的原区域凭证与网络。";
        Console.WriteLine(message); Environment.ExitCode = 2;
    }
    return;
}

if (args.Length == 2 && args[0] is "--oauth-login" or "--oauth-probe")
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        object result;
        if (args[0] == "--oauth-login") result = await Dispatch(new RpcRequest(1, Guid.NewGuid(), "login", LuxJson.Element(new { region = args[1] })), timeout.Token);
        else
        {
            string region = LuxRegions.Parse(args[1]) == LuxRegion.China ? "cn" : "global";
            OAuthToken token = await GetToken(region, timeout.Token) ?? throw new InvalidOperationException("请先完成 OAuth 登录。");
            result = await new OAuthMcpProbe(http).InspectAsync(token, timeout.Token);
        }
        Console.WriteLine(JsonSerializer.Serialize(result, LuxJson.Options));
    }
    catch (OperationCanceledException) { Console.WriteLine("OAuth 已取消或超时，未调用生成。请从 Rhino 面板重新登录。"); Environment.ExitCode = 2; }
    catch (ProviderException ex) { Console.WriteLine(ex.Message); Environment.ExitCode = 2; }
    catch (Exception) { Console.WriteLine("OAuth 未完成。请检查网络、系统凭证存储权限和账户授权后重试。"); Environment.ExitCode = 2; }
    return;
}

while (true)
{
    string? line;
    try { line = await BoundedLine.ReadAsync(Console.In, CancellationToken.None, 128 * 1024); }
    catch (IOException) { break; }
    if (line is null) break;
    Guid id = Guid.Empty;
    RpcResponse response;
    try
    {
        var request = JsonSerializer.Deserialize<RpcRequest>(line, LuxJson.Options) ?? throw new IOException("请求无效。");
        id = request.Id;
        if (request.Version != 1 || id == Guid.Empty) throw new IOException("协议版本不兼容。");
        using var timeout = new CancellationTokenSource(request.Method == "login" ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(3));
        object result = await Dispatch(request, timeout.Token);
        response = new(1, id, LuxJson.Element(result), null);
    }
    catch (Exception ex)
    {
        string message = ex switch
        {
            ProviderException p => p.Message,
            ArgumentException a => a.Message,
            InvalidOperationException o => o.Message,
            OperationCanceledException => "操作已取消或超时。远程任务可以从历史继续查询。",
            _ => "本地操作未完成。请检查后台文件权限、网络与连接；不要重复提交未知结果的生成。"
        };
        response = new(1, id, null, message);
    }
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, LuxJson.Options));
    await Console.Out.FlushAsync();
}

async Task<object> Dispatch(RpcRequest request, CancellationToken ct)
{
    JsonElement p = request.Payload;
    if (request.Method == "list") return store.List(p.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0, p.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 30);
    if (request.Method == "hasUnknown") return store.HasUnknownSubmissions();
    if (request.Method == "getTask") return store.Get(p.GetProperty("operationId").GetGuid());
    if (request.Method == "refresh")
    {
        Guid operationId = p.GetProperty("operationId").GetGuid();
        TaskRecord task = store.Get(operationId);
        ApiKeyCredential credential = await GetKey(task.Region, ct) ?? throw new InvalidOperationException("请恢复原任务的 API Key 连接。");
        return await workflow.RefreshAsync(new RestGenerationProvider(http, download, credential), operationId, ct);
    }
    LuxRegion selected = LuxRegions.Parse(p.GetProperty("region").GetString() ?? "");
    string region = selected == LuxRegion.China ? "cn" : "global";
    switch (request.Method)
    {
        case "connection":
        {
            bool key = await GetKey(region, ct) is not null;
            if (key) return new ConnectionInfo(region, true, "已配置区域 API Key；生成前请确认额度。", true);
            OAuthToken? token = await GetToken(region, ct);
            bool oauth = token is not null && token.ExpiresAt > DateTimeOffset.UtcNow;
            return new ConnectionInfo(region, key || oauth,
                key ? "已配置区域 API Key；生成前请确认额度。" : oauth ? "Manycore OAuth 已登录；生成 API 权限仍需独立验证或配置区域 API Key。" : "尚未连接。可登录 Manycore 或配置区域 API Key。", key);
        }
        case "setKey":
        {
            var key = new ApiKeyCredential(selected, p.GetProperty("apiKey").GetString() ?? "");
            await new Lux3DApiClient(http, key).VerifyConnectionAsync(ct);
            bool persist = p.GetProperty("persist").GetBoolean();
            if (persist) await credentials.SetAsync("api-key:" + region, p.GetProperty("apiKey").GetString()!, ct);
            else await credentials.DeleteAsync("api-key:" + region, ct);
            sessionKeys[region] = key;
            return new ConnectionInfo(region, true, persist ? "API 连接验证通过，已保存至系统凭证存储。" : "API 连接验证通过，仅本次会话使用。", true);
        }
        case "login":
        {
            var service = new ManycoreOAuthService(http);
            OAuthToken token = await service.LoginAsync(selected, uri =>
            {
                // This runs only in response to the user's login action in the panel.
                if (args.Length == 2 && args[0] == "--oauth-login")
                    Console.WriteLine("AUTHORIZE_URL=" + uri.AbsoluteUri);
                else Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }, ct);
            sessionTokens[region] = token;
            // User's OAuth session is recoverable; no token enters a task journal or Rhino document.
            await credentials.SetAsync("oauth:" + region, JsonSerializer.Serialize(token, LuxJson.Options), ct);
            OAuthMcpInspection inspection = await new OAuthMcpProbe(http).InspectAsync(token, ct);
            return new ConnectionInfo(region, true, "Manycore OAuth 登录成功，凭证已保存至系统凭证存储。" + inspection.Message + " 当前生成使用区域 API Key。", await GetKey(region, ct) is not null);
        }
        case "logout":
            sessionKeys.Remove(region); sessionTokens.Remove(region);
            await credentials.DeleteAsync("api-key:" + region, ct);
            await credentials.DeleteAsync("oauth:" + region, ct);
            return new ConnectionInfo(region, false, "本机连接已移除。远程授权可在官方账户页面管理。");
        case "generate":
        {
            ApiKeyCredential credential = await GetKey(region, ct) ?? throw new InvalidOperationException("OAuth 登录不等于生成 API 已授权。请配置并验证该区域 API Key 后生成。");
            GenerationInput input = p.GetProperty("input").Deserialize<GenerationInput>(LuxJson.Options) ?? throw new ArgumentException("输入为空。");
            return await workflow.GenerateAsync(new RestGenerationProvider(http, download, credential), input, ct);
        }
        default: throw new ArgumentException("未知操作。");
    }
}
async Task<ApiKeyCredential?> GetKey(string region, CancellationToken ct)
{
    if (sessionKeys.TryGetValue(region, out var current)) return current;
    string? value = await credentials.GetAsync("api-key:" + region, ct);
    if (value is null) return null;
    return sessionKeys[region] = new(LuxRegions.Parse(region), value);
}
async Task<OAuthToken?> GetToken(string region, CancellationToken ct)
{
    if (!sessionTokens.TryGetValue(region, out var token))
    {
        string? value = await credentials.GetAsync("oauth:" + region, ct);
        if (value is null) return null;
        token = JsonSerializer.Deserialize<OAuthToken>(value, LuxJson.Options);
        if (token is null) return null;
    }
    if (token.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1) && !string.IsNullOrEmpty(token.RefreshToken))
    {
        token = await new ManycoreOAuthService(http).RefreshAsync(token, ct);
        await credentials.SetAsync("oauth:" + region, JsonSerializer.Serialize(token, LuxJson.Options), ct);
    }
    return sessionTokens[region] = token;
}

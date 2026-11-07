using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lux3D.Core;
using Lux3D.Provider;
using Lux3D.Sidecar;

// Use the host's resolved language even when the operating system uses another language.
LocalText.Configure(null, Environment.GetEnvironmentVariable(LocalText.LanguageEnvironmentVariable) ?? System.Globalization.CultureInfo.CurrentUICulture.Name);

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
        string message = ex is ProviderException or InvalidOperationException ? ex.Message : global::Lux3D.Core.LocalText.Get("Original task recovery did not complete. Check its ID, saved credentials for the original region, and network.");
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
            OAuthToken token = await GetToken(region, timeout.Token) ?? throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Complete OAuth sign-in first."));
            result = await new OAuthMcpProbe(http).InspectAsync(token, timeout.Token);
        }
        Console.WriteLine(JsonSerializer.Serialize(result, LuxJson.Options));
    }
    catch (OperationCanceledException) { Console.WriteLine(global::Lux3D.Core.LocalText.Get("OAuth was cancelled or timed out; generation was not called. Sign in again from the Rhino panel.")); Environment.ExitCode = 2; }
    catch (ProviderException ex) { Console.WriteLine(ex.Message); Environment.ExitCode = 2; }
    catch (Exception) { Console.WriteLine(global::Lux3D.Core.LocalText.Get("OAuth did not complete. Check network, system credential permissions and account authorization, then try again.")); Environment.ExitCode = 2; }
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
        var request = JsonSerializer.Deserialize<RpcRequest>(line, LuxJson.Options) ?? throw new IOException(global::Lux3D.Core.LocalText.Get("Invalid request."));
        id = request.Id;
        if (request.Version != 1 || id == Guid.Empty) throw new IOException(global::Lux3D.Core.LocalText.Get("Incompatible protocol version."));
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
            OperationCanceledException => global::Lux3D.Core.LocalText.Get("Operation cancelled or timed out. Continue querying the remote task from history."),
            _ => global::Lux3D.Core.LocalText.Get("The local operation did not complete. Check background file permissions, network and connection; do not resubmit a generation with an unknown result.")
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
        ApiKeyCredential credential = await GetKey(task.Region, ct) ?? throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("Restore the original task's API key connection."));
        return await workflow.RefreshAsync(new RestGenerationProvider(http, download, credential), operationId, ct);
    }
    LuxRegion selected = LuxRegions.Parse(p.GetProperty("region").GetString() ?? "");
    string region = selected == LuxRegion.China ? "cn" : "global";
    switch (request.Method)
    {
        case "connection":
        {
            bool key = await GetKey(region, ct) is not null;
            if (key) return new ConnectionInfo(region, true, global::Lux3D.Core.LocalText.Get("Regional API key configured; check credits before generating."), true);
            OAuthToken? token = await GetToken(region, ct);
            bool oauth = token is not null && token.ExpiresAt > DateTimeOffset.UtcNow;
            return new ConnectionInfo(region, key || oauth,
                key ? global::Lux3D.Core.LocalText.Get("Regional API key configured; check credits before generating.") : oauth ? global::Lux3D.Core.LocalText.Get("Signed in to Manycore OAuth; generation API permissions need separate verification or a regional API key.") : global::Lux3D.Core.LocalText.Get("Not connected. Sign in to Manycore or configure a regional API key."), key);
        }
        case "setKey":
        {
            var key = new ApiKeyCredential(selected, p.GetProperty("apiKey").GetString() ?? "");
            await new Lux3DApiClient(http, key).VerifyConnectionAsync(ct);
            bool persist = p.GetProperty("persist").GetBoolean();
            if (persist) await credentials.SetAsync("api-key:" + region, p.GetProperty("apiKey").GetString()!, ct);
            else await credentials.DeleteAsync("api-key:" + region, ct);
            sessionKeys[region] = key;
            return new ConnectionInfo(region, true, persist ? global::Lux3D.Core.LocalText.Get("API connection verified and saved to system credential storage.") : global::Lux3D.Core.LocalText.Get("API connection verified for this session only."), true);
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
            return new ConnectionInfo(region, true, global::Lux3D.Core.LocalText.Get("Manycore OAuth sign-in succeeded; credentials were saved to system credential storage. ") + inspection.Message + global::Lux3D.Core.LocalText.Get(" Generation currently uses a regional API key."), await GetKey(region, ct) is not null);
        }
        case "logout":
            sessionKeys.Remove(region); sessionTokens.Remove(region);
            await credentials.DeleteAsync("api-key:" + region, ct);
            await credentials.DeleteAsync("oauth:" + region, ct);
            return new ConnectionInfo(region, false, global::Lux3D.Core.LocalText.Get("Local connection removed. Manage remote authorizations on the official account page."));
        case "generate":
        {
            ApiKeyCredential credential = await GetKey(region, ct) ?? throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("OAuth sign-in does not authorize the generation API. Configure and verify the regional API key before generating."));
            GenerationInput input = p.GetProperty("input").Deserialize<GenerationInput>(LuxJson.Options) ?? throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Input is empty."));
            return await workflow.GenerateAsync(new RestGenerationProvider(http, download, credential), input, ct);
        }
        default: throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Unknown operation."));
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

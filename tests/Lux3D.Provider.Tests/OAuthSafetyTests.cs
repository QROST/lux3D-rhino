using System.Net;
using System.Text;
using System.Text.Json;
using Lux3D.Provider;
using Xunit;

namespace Lux3D.Provider.Tests;

public sealed class OAuthSafetyTests
{
    private const string Metadata = "{\"issuer\":\"https://www.aholo.ai\",\"authorization_endpoint\":\"https://www.aholo.ai/oauth/authorize\",\"token_endpoint\":\"https://www.aholo.ai/oauth/token\",\"registration_endpoint\":\"https://www.aholo.ai/oauth/register\",\"scopes_supported\":[\"get_user_info\"],\"grant_types_supported\":[\"authorization_code\",\"refresh_token\"],\"code_challenge_methods_supported\":[\"S256\"],\"token_endpoint_auth_methods_supported\":[\"none\"]}";
    [Theory]
    [InlineData("http://127.0.0.1:34567/callback?state={state}&code=%GG")]
    [InlineData("http://127.0.0.1:34567/callback?state={state}&code=%")]
    [InlineData("http://127.0.0.1:34567/callback?state={state}&code=%0d%0a")]
    [InlineData("http://127.0.0.1:34567/path/../callback?state={state}&code=valid")]
    [InlineData("http://127.0.0.1:34567/callback/extra?state={state}&code=valid")]
    [InlineData("http://127.0.0.1:34567/%63allback?state={state}&code=valid")]
    public void CallbackRejectsMalformedEncodingAndPath(string target)
    {
        var attempt = OAuthAttempt.Create(new Uri("http://127.0.0.1:34567/callback"));
        Assert.Throws<ProviderException>(() => attempt.ValidateCallback(new Uri(target.Replace("{state}", attempt.State))));
    }
    [Theory]
    [InlineData("token_type", "Basic")]
    [InlineData("scope", "get_user_info other_scope")]
    [InlineData("scope", 42)]
    [InlineData("expires_in", "not-number")]
    [InlineData("refresh_token", "")]
    [InlineData("refresh_token", "contains\ncontrol")]
    public async Task RefreshRejectsMalformedTokenFields(string field, object replacement)
    {
        var response = new Dictionary<string, object> { ["access_token"] = "new-access", ["token_type"] = "Bearer", ["expires_in"] = 3600, ["scope"] = "get_user_info", ["refresh_token"] = "new-refresh" };
        response[field] = replacement;
        var service = new ManycoreOAuthService(new HttpClient(Handler(JsonSerializer.Serialize(response))));
        await Assert.ThrowsAsync<ProviderException>(() => service.RefreshAsync(Token()));
    }
    [Fact]
    public async Task RefreshRejectsOversizedRefreshAndKeepsOldTokenWhenRotationOmitted()
    {
        var invalid = new ManycoreOAuthService(new HttpClient(Handler(JsonSerializer.Serialize(new { access_token = "new-access", token_type = "Bearer", refresh_token = new string('x', 32769) }))));
        await Assert.ThrowsAsync<ProviderException>(() => invalid.RefreshAsync(Token()));
        var valid = new ManycoreOAuthService(new HttpClient(Handler("{\"access_token\":\"new-access\",\"token_type\":\"Bearer\",\"expires_in\":3600}")));
        var previous = Token(); var next = await valid.RefreshAsync(previous);
        Assert.Equal(previous.RefreshToken, next.RefreshToken); Assert.Equal(previous.ConnectionId, next.ConnectionId); Assert.Equal("get_user_info", next.Scope);
    }
    private static ProviderTests.Stub Handler(string tokenResponse) => new(request => Task.FromResult(ProviderTests.Json(request.RequestUri!.Host == "api.aholo.ai"
        ? "{\"resource\":\"https://api.aholo.ai\",\"authorization_servers\":[\"https://www.aholo.ai\"]}"
        : request.RequestUri.AbsolutePath.EndsWith("oauth-authorization-server") ? Metadata : tokenResponse)));
    internal static OAuthToken Token() => new() { Region = LuxRegion.International, AccessToken = "fake-access", RefreshToken = "fake-refresh", ClientId = "fake-public-client", Scope = "get_user_info", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
}

public sealed class OAuthMcpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeOnlyListsToolsAndPreservesNegotiatedSession(bool sse)
    {
        var methods = new List<string>();
        var handler = new ProviderTests.Stub(async request =>
        {
            Assert.Equal("https://api.aholo.ai/lux3d-mcp/mcp", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("fake-access", request.Headers.Authorization.Parameter);
            Assert.Contains(request.Headers.Accept, h => h.MediaType == "application/json"); Assert.Contains(request.Headers.Accept, h => h.MediaType == "text/event-stream");
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            string method = body.GetProperty("method").GetString()!; methods.Add(method);
            if (method == "initialize")
            {
                Assert.False(request.Headers.Contains("Mcp-Session-Id"));
                var result = Reply(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = "2025-03-26", capabilities = new { tools = new { } } } }, sse);
                result.Headers.Add("Mcp-Session-Id", "opaque-test-session"); return result;
            }
            Assert.Equal("opaque-test-session", request.Headers.GetValues("Mcp-Session-Id").Single());
            Assert.Equal("2025-03-26", request.Headers.GetValues("MCP-Protocol-Version").Single());
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            Assert.Equal("tools/list", method);
            int id = body.GetProperty("id").GetInt32();
            return id == 2 ? Reply(new { jsonrpc = "2.0", id, result = new { tools = new[] { new { name = "gettask" } }, nextCursor = "next" } }, sse)
                : Reply(new { jsonrpc = "2.0", id, result = new { tools = new[] { new { name = "post_createtextto3dtask" } } } }, sse);
        });
        var inspection = await new OAuthMcpProbe(new HttpClient(handler)).InspectAsync(OAuthSafetyTests.Token());
        Assert.True(inspection.Available, inspection.Message);
        Assert.Equal(new[] { "gettask", "post_createtextto3dtask" }, inspection.ToolNames);
        Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list", "tools/list" }, methods);
    }
    [Fact]
    public async Task ProbeRejectsAuthWithoutSurfacingSensitiveBody()
    {
        var handler = new ProviderTests.Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("do not leak fake-access or account") }));
        var result = await new OAuthMcpProbe(new HttpClient(handler)).InspectAsync(OAuthSafetyTests.Token());
        Assert.False(result.Available); Assert.DoesNotContain("fake-access", result.Message); Assert.Equal(1, handler.Count);
    }
    [Fact]
    public async Task ProbeRejectsOversizedResponseAndMismatchedId()
    {
        foreach (var response in new[] {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1), Encoding.UTF8, "application/json") },
            Reply(new { jsonrpc = "2.0", id = 99, result = new { protocolVersion = "2025-11-25" } }, false) })
        {
            var handler = new ProviderTests.Stub(_ => Task.FromResult(response));
            var result = await new OAuthMcpProbe(new HttpClient(handler)).InspectAsync(OAuthSafetyTests.Token());
            Assert.False(result.Available); Assert.Equal(1, handler.Count);
        }
    }
    private static HttpResponseMessage Reply(object body, bool sse)
    {
        string json = JsonSerializer.Serialize(body);
        return sse ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(": heartbeat\n\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}\n\ndata: " + json + "\n\n", Encoding.UTF8, "text/event-stream") }
            : ProviderTests.Json(json);
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lux3D.Provider;
using Xunit;

namespace Lux3D.Provider.Tests;

public sealed class ProviderTests
{
    [Fact]
    public async Task InternationalCreateUsesRawKeyAndExactInt64()
    {
        var handler = new Stub(async request =>
        {
            Assert.Equal("https://api.aholo.ai/global/lux3d/v1/generate/text-to-3d/task/create", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-only-key", request.Headers.GetValues("Authorization").Single());
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal("G1-Turbo", body.GetProperty("version").GetString());
            Assert.Equal("glb", body.GetProperty("outputFormat")[0].GetString());
            return Json("{\"c\":\"0\",\"d\":9223372036854775806}");
        });
        var api = new Lux3DApiClient(new HttpClient(handler), new(LuxRegion.International, "test-only-key"));
        Assert.Equal("9223372036854775806", await api.CreateTextAsync("chair"));
        Assert.Equal(1, handler.Count);
    }
    [Fact]
    public async Task G1OmitsUnsupportedPbrField()
    {
        var handler = new Stub(async request =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.False(body.TryGetProperty("enablePbr", out _)); return Json("{\"c\":\"0\",\"d\":42}");
        });
        await new Lux3DApiClient(new HttpClient(handler), new(LuxRegion.China, "fake")).CreateTextAsync("chair", "G1");
    }
    [Fact]
    public async Task CreateTransportFailureIsUnknownAndNeverRetried()
    {
        var handler = new Stub(_ => throw new HttpRequestException("do not surface raw request"));
        var api = new Lux3DApiClient(new HttpClient(handler), new(LuxRegion.China, "fake"));
        var error = await Assert.ThrowsAsync<ProviderException>(() => api.CreateTextAsync("chair"));
        Assert.True(error.SubmissionMayHaveSucceeded); Assert.Equal(1, handler.Count);
        Assert.DoesNotContain("raw request", error.Message);
    }
    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    public async Task MalformedCreateReceiptIsUnknownAndNeverRetried(string code)
    {
        var handler = new Stub(_ => Task.FromResult(Json("{\"c\":" + code + ",\"d\":42}")));
        var api = new Lux3DApiClient(new HttpClient(handler), new(LuxRegion.China, "fake"));
        var error = await Assert.ThrowsAsync<ProviderException>(() => api.CreateTextAsync("chair"));
        Assert.True(error.SubmissionMayHaveSucceeded);
        Assert.Equal("submission_unknown", error.Code);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("null", "3")]
    [InlineData("42", "3")]
    [InlineData("\"LUX_3D\"", "\"3\"")]
    [InlineData("\"LUX_3D\"", "null")]
    public async Task MalformedTaskFieldsReturnControlledError(string biz, string status)
    {
        var handler = new Stub(_ => Task.FromResult(Json("{\"c\":0,\"d\":{\"taskId\":42,\"bizId\":" + biz + ",\"status\":" + status + "}}")));
        var api = new Lux3DApiClient(new HttpClient(handler), new(LuxRegion.China, "fake"));
        var error = await Assert.ThrowsAsync<ProviderException>(() => api.GetTaskAsync("42"));
        Assert.Equal("invalid_task", error.Code);
    }

    [Fact]
    public async Task RejectsMismatchedTaskIdentity()
    {
        var handler = new Stub(_ => Task.FromResult(Json("{\"c\":\"0\",\"d\":{\"taskId\":43,\"bizId\":\"LUX_3D\",\"status\":3,\"outputs\":[{\"content\":\"https://cdn.example/a.glb\"}]}}")));
        var api = new Lux3DApiClient(new HttpClient(handler), new(LuxRegion.China, "fake"));
        await Assert.ThrowsAsync<ProviderException>(() => api.GetTaskAsync("42"));
    }
    [Theory]
    [InlineData(2, "running")]
    [InlineData(5, "failed")]
    [InlineData(6, "cancelled")]
    [InlineData(99, "unknown")]
    public async Task PreservesProviderStatus(int status, string state)
    {
        var handler = new Stub(_ => Task.FromResult(Json(JsonSerializer.Serialize(new { c = "0", d = new { taskId = 42, bizId = "LUX_3D", status } }))));
        var task = await new Lux3DApiClient(new HttpClient(handler), new(LuxRegion.China, "fake")).GetTaskAsync("42");
        Assert.Equal(status, task.Status); Assert.Equal(state, task.State); Assert.Null(task.GlbUri);
    }
    [Fact]
    public async Task FindsUniqueGlbAndIgnoresNotRequested()
    {
        var handler = new Stub(_ => Task.FromResult(Json("{\"c\":\"0\",\"d\":{\"taskId\":42,\"bizId\":\"LUX_3D\",\"status\":3,\"outputs\":[{\"content\":\"NOT_REQUESTED\"},{\"content\":\"https://cdn.example/a.glb?signature=fake\"}]}}")));
        var task = await new Lux3DApiClient(new HttpClient(handler), new(LuxRegion.China, "fake")).GetTaskAsync("42");
        Assert.Equal("/a.glb", task.GlbUri!.AbsolutePath);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"outputs\":[]}")]
    [InlineData("{\"outputs\":[{\"content\":\"https://cdn.example/model.zip\"}]}")]
    [InlineData("{\"outputs\":[{\"content\":\"NOT_REQUESTED\"}]}")]
    [InlineData("{\"outputs\":[{\"content\":123}]}")]
    [InlineData("{\"outputs\":[{\"content\":\"unrecognized result format\"}]}")]
    public async Task SuccessfulTaskWithUnusableOutputPreservesRemoteSuccess(string outputJson)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(outputJson)!;
        data["taskId"] = 42; data["bizId"] = "LUX_3D"; data["status"] = 3;
        var handler = new Stub(_ => Task.FromResult(Json(JsonSerializer.Serialize(new { c = "0", d = data }))));
        var task = await new Lux3DApiClient(new HttpClient(handler), new(LuxRegion.China, "fake")).GetTaskAsync("42");
        Assert.Equal(3, task.Status); Assert.Equal("succeeded", task.State);
        Assert.Null(task.GlbUri); Assert.False(string.IsNullOrWhiteSpace(task.OutputError));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task UploadUsesDynamicPartsAndIsolatesCredentialHeaders()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4, 5 });
        try
        {
            var seen = new List<string>();
            var handler = new Stub(async request =>
            {
                seen.Add(request.RequestUri!.AbsolutePath);
                if (request.RequestUri.Host == "api.aholo.ai")
                { Assert.True(request.Headers.Contains("Authorization")); Assert.False(request.Headers.Contains("ous-token-v2")); return Json("{\"c\":\"0\",\"d\":{\"ousToken\":\"fake-ous\",\"globalDomain\":\"https://ous.example\",\"blockSize\":2}}"); }
                Assert.False(request.Headers.Contains("Authorization")); Assert.Equal("fake-ous", request.Headers.GetValues("ous-token-v2").Single());
                if (request.RequestUri.AbsolutePath.EndsWith("/init")) Assert.Contains("blocks=3", await request.Content!.ReadAsStringAsync());
                return request.Method == HttpMethod.Get ? Json("{\"c\":\"0\",\"d\":{\"status\":5,\"url\":\"https://cdn.example/image.png\"}}") : Json("{\"c\":\"0\",\"d\":{}}");
            });
            var uri = await new Lux3DApiClient(new HttpClient(handler), new(LuxRegion.International, "fake-gateway")).UploadImageAsync(path);
            Assert.Equal("cdn.example", uri.Host); Assert.Equal(3, seen.Count(p => p.EndsWith("/part")));
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("null", 100)]
    [InlineData("[]", 100)]
    [InlineData("null", 2)]
    [InlineData("[]", 2)]
    public async Task MalformedUploadPayloadReturnsControlledError(string payload, int blockSize)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4, 5 });
        try
        {
            var handler = new Stub(request => Task.FromResult(Json(request.RequestUri!.Host == "api.aholo.ai"
                ? JsonSerializer.Serialize(new { c = 0, d = new { ousToken = "fake", globalDomain = "https://ous.example", blockSize } })
                : "{\"c\":0,\"d\":" + payload + "}")));
            var api = new Lux3DApiClient(new HttpClient(handler), new(LuxRegion.International, "fake"));
            var error = await Assert.ThrowsAsync<ProviderException>(() => api.UploadImageAsync(path));
            Assert.Equal("invalid_upload", error.Code);
            Assert.False(error.SubmissionMayHaveSucceeded);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CredentialsNeverPrintSecretsAndAreRegionBound()
    {
        var first = new ApiKeyCredential(LuxRegion.China, "secret-test"); var second = new ApiKeyCredential(LuxRegion.International, "secret-test");
        Assert.DoesNotContain("secret-test", first.ToString()); Assert.NotEqual(first.ConnectionId, second.ConnectionId);
        Assert.Throws<ArgumentException>(() => new ApiKeyCredential(LuxRegion.China, "Bearer token"));
        Assert.Equal(LuxRegion.International, LuxRegions.Parse("global"));
    }
    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    internal sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Count++; return action(request); }
    }
}

public sealed class OAuthTests
{
    [Fact]
    public void PkceUsesS256AndRejectsStateAndReplay()
    {
        var attempt = OAuthAttempt.Create(new Uri("http://127.0.0.1:34567/callback"));
        string expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(attempt.Verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, attempt.Challenge); Assert.InRange(attempt.Verifier.Length, 43, 128);
        Assert.Throws<ProviderException>(() => attempt.ValidateCallback(new Uri("http://127.0.0.1:34567/callback?state=wrong&code=fake")));
        var callback = new Uri("http://127.0.0.1:34567/callback?state=" + attempt.State + "&code=fake");
        Assert.Equal("fake", attempt.ValidateCallback(callback));
        Assert.Throws<ProviderException>(() => attempt.ValidateCallback(callback));
    }
    [Fact]
    public void CallbackRejectsWrongOriginDuplicateParametersAndDenial()
    {
        var a = OAuthAttempt.Create(new Uri("http://127.0.0.1:34567/callback"));
        Assert.Throws<ProviderException>(() => a.ValidateCallback(new Uri("https://evil.example/callback?state=" + a.State + "&code=fake")));
        Assert.Throws<ProviderException>(() => a.ValidateCallback(new Uri("http://127.0.0.1:34567/callback?state=" + a.State + "&state=" + a.State + "&code=fake")));
        Assert.Throws<ProviderException>(() => a.ValidateCallback(new Uri("http://127.0.0.1:34567/callback?state=" + a.State + "&error=access_denied")));
    }
    [Fact]
    public async Task DiscoveryRejectsEndpointOutsideVerifiedIssuer()
    {
        var handler = new ProviderTests.Stub(r => Task.FromResult(ProviderTests.Json(r.RequestUri!.Host == "api.aholo.ai"
            ? "{\"resource\":\"https://api.aholo.ai\",\"authorization_servers\":[\"https://www.aholo.ai\"]}"
            : "{\"issuer\":\"https://www.aholo.ai\",\"authorization_endpoint\":\"https://evil.example/authorize\",\"token_endpoint\":\"https://www.aholo.ai/oauth/token\",\"registration_endpoint\":\"https://www.aholo.ai/oauth/register\",\"scopes_supported\":[\"get_user_info\"],\"grant_types_supported\":[\"authorization_code\"],\"code_challenge_methods_supported\":[\"S256\"],\"token_endpoint_auth_methods_supported\":[\"none\"]}")));
        await Assert.ThrowsAsync<ProviderException>(() => new ManycoreOAuthService(new HttpClient(handler)).DiscoverAsync(LuxRegion.International));
    }
    [Fact]
    public async Task FullLoginRegistersPkceAndCompletesLoopbackWithoutLeakingApiKey()
    {
        Uri? registeredRedirect = null;
        string? challenge = null;
        Task? callback = null;
        var handler = new ProviderTests.Stub(async request =>
        {
            Assert.False(request.Headers.Contains("Authorization"));
            if (request.RequestUri!.AbsolutePath.EndsWith("oauth-protected-resource"))
                return ProviderTests.Json("{\"resource\":\"https://api.aholo.ai\",\"authorization_servers\":[\"https://www.aholo.ai\"]}");
            if (request.RequestUri.AbsolutePath.EndsWith("oauth-authorization-server"))
                return ProviderTests.Json("{\"issuer\":\"https://www.aholo.ai\",\"authorization_endpoint\":\"https://www.aholo.ai/oauth/authorize\",\"token_endpoint\":\"https://www.aholo.ai/oauth/token\",\"registration_endpoint\":\"https://www.aholo.ai/oauth/register\",\"scopes_supported\":[\"get_user_info\"],\"grant_types_supported\":[\"authorization_code\",\"refresh_token\"],\"code_challenge_methods_supported\":[\"S256\"],\"token_endpoint_auth_methods_supported\":[\"none\"]}");
            if (request.RequestUri.AbsolutePath.EndsWith("/register"))
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                registeredRedirect = new Uri(body.GetProperty("redirect_uris")[0].GetString()!);
                Assert.Equal("127.0.0.1", registeredRedirect.Host);
                Assert.Equal("none", body.GetProperty("token_endpoint_auth_method").GetString());
                return ProviderTests.Json("{\"client_id\":\"public-test-client\",\"token_endpoint_auth_method\":\"none\"}");
            }
            var fields = Query(await request.Content!.ReadAsStringAsync());
            Assert.Equal("test-code", fields["code"]);
            Assert.Equal(registeredRedirect!.AbsoluteUri, fields["redirect_uri"]);
            Assert.Equal(challenge, Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
            return ProviderTests.Json("{\"access_token\":\"fake-access\",\"refresh_token\":\"fake-refresh\",\"token_type\":\"Bearer\",\"expires_in\":3600,\"scope\":\"get_user_info\"}");
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = await new ManycoreOAuthService(new HttpClient(handler)).LoginAsync(LuxRegion.International, url =>
        {
            var fields = Query(url.Query.TrimStart('?')); challenge = fields["code_challenge"];
            Assert.Equal("S256", fields["code_challenge_method"]);
            callback = Task.Run(async () =>
            {
                using var local = new HttpClient();
                using var response = await local.GetAsync(registeredRedirect + "?state=" + fields["state"] + "&code=test-code", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }, timeout.Token);
        }, timeout.Token);
        await callback!;
        Assert.Equal("fake-access", token.AccessToken); Assert.Equal(4, handler.Count);
    }
    private static Dictionary<string, string> Query(string text) => text.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    [Fact]
    public void TokenRoundTripKeepsStableIdentityAcrossRefreshAndRedactsToString()
    {
        var one = new OAuthToken { Region = LuxRegion.International, AccessToken = "fake-access-one", ClientId = "fake-client", RefreshToken = "fake-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var two = JsonSerializer.Deserialize<OAuthToken>(JsonSerializer.Serialize(one))!;
        Assert.Equal(one.AccessToken, two.AccessToken); Assert.DoesNotContain(one.AccessToken, one.ToString());
        Assert.Equal(one.ConnectionId, new OAuthToken { Region = one.Region, AccessToken = "fake-access-two", ClientId = one.ClientId }.ConnectionId);
    }
}

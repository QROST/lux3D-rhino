using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lux3D.Provider;

public sealed class OAuthToken
{
    public LuxRegion Region { get; init; }
    public string AccessToken { get; init; } = "";
    public string? RefreshToken { get; init; }
    public string ClientId { get; init; } = "";
    public string TokenType { get; init; } = "Bearer";
    public string Scope { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
    // Stable across refreshes; a new client registration creates a distinct local account binding.
    public string ConnectionId => "oauth:" + LuxRegions.Name(Region) + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ClientId))).ToLowerInvariant();
    public override string ToString() => $"OAuthToken({LuxRegions.Name(Region)}, [redacted])";
}

public sealed record OAuthServerMetadata(Uri Issuer, Uri AuthorizationEndpoint, Uri TokenEndpoint, Uri RegistrationEndpoint, Uri Resource);

/// <summary>Official discovery + dynamic registration + authorization-code PKCE. This is not API-key authentication.</summary>
public sealed class ManycoreOAuthService
{
    private readonly HttpClient _http;
    public ManycoreOAuthService(HttpClient http) { _http = http; }
    public async Task<OAuthServerMetadata> DiscoverAsync(LuxRegion region, CancellationToken cancellationToken = default)
    {
        string api = region == LuxRegion.China ? "https://api.aholo3d.cn" : "https://api.aholo.ai";
        string issuer = region == LuxRegion.China ? "https://www.aholo3d.cn" : "https://www.aholo.ai";
        using var resourceRequest = new HttpRequestMessage(HttpMethod.Get, api + "/.well-known/oauth-protected-resource");
        var resource = await ReadJsonAsync(resourceRequest, cancellationToken).ConfigureAwait(false);
        if (String(resource, "resource") != api || !Contains(resource, "authorization_servers", issuer)) throw Error(global::Lux3D.Core.LocalText.Get("Official OAuth resource metadata does not match."));
        using var authRequest = new HttpRequestMessage(HttpMethod.Get, issuer + "/.well-known/oauth-authorization-server");
        var metadata = await ReadJsonAsync(authRequest, cancellationToken).ConfigureAwait(false);
        if (String(metadata, "issuer") != issuer || !Contains(metadata, "code_challenge_methods_supported", "S256") ||
            !Contains(metadata, "grant_types_supported", "authorization_code") || !Contains(metadata, "token_endpoint_auth_methods_supported", "none") ||
            !Contains(metadata, "scopes_supported", "get_user_info")) throw Error(global::Lux3D.Core.LocalText.Get("Official OAuth does not support the required public-client PKCE flow."));
        Uri Endpoint(string property)
        {
            var uri = ProviderValidation.HttpsUri(String(metadata, property));
            if (uri.GetLeftPart(UriPartial.Authority) != issuer || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query)) throw Error(global::Lux3D.Core.LocalText.Get("The OAuth endpoint does not match the verified issuer."));
            return uri;
        }
        return new(new Uri(issuer), Endpoint("authorization_endpoint"), Endpoint("token_endpoint"), Endpoint("registration_endpoint"), new Uri(api));
    }
    public async Task<OAuthToken> LoginAsync(LuxRegion region, Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5)); var ct = timeout.Token;
        var metadata = await DiscoverAsync(region, ct).ConfigureAwait(false);
        // Bind before registering/opening the browser. OS-selected port and loopback-only interface.
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = new Uri($"http://127.0.0.1:{port}/callback");
        string clientId = await RegisterAsync(metadata, redirectUri, ct).ConfigureAwait(false);
        var attempt = OAuthAttempt.Create(redirectUri);
        openBrowser(attempt.AuthorizationUri(metadata, clientId));
        string code = await ReceiveCodeAsync(listener, attempt, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, metadata.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
                ["redirect_uri"] = redirectUri.AbsoluteUri, ["code_verifier"] = attempt.Verifier,
                ["resource"] = metadata.Resource.AbsoluteUri.TrimEnd('/')
            })
        };
        var token = await ReadJsonAsync(request, ct).ConfigureAwait(false);
        return ParseToken(token, region, clientId, null);
    }
    public async Task<OAuthToken> RefreshAsync(OAuthToken previous, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(previous.RefreshToken)) throw Error(global::Lux3D.Core.LocalText.Get("Your sign-in has expired. Sign in again."));
        ValidateOpaque(previous.RefreshToken, "refresh token");
        if (!string.Equals(previous.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(previous.ClientId) || previous.ClientId.Length > 2048 || previous.ClientId.Any(char.IsControl)) throw Error(global::Lux3D.Core.LocalText.Get("Invalid OAuth session format. Sign in again."));
        ValidateScope(previous.Scope);
        var metadata = await DiscoverAsync(previous.Region, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, metadata.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["client_id"] = previous.ClientId,
                ["refresh_token"] = previous.RefreshToken, ["resource"] = metadata.Resource.AbsoluteUri.TrimEnd('/')
            })
        };
        return ParseToken(await ReadJsonAsync(request, cancellationToken).ConfigureAwait(false), previous.Region, previous.ClientId, previous);
    }
    private async Task<string> RegisterAsync(OAuthServerMetadata metadata, Uri redirect, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, metadata.RegistrationEndpoint)
        {
            Content = JsonContent.Create(new
            {
                client_name = "Lux3D for Rhino", redirect_uris = new[] { redirect.AbsoluteUri },
                grant_types = new[] { "authorization_code", "refresh_token" }, response_types = new[] { "code" },
                token_endpoint_auth_method = "none", scope = "get_user_info"
            })
        };
        var registration = await ReadJsonAsync(request, ct).ConfigureAwait(false);
        if (registration.TryGetProperty("token_endpoint_auth_method", out var method) && method.GetString() != "none") throw Error(global::Lux3D.Core.LocalText.Get("The service returned an unsupported client authentication method."));
        string clientId = String(registration, "client_id");
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 2048 || clientId.Any(char.IsControl)) throw Error(global::Lux3D.Core.LocalText.Get("OAuth registration did not return a valid client_id."));
        return clientId;
    }
    private static OAuthToken ParseToken(JsonElement value, LuxRegion region, string clientId, OAuthToken? previous)
    {
        string access = String(value, "access_token"), type = String(value, "token_type");
        ValidateOpaque(access, "access token");
        if (!type.Equals("Bearer", StringComparison.OrdinalIgnoreCase)) throw Error(global::Lux3D.Core.LocalText.Get("OAuth did not return a valid Bearer token."));
        long seconds = 3600;
        if (value.TryGetProperty("expires_in", out var expires) && (expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt64(out seconds) || seconds <= 0 || seconds > 365 * 24 * 3600)) throw Error(global::Lux3D.Core.LocalText.Get("Invalid OAuth token lifetime."));
        string? refresh = previous?.RefreshToken;
        if (value.TryGetProperty("refresh_token", out var returnedRefresh))
        {
            if (returnedRefresh.ValueKind != JsonValueKind.String) throw Error(global::Lux3D.Core.LocalText.Get("Invalid OAuth refresh token format."));
            refresh = returnedRefresh.GetString(); ValidateOpaque(refresh, "refresh token");
        }
        string scope = previous?.Scope ?? "get_user_info";
        if (value.TryGetProperty("scope", out var returnedScope))
        {
            if (returnedScope.ValueKind != JsonValueKind.String) throw Error(global::Lux3D.Core.LocalText.Get("Invalid OAuth scope format."));
            scope = returnedScope.GetString()!;
        }
        ValidateScope(scope);
        return new OAuthToken { Region = region, ClientId = clientId, AccessToken = access, TokenType = "Bearer", RefreshToken = refresh, Scope = scope, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds) };
    }
    private static void ValidateOpaque(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32768 || value.Any(c => c < 0x21 || c > 0x7e)) throw Error(global::Lux3D.Core.LocalText.Format("Invalid OAuth {0} format.", label));
    }
    private static void ValidateScope(string scope)
    {
        // Only the explicitly requested, metadata-advertised scope is accepted.
        if (scope != "get_user_info") throw Error(global::Lux3D.Core.LocalText.Get("The OAuth scope does not match the requested scope."));
    }

    private async Task<JsonElement> ReadJsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Error(global::Lux3D.Core.LocalText.Format("OAuth request failed (HTTP {0}).", (int)response.StatusCode));
        if (response.Content.Headers.ContentLength > 128 * 1024) throw Error(global::Lux3D.Core.LocalText.Get("The OAuth response is too large."));
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(); byte[] chunk = new byte[4096]; int n;
        while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0) { if (buffer.Length + n > 128 * 1024) throw Error(global::Lux3D.Core.LocalText.Get("The OAuth response is too large.")); buffer.Write(chunk, 0, n); }
        try { using var json = JsonDocument.Parse(buffer.ToArray()); if (json.RootElement.ValueKind != JsonValueKind.Object) throw Error(global::Lux3D.Core.LocalText.Get("Invalid OAuth response format.")); return json.RootElement.Clone(); }
        catch (JsonException) { throw Error(global::Lux3D.Core.LocalText.Get("Invalid OAuth response format.")); }
    }
    private static async Task<string> ReceiveCodeAsync(TcpListener listener, OAuthAttempt attempt, CancellationToken ct)
    {
        while (true)
        {
            using var socket = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var stream = socket.GetStream();
            var request = new StringBuilder(); byte[] one = new byte[1];
            while (request.Length < 16384)
            {
                if (await stream.ReadAsync(one, requestTimeout.Token).ConfigureAwait(false) == 0) break;
                request.Append((char)one[0]);
                if (request.Length >= 4 && request.ToString(request.Length - 4, 4) == "\r\n\r\n") break;
            }
            string? code = null; bool denied = false;
            try
            {
                string[] line = request.ToString().Split('\r')[0].Split(' ');
                if (line.Length == 3 && line[0] == "GET" && line[1].StartsWith("/callback?", StringComparison.Ordinal))
                    code = attempt.ValidateCallback(new Uri(attempt.RedirectUri, line[1]));
            }
            catch (ProviderException) { denied = true; }
            string body = code is not null ? global::Lux3D.Core.LocalText.Get("Authorization callback received. Return to Rhino to see the sign-in result.") : global::Lux3D.Core.LocalText.Get("Invalid sign-in callback. Return to Rhino and try again.");
            byte[] payload = Encoding.UTF8.GetBytes(body);
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(code is not null ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, ct).ConfigureAwait(false); await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            if (code is not null) return code;
            if (denied) throw Error(global::Lux3D.Core.LocalText.Get("OAuth authorization was denied or the callback state did not match. Sign in again."));
        }
    }
    private static bool Contains(JsonElement value, string name, string item) => value.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array && list.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == item);
    private static string String(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
    private static ProviderException Error(string message) => new("oauth_error", message);
}

public sealed class OAuthAttempt
{
    public string State { get; }
    public string Verifier { get; }
    public string Challenge { get; }
    public Uri RedirectUri { get; }
    private bool _consumed;
    private OAuthAttempt(Uri redirect) { RedirectUri = redirect; State = Base64Url(RandomNumberGenerator.GetBytes(32)); Verifier = Base64Url(RandomNumberGenerator.GetBytes(32)); Challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier))); }
    public static OAuthAttempt Create(Uri redirect)
    {
        if (redirect.Scheme != "http" || redirect.Host != "127.0.0.1" || redirect.Port <= 0 || !string.IsNullOrEmpty(redirect.UserInfo) || !string.IsNullOrEmpty(redirect.Query) || !string.IsNullOrEmpty(redirect.Fragment)) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("The OAuth redirect must be a local IPv4 loopback URL."), nameof(redirect));
        return new(redirect);
    }
    public Uri AuthorizationUri(OAuthServerMetadata metadata, string clientId)
    {
        var values = new Dictionary<string, string> { ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = RedirectUri.AbsoluteUri, ["scope"] = "get_user_info", ["state"] = State, ["code_challenge"] = Challenge, ["code_challenge_method"] = "S256", ["resource"] = metadata.Resource.AbsoluteUri.TrimEnd('/') };
        return new Uri(metadata.AuthorizationEndpoint.AbsoluteUri + "?" + string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
    }
    public string ValidateCallback(Uri callback)
    {
        if (HasMalformedEscape(callback.OriginalString) || callback.UserInfo.Length > 0 || !callback.OriginalString.StartsWith(RedirectUri.AbsoluteUri + "?", StringComparison.Ordinal)) throw new ProviderException("oauth_callback", global::Lux3D.Core.LocalText.Get("Invalid OAuth callback encoding."));
        if (_consumed || callback.GetLeftPart(UriPartial.Path) != RedirectUri.GetLeftPart(UriPartial.Path) || !string.IsNullOrEmpty(callback.Fragment)) throw new ProviderException("oauth_callback", global::Lux3D.Core.LocalText.Get("The OAuth callback URL is invalid or has already been used."));
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] p = pair.Split('=', 2); string key = Uri.UnescapeDataString(p[0]);
            if (key.Any(char.IsControl)) throw new ProviderException("oauth_callback", global::Lux3D.Core.LocalText.Get("Invalid OAuth callback parameters."));
            if (!pairs.TryAdd(key, p.Length == 2 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "")) throw new ProviderException("oauth_callback", global::Lux3D.Core.LocalText.Get("The OAuth callback contains duplicate parameters."));
        }
        if (!pairs.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(State), Encoding.UTF8.GetBytes(state))) throw new ProviderException("oauth_state", global::Lux3D.Core.LocalText.Get("OAuth state does not match."));
        _consumed = true;
        if (pairs.ContainsKey("error") || !pairs.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Length > 8192 || code.Any(char.IsControl)) throw new ProviderException("oauth_denied", global::Lux3D.Core.LocalText.Get("OAuth authorization did not complete."));
        return code;
    }
    private static bool HasMalformedEscape(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '%') continue;
            if (i + 2 >= text.Length || !Uri.IsHexDigit(text[i + 1]) || !Uri.IsHexDigit(text[i + 2])) return true;
            i += 2;
        }
        return false;
    }
    public override string ToString() => "OAuthAttempt([redacted])";
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

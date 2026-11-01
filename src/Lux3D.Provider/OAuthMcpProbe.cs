using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Lux3D.Provider;

public sealed record OAuthMcpInspection(bool Available, string[] ToolNames, string? ProtocolVersion, string Message);

/// <summary>Read-only OAuth transport inspection. Does not call tools, retrieve account details, or create content.</summary>
public sealed class OAuthMcpProbe
{
    private readonly HttpClient _http;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly string[] SupportedVersions = ["2025-11-25", "2025-06-18", "2025-03-26"];
    public OAuthMcpProbe(HttpClient http) { _http = http; }
    public async Task<OAuthMcpInspection> InspectAsync(OAuthToken token, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        string? protocol = null;
        try
        {
            if (token.ExpiresAt <= DateTimeOffset.UtcNow || !string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(token.AccessToken) || token.AccessToken.Length > 32768 || token.AccessToken.Any(c => c < 0x21 || c > 0x7e))
                throw Error("OAuth 会话无效或已过期，请重新登录。");
            Uri endpoint = new(token.Region == LuxRegion.China ? "https://api.aholo3d.cn/lux3d-mcp/mcp" : "https://api.aholo.ai/lux3d-mcp/mcp");
            string? session = null;
            using (var initRequest = Request(endpoint, token, null, null, new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = SupportedVersions[0], capabilities = new { }, clientInfo = new { name = "Lux3D-for-Rhino", version = "0.1.0" } }
            }))
            {
                using var response = await _http.SendAsync(initRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                EnsureStatus(response);
                if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
                {
                    var sessions = values.ToArray();
                    if (sessions.Length != 1 || sessions[0].Length is < 1 or > 2048 || sessions[0].Any(c => c < 0x21 || c > 0x7e)) throw Error("MCP 会话标识无效。");
                    session = sessions[0];
                }
                var initialized = await ReadResultAsync(response, 1, timeout.Token).ConfigureAwait(false);
                protocol = String(initialized, "protocolVersion");
                if (!SupportedVersions.Contains(protocol, StringComparer.Ordinal)) throw Error("MCP 协议版本尚不受支持。");
                if (!initialized.TryGetProperty("capabilities", out var capabilities) || !capabilities.TryGetProperty("tools", out _)) throw Error("MCP 服务未公布工具发现能力。");
            }
            using (var ready = Request(endpoint, token, protocol, session, new { jsonrpc = "2.0", method = "notifications/initialized" }))
            using (var response = await _http.SendAsync(ready, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
            { EnsureStatus(response); if (response.StatusCode != HttpStatusCode.Accepted) throw Error("MCP 初始化通知未被正确确认。"); }
            var names = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            for (int page = 0; page < 20; page++)
            {
                int id = page + 2;
                object parameters = cursor is null ? new { } : new { cursor };
                using var list = Request(endpoint, token, protocol, session, new { jsonrpc = "2.0", id, method = "tools/list", @params = parameters });
                using var response = await _http.SendAsync(list, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                EnsureStatus(response);
                var result = await ReadResultAsync(response, id, timeout.Token).ConfigureAwait(false);
                if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array) throw Error("MCP 工具清单格式无效。");
                foreach (var tool in tools.EnumerateArray())
                {
                    string name = String(tool, "name");
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl) || !names.Add(name) || names.Count > 1000) throw Error("MCP 工具清单超出限制或含重复名称。");
                }
                cursor = String(result, "nextCursor");
                if (cursor.Length == 0) return new(true, names.Order(StringComparer.Ordinal).ToArray(), protocol, "OAuth 可访问 MCP 工具清单；尚未验证生成权限或额度。");
                if (cursor.Length > 4096 || cursor.Any(char.IsControl) || !cursors.Add(cursor)) throw Error("MCP 分页游标无效。");
            }
            throw Error("MCP 工具清单超过分页限制。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, [], protocol, "OAuth MCP 探测超时；未调用生成工具。"); }
        catch (ProviderException ex) { return new(false, [], protocol, ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or DecoderFallbackException)
        { return new(false, [], protocol, "OAuth MCP 连接或响应格式无效；未调用生成工具。"); }
    }
    private static HttpRequestMessage Request(Uri endpoint, OAuthToken token, string? protocol, string? session, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (protocol is not null) request.Headers.Add("MCP-Protocol-Version", protocol);
        if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
        return request;
    }
    private static void EnsureStatus(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw Error(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "OAuth 已取得 token，但 MCP 未授予访问权限。"
            : $"MCP 探测失败 (HTTP {(int)response.StatusCode})；未调用生成工具。");
    }
    private static async Task<JsonElement> ReadResultAsync(HttpResponseMessage response, int expectedId, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw Error("MCP 响应超过大小限制。");
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (mediaType == "application/json")
        {
            using var buffer = new MemoryStream(); byte[] chunk = new byte[8192]; int n;
            while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            { if (buffer.Length + n > MaximumResponseBytes) throw Error("MCP 响应超过大小限制。"); buffer.Write(chunk, 0, n); }
            return ParseResult(new UTF8Encoding(false, true).GetString(buffer.ToArray()), expectedId) ?? throw Error("MCP 响应编号不匹配。");
        }
        if (mediaType != "text/event-stream") throw Error("MCP 返回了不支持的内容类型。");
        byte[] bytes = new byte[4096]; char[] chars = new char[8192];
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var pending = new StringBuilder(); var eventData = new StringBuilder(); int total = 0, read;
        while ((read = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
        {
            total += read; if (total > MaximumResponseBytes) throw Error("MCP SSE 响应超过大小限制。");
            int count = decoder.GetChars(bytes, 0, read, chars, 0, false); pending.Append(chars, 0, count);
            while (true)
            {
                int newline = IndexOfNewline(pending); if (newline < 0) break;
                string line = pending.ToString(0, newline).TrimEnd('\r'); pending.Remove(0, newline + 1);
                if (line.Length == 0)
                {
                    if (eventData.Length > 0)
                    {
                        var result = ParseResult(eventData.ToString(), expectedId); eventData.Clear();
                        if (result is not null) return result.Value;
                    }
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    string data = line[5..]; if (data.StartsWith(' ')) data = data[1..];
                    if (data.Length > 0) { if (eventData.Length > 0) eventData.Append('\n'); eventData.Append(data); }
                }
            }
        }
        throw Error("MCP SSE 连接结束前未返回对应结果。");
    }
    private static JsonElement? ParseResult(string json, int expectedId)
    {
        using var document = JsonDocument.Parse(json); var message = document.RootElement;
        if (message.ValueKind != JsonValueKind.Object || String(message, "jsonrpc") != "2.0") throw Error("MCP 响应不是有效 JSON-RPC。");
        if (!message.TryGetProperty("id", out var id)) return null; // Notifications carry no result.
        if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int number) || number != expectedId) return null;
        if (message.TryGetProperty("error", out _)) throw Error("MCP 服务拒绝了工具发现请求。");
        if (!message.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) throw Error("MCP 结果格式无效。");
        return result.Clone();
    }
    private static int IndexOfNewline(StringBuilder text) { for (int n = 0; n < text.Length; n++) if (text[n] == '\n') return n; return -1; }
    private static string String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
    private static ProviderException Error(string message) => new("oauth_mcp_probe", message);
}

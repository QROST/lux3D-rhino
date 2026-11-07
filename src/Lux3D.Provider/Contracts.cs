using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lux3D.Provider;

public enum LuxRegion { China, International }
public static class LuxRegions
{
    public static LuxRegion Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "cn" or "china" => LuxRegion.China,
        "global" or "international" or "com" => LuxRegion.International,
        _ => throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Choose the China or Global region."), nameof(value))
    };
    public static string Name(LuxRegion region) => region == LuxRegion.China ? "cn" : "international";
    public static Uri ApiBase(LuxRegion region) => new(region == LuxRegion.China ? "https://api.aholo3d.cn/" : "https://api.aholo.ai/global/");
    public static Uri ApiKeyPage(LuxRegion region) => new(region == LuxRegion.China ? "https://labs.aholo3d.cn/api-keys?from=lux3d" : "https://labs.aholo.ai/api-keys?from=lux3d");
}

/// <summary>API keys and OAuth tokens are different credentials. This class only accepts explicit API keys.</summary>
public sealed class ApiKeyCredential
{
    internal string Value { get; }
    public LuxRegion Region { get; }
    public string ConnectionId { get; }
    public ApiKeyCredential(LuxRegion region, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 8192 || key.Any(char.IsControl) || key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Invalid API key. Use the original API key for the selected region."), nameof(key));
        Value = key.Trim(); Region = region;
        ConnectionId = LuxRegions.Name(region) + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Value))).ToLowerInvariant();
    }
    public override string ToString() => $"ApiKeyCredential({LuxRegions.Name(Region)}, [redacted])";
}

public sealed record ProviderTask(string TaskId, int Status, string State, Uri? GlbUri, string? OutputError = null);
public sealed class ProviderException : Exception
{
    public string Code { get; }
    public bool SubmissionMayHaveSucceeded { get; }
    public ProviderException(string code, string message, bool submissionMayHaveSucceeded = false) : base(message)
    { Code = code; SubmissionMayHaveSucceeded = submissionMayHaveSucceeded; }
}

internal static class ProviderValidation
{
    public static string TaskId(string? value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id < 1)
            throw new ProviderException("invalid_task_id", global::Lux3D.Core.LocalText.Get("The service returned an invalid task ID."));
        return id.ToString(CultureInfo.InvariantCulture);
    }
    public static Uri HttpsUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.IsLoopback ||
            uri.HostNameType != UriHostNameType.Dns || !string.IsNullOrEmpty(uri.UserInfo) || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException("invalid_url", global::Lux3D.Core.LocalText.Get("The service returned an invalid HTTPS asset URL."));
        return uri;
    }
}

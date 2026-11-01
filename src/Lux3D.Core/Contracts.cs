using System.Text.Json;

namespace Lux3D.Core;

public sealed record ConnectionInfo(string Region, bool SignedIn, string Message, bool CanGenerate = false);
public sealed record GenerationInput(string Prompt, string? ImagePath = null, string Model = "G1-Turbo", int FaceCount = 50000, bool EnablePbr = true, bool AcknowledgeUnknownSubmission = false);
public sealed record TaskRecord(Guid OperationId, string Region, string Prompt, string? ImagePath, string Model, int FaceCount,
    string State, string? TaskId, string? AssetPath, string? AssetSha256, string? Error, DateTimeOffset CreatedAt,
    string ConnectionId = "", bool EnablePbr = true, string? InputSha256 = null);
public sealed record RpcRequest(int Version, Guid Id, string Method, JsonElement Payload);
public sealed record RpcResponse(int Version, Guid Id, JsonElement? Result, string? Error);
public static class LuxJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
public static class AssetLimits
{
    public const long MaximumGlbArtifactBytes = 64L * 1024 * 1024;
    public const int MaximumVertices = 250_000;
    public const int MaximumTriangles = 500_000;
}
public sealed class AssetValidationException : Exception
{
    public AssetValidationException(string code, string message) : base(message) { Code = code; }
    public AssetValidationException(string code, string message, Exception inner) : base(message, inner) { Code = code; }
    public string Code { get; }
}

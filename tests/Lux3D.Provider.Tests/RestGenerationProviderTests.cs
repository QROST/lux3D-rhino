using System.Net;
using System.Text;
using Lux3D.Core;
using Lux3D.Sidecar;
using Xunit;

namespace Lux3D.Provider.Tests;

public sealed class RestGenerationProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lux3d-rest-workflow-" + Guid.NewGuid());

    [Theory]
    [InlineData("token", "cancel")]
    [InlineData("upload", "cancel")]
    [InlineData("status", "cancel")]
    [InlineData("token", "io")]
    [InlineData("upload", "io")]
    [InlineData("status", "io")]
    [InlineData("token", "http")]
    [InlineData("upload", "http")]
    [InlineData("status", "http")]
    [InlineData("status", "malformed-null")]
    [InlineData("status", "malformed-array")]
    [InlineData("init", "malformed-null")]
    [InlineData("init", "malformed-array")]
    public async Task UploadFailureDoesNotBlockAnotherGeneration(string phase, string failure)
    {
        var store = new TaskStore(_root);
        string image = WriteImage();
        bool fail = true;
        int creates = 0;
        using var cancelled = new CancellationTokenSource();
        using var http = new HttpClient(new Stub(request =>
        {
            string current = Phase(request);
            if (current == "create") creates++;
            if (fail && current == phase)
            {
                if (failure.StartsWith("malformed-", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = new StringContent("{\"c\":0,\"d\":" + (failure == "malformed-null" ? "null" : "[]") + "}") };
                if (failure == "cancel")
                {
                    cancelled.Cancel();
                    throw new OperationCanceledException(cancelled.Token);
                }
                throw Failure(failure);
            }
            if (fail && phase == "init" && current == "token")
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"c":0,"d":{"ousToken":"test-upload-token","globalDomain":"https://upload.example/","blockSize":1}}""") };
            return Success(current);
        }));
        var provider = new RestGenerationProvider(http, http, new(LuxRegion.International, "test-only-key"));
        var workflow = new Workflow(store);
        var failed = await workflow.GenerateAsync(provider, new("", image), cancelled.Token);
        Assert.Equal("Failed", failed.State);
        Assert.Null(failed.TaskId);
        Assert.DoesNotContain("sensitive-detail", failed.Error!);
        Assert.Equal(0, creates);
        Assert.False(new TaskStore(_root).HasUnknownSubmissions());

        fail = false;
        var next = await workflow.GenerateAsync(provider, new("", image));
        Assert.Equal("Queued", next.State);
        Assert.Equal("42", next.TaskId);
        Assert.Equal(1, creates);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("io")]
    [InlineData("http")]
    public async Task LostCreateResponseAfterUploadStillBlocksDuplicateGeneration(string failure)
    {
        var store = new TaskStore(_root);
        string image = WriteImage();
        int creates = 0;
        using var http = new HttpClient(new Stub(request =>
        {
            string phase = Phase(request);
            if (phase == "create") { creates++; throw Failure(failure); }
            return Success(phase);
        }));
        var provider = new RestGenerationProvider(http, http, new(LuxRegion.International, "test-only-key"));
        var workflow = new Workflow(store);
        var unknown = await workflow.GenerateAsync(provider, new("", image));
        Assert.Equal("SubmissionUnknown", unknown.State);
        Assert.True(new TaskStore(_root).HasUnknownSubmissions());
        Assert.DoesNotContain("sensitive-detail", unknown.Error!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.GenerateAsync(provider, new("", image)));
        Assert.Equal(1, creates);
    }

    private string WriteImage()
    {
        string image = Path.Combine(_root, "input.png");
        File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a9sAAAAASUVORK5CYII="));
        return image;
    }

    private static Exception Failure(string kind) => kind switch
    {
        "cancel" => new OperationCanceledException("sensitive-detail"),
        "io" => new IOException("sensitive-detail"),
        _ => new HttpRequestException("sensitive-detail")
    };
    private static string Phase(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/asset/v1/token", StringComparison.Ordinal)) return "token";
        if (path.EndsWith("/single/upload", StringComparison.Ordinal)) return "upload";
        if (path.EndsWith("/upload/status", StringComparison.Ordinal)) return "status";
        if (path.EndsWith("/block/upload/init", StringComparison.Ordinal)) return "init";
        Assert.EndsWith("/img-to-3d/task/create", path);
        return "create";
    }
    private static HttpResponseMessage Success(string phase) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(phase switch
        {
            "token" => """{"c":0,"d":{"ousToken":"test-upload-token","globalDomain":"https://upload.example/","blockSize":100000}}""",
            "upload" => """{"c":0,"d":{}}""",
            "status" => """{"c":0,"d":{"status":5,"url":"https://cdn.example/input.png"}}""",
            _ => """{"c":0,"d":42}"""
        }, Encoding.UTF8, "application/json")
    };
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

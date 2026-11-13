using System.Net;
using Lux3D.Core;
using Lux3D.Sidecar;
using Xunit;

namespace Lux3D.Provider.Tests;

public sealed class QueryRetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lux-query-retry-" + Guid.NewGuid());

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task DeltaRetryAfterIsPreservedWithoutAutomaticRequestRetry(int status)
    {
        int count = 0;
        using var http = new HttpClient(new ProviderTests.Stub(_ =>
        {
            count++;
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.TryAddWithoutValidation("Retry-After", "600");
            return Task.FromResult(response);
        }));
        var start = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            new Lux3DApiClient(http, new(LuxRegion.China, "fake")).GetTaskAsync("42"));
        Assert.NotNull(error.RetryAt);
        Assert.InRange(error.RetryAt.Value, start.AddSeconds(600), DateTimeOffset.UtcNow.AddSeconds(600));
        Assert.Equal(1, count);
        Assert.False(error.SubmissionMayHaveSucceeded);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("-50")]
    public async Task InvalidRetryAfterUsesDefaultPolicy(string header)
    {
        using var http = new HttpClient(new ProviderTests.Stub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", header);
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            new Lux3DApiClient(http, new(LuxRegion.China, "fake")).GetTaskAsync("42"));
        Assert.Null(error.RetryAt);
    }

    [Fact]
    public async Task ServerDateSurvivesAdapterAndWorkflowRestartWithoutAnotherNetworkRequest()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var deadline = now.AddMinutes(30);
        int count = 0;
        using var http = new HttpClient(new ProviderTests.Stub(_ =>
        {
            count++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(deadline);
            return Task.FromResult(response);
        }));
        using var download = new HttpClient(new ProviderTests.Stub(_ => throw new Exception("No download expected")));
        var provider = new RestGenerationProvider(http, download, new(LuxRegion.China, "fake"));
        var store = new TaskStore(_root);
        var task = new TaskRecord(Guid.NewGuid(), "cn", "chair", null, "G1-Turbo", 50000,
            "Queued", "42", null, null, null, now, provider.ConnectionId);
        store.Save(task);
        var first = await new Workflow(store, () => now, () => 0).RefreshAsync(provider, task.OperationId);
        Assert.Equal(deadline, first.NextQueryAt);
        Assert.Equal(1, first.QueryFailures);
        Assert.Equal("42", first.TaskId);
        var recovered = await new Workflow(new TaskStore(_root), () => now.AddMinutes(1), () => 0)
            .RefreshAsync(provider, task.OperationId);
        Assert.Equal(first, recovered);
        Assert.Equal(1, count);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

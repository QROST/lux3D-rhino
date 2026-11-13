using System.Text.Json;
using Xunit;

namespace Lux3D.Core.Tests;

public sealed class WorkflowRetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lux3d-query-retry-" + Guid.NewGuid());
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeadlineSurvivesRestartAndUnknownSuccessPreservesRawStatus()
    {
        var store = new TaskStore(_root);
        var workflow = new Workflow(store, () => _now, () => 0);
        var provider = new FakeProvider { QueryError = new IOException("secret-provider-body") };
        var created = await workflow.GenerateAsync(provider, new("chair"));
        var failed = await workflow.RefreshAsync(provider, created.OperationId);
        Assert.Equal("StatusUnknown", failed.State);
        Assert.Equal(1, failed.QueryFailures);
        Assert.Equal(_now.AddSeconds(5), failed.NextQueryAt);
        Assert.DoesNotContain("secret-provider-body", failed.Error!);
        Assert.Equal(failed, new TaskStore(_root).Get(created.OperationId));

        var restarted = new Workflow(new TaskStore(_root), () => _now, () => 0);
        _now = failed.NextQueryAt!.Value.AddTicks(-1);
        Assert.Equal(failed, await restarted.RefreshAsync(provider, created.OperationId));
        Assert.Equal(1, provider.Queries);
        _now = failed.NextQueryAt.Value;
        provider.QueryError = null;
        provider.State = "StatusUnknown";
        provider.RawStatus = 987;
        var unknown = await restarted.RefreshAsync(provider, created.OperationId);
        Assert.Equal("StatusUnknown", unknown.State);
        Assert.Equal(987, unknown.RawStatus);
        Assert.Equal(0, unknown.QueryFailures);
        Assert.Null(unknown.NextQueryAt);
        Assert.Equal(unknown, store.Get(created.OperationId));
        Assert.Equal(0, provider.Downloads);

        provider.QueryError = new IOException("offline");
        var failedAgain = await restarted.RefreshAsync(provider, created.OperationId);
        Assert.Equal(987, failedAgain.RawStatus);
        Assert.Equal(1, failedAgain.QueryFailures);
        Assert.Equal(_now.AddSeconds(5), failedAgain.NextQueryAt);
        Assert.Equal(1, provider.Creates);
    }

    [Fact]
    public async Task ConsecutiveFailuresBackOffExponentiallyWithoutRecreating()
    {
        var store = new TaskStore(_root);
        var provider = new FakeProvider { QueryError = new IOException("offline") };
        var created = await new Workflow(store, () => _now).GenerateAsync(provider, new("chair"));
        int[] delays = { 5, 10, 20, 40, 80, 100, 100, 100 };
        for (int i = 0; i < delays.Length; i++)
        {
            var restarted = new Workflow(new TaskStore(_root), () => _now, () => 0);
            var failed = await restarted.RefreshAsync(provider, created.OperationId);
            Assert.Equal(i + 1, failed.QueryFailures);
            Assert.Equal(_now.AddSeconds(delays[i]), failed.NextQueryAt);
            Assert.Equal(failed, await restarted.RefreshAsync(provider, created.OperationId));
            Assert.Equal(i + 1, provider.Queries);
            _now = failed.NextQueryAt!.Value;
        }
        Assert.Equal(1, provider.Creates);
        Assert.Equal(0, provider.Downloads);
    }

    [Fact]
    public async Task QueryBackoffIncludesBoundedJitter()
    {
        var workflow = new Workflow(new TaskStore(_root), () => _now, () => 0.5);
        var provider = new FakeProvider { QueryError = new IOException("offline") };
        var created = await workflow.GenerateAsync(provider, new("chair"));
        var failed = await workflow.RefreshAsync(provider, created.OperationId);
        Assert.Equal(_now.AddSeconds(5.5), failed.NextQueryAt);
    }

    [Fact]
    public async Task SaturatedBackoffRetainsJitterAndNeverExceedsTwoMinutes()
    {
        var store = new TaskStore(_root);
        var provider = new FakeProvider { QueryError = new IOException("offline") };
        var created = await new Workflow(store, () => _now).GenerateAsync(provider, new("chair"));
        var saturated = created with { QueryFailures = 20 };
        store.Save(saturated);
        var low = await new Workflow(store, () => _now, () => 0).RefreshAsync(provider, created.OperationId);
        store.Save(saturated);
        var high = await new Workflow(store, () => _now, () => 1).RefreshAsync(provider, created.OperationId);
        Assert.Equal(_now.AddSeconds(100), low.NextQueryAt);
        Assert.Equal(_now.AddSeconds(120), high.NextQueryAt);
        Assert.NotEqual(low.NextQueryAt, high.NextQueryAt);
        Assert.Equal(1, provider.Creates);
    }

    [Theory]
    [InlineData(-10, 5)]
    [InlineData(2, 5)]
    [InlineData(3600, 3600)]
    public async Task ServiceRetryAfterCannotShortenLocalBackoff(int serviceSeconds, int expectedSeconds)
    {
        var workflow = new Workflow(new TaskStore(_root), () => _now, () => 0);
        var provider = new FakeProvider { QueryError = new QueryRetryException("rate-limited", _now.AddSeconds(serviceSeconds)) };
        var created = await workflow.GenerateAsync(provider, new("chair"));
        var failed = await workflow.RefreshAsync(provider, created.OperationId);
        Assert.Equal(_now.AddSeconds(expectedSeconds), failed.NextQueryAt);
        Assert.Equal(1, failed.QueryFailures);
        Assert.Equal(1, provider.Creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAndValidationFailuresDoNotCountAsQueryFailures(bool invalidAsset)
    {
        var store = new TaskStore(_root);
        var workflow = new Workflow(store, () => _now, () => 0);
        var provider = new FakeProvider { QueryError = new IOException("offline") };
        var created = await workflow.GenerateAsync(provider, new("chair"));
        var first = await workflow.RefreshAsync(provider, created.OperationId);
        _now = first.NextQueryAt!.Value;
        provider.QueryError = null;
        provider.DownloadError = invalidAsset ? null : new QueryRetryException("download rate limit", _now.AddHours(1));
        provider.InvalidAsset = invalidAsset;
        var downloaded = await workflow.RefreshAsync(provider, created.OperationId);
        Assert.Equal(invalidAsset ? "Rejected" : "DownloadFailed", downloaded.State);
        Assert.Equal(3, downloaded.RawStatus);
        Assert.Equal(0, downloaded.QueryFailures);
        Assert.Null(downloaded.NextQueryAt);
        Assert.Equal(downloaded, store.Get(created.OperationId));
        Assert.Equal(1, provider.Creates);
    }

    [Fact]
    public void LegacyTaskJsonDefaultsRetryFieldsWithoutMigration()
    {
        var task = JsonSerializer.Deserialize<TaskRecord>("""{"operationId":"aabbccdd-eeff-0011-2233-445566778899","region":"global","prompt":"chair","model":"G1-Turbo","faceCount":50000,"state":"Queued","taskId":"42","createdAt":"2026-01-01T00:00:00+00:00"}""", LuxJson.Options)!;
        Assert.Null(task.RawStatus);
        Assert.Null(task.NextQueryAt);
        Assert.Equal(0, task.QueryFailures);
        var store = new TaskStore(_root);
        store.Save(task);
        Assert.Equal(task, store.Get(task.OperationId));
    }

    private sealed class FakeProvider : IGenerationProvider
    {
        public string Region => "global";
        public string ConnectionId => "test-connection";
        public int Creates, Queries, Downloads;
        public Exception? QueryError, DownloadError;
        public bool InvalidAsset;
        public string State = "Succeeded";
        public int RawStatus = 3;
        public Task<string> CreateAsync(GenerationInput input, CancellationToken cancellationToken) { Creates++; return Task.FromResult("42"); }
        public Task<RemoteTask> QueryAsync(string taskId, CancellationToken cancellationToken)
        {
            Queries++;
            if (QueryError is not null) throw QueryError;
            return Task.FromResult(new RemoteTask(taskId, State, RawStatus, new Uri("https://cdn.example/asset.glb")));
        }
        public Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            Downloads++;
            if (DownloadError is not null) throw DownloadError;
            return Task.FromResult(InvalidAsset ? new byte[] { 1, 2, 3 } : GlbContainerValidatorTests.CreateTexturedTriangleGlb());
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

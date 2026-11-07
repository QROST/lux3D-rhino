using Xunit;
namespace Lux3D.Core.Tests;

public sealed class WorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lux3d-workflow-" + Guid.NewGuid());
    private readonly FakeProvider _provider = new();
    [Fact] public async Task LostCreateResponseIsNotAutomaticallyRetriedAfterRestart()
    {
        _provider.LoseResponse = true;
        var store = new TaskStore(_root);
        TaskRecord result = await new Workflow(store).GenerateAsync(_provider, new("chair"));
        Assert.Equal("SubmissionUnknown", result.State);
        result = await new Workflow(new TaskStore(_root)).RefreshAsync(_provider, result.OperationId);
        Assert.Equal("SubmissionUnknown", result.State); Assert.Equal(1, _provider.Creates); Assert.Equal(0, _provider.Queries);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Workflow(new TaskStore(_root)).GenerateAsync(_provider, new("chair")));
        Assert.Equal(1, _provider.Creates);
    }
    [Fact] public async Task SuccessfulGenerationDownloadsValidatedAssetWithoutRecreating()
    {
        var store = new TaskStore(_root); var workflow = new Workflow(store);
        TaskRecord created = await workflow.GenerateAsync(_provider, new("chair"));
        TaskRecord ready = await workflow.RefreshAsync(_provider, created.OperationId);
        Assert.Equal("Ready", ready.State); Assert.True(File.Exists(ready.AssetPath));
        await workflow.RefreshAsync(_provider, created.OperationId);
        Assert.Equal(1, _provider.Creates); Assert.Equal(1, _provider.Queries);
    }
    [Fact] public async Task RegionAndCredentialBindingSurviveRestart()
    {
        var store = new TaskStore(_root);
        TaskRecord created = await new Workflow(store).GenerateAsync(_provider, new("chair"));
        var differentAccount = new FakeProvider { ConnectionId = "other" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Workflow(new TaskStore(_root)).RefreshAsync(differentAccount, created.OperationId));
        Assert.Equal(0, differentAccount.Queries);
    }
    [Fact] public async Task DownloadFailureCanResumeOriginalTask()
    {
        _provider.FailDownload = true;
        var workflow = new Workflow(new TaskStore(_root));
        var task = await workflow.GenerateAsync(_provider, new("chair"));
        Assert.Equal("DownloadFailed", (await workflow.RefreshAsync(_provider, task.OperationId)).State);
        _provider.FailDownload = false;
        Assert.Equal("Ready", (await workflow.RefreshAsync(_provider, task.OperationId)).State);
        Assert.Equal(1, _provider.Creates);
    }
    [Fact] public async Task RejectedAssetKeepsDiagnosticAndRecoversOriginalTaskWithoutCreatingAgain()
    {
        _provider.Payload = new byte[] { 1, 2, 3 };
        var store = new TaskStore(_root);
        var workflow = new Workflow(store);
        var created = await workflow.GenerateAsync(_provider, new("chair"));
        var rejected = await workflow.RefreshAsync(_provider, created.OperationId);
        Assert.Equal("Rejected", rejected.State);
        Assert.Equal(created.TaskId, rejected.TaskId);
        Assert.Equal("glb_invalid", rejected.ValidationCode);
        Assert.Contains("too short", rejected.Error);
        Assert.Null(rejected.AssetPath);
        Assert.Equal(_provider.Payload, File.ReadAllBytes(rejected.RejectedAssetPath!));
        Assert.Equal(rejected.RejectedAssetSha256, store.Get(created.OperationId).RejectedAssetSha256);

        _provider.Payload = GlbContainerValidatorTests.CreateTexturedTriangleGlb();
        var recovered = await workflow.RefreshAsync(_provider, created.OperationId);
        Assert.Equal("Ready", recovered.State);
        Assert.Equal(created.TaskId, recovered.TaskId);
        Assert.Null(recovered.ValidationCode);
        Assert.Null(recovered.RejectedAssetPath);
        Assert.Null(recovered.Error);
        Assert.Equal(1, _provider.Creates);
        Assert.Equal(2, _provider.Queries);
        Assert.Equal(2, _provider.Downloads);
    }
    [Fact] public async Task MemoryFailureIsRecoverableAndDoesNotRejectGeometry()
    {
        _provider.ResourceFailure = true;
        var workflow = new Workflow(new TaskStore(_root));
        var created = await workflow.GenerateAsync(_provider, new("chair"));
        var failed = await workflow.RefreshAsync(_provider, created.OperationId);
        Assert.Equal("DownloadFailed", failed.State);
        Assert.Contains("not enough memory", failed.Error);
        Assert.Null(failed.ValidationCode);
        Assert.Equal(created.TaskId, failed.TaskId);
        _provider.ResourceFailure = false;
        Assert.Equal("Ready", (await workflow.RefreshAsync(_provider, created.OperationId)).State);
        Assert.Equal(1, _provider.Creates);
    }
    [Fact] public async Task UnknownRemoteStateNeverDownloadsOrImports()
    {
        _provider.State = "StatusUnknown";
        var workflow = new Workflow(new TaskStore(_root));
        var task = await workflow.GenerateAsync(_provider, new("chair"));
        Assert.Equal("StatusUnknown", (await workflow.RefreshAsync(_provider, task.OperationId)).State);
        Assert.Equal(0, _provider.Downloads);
    }
    [Fact] public async Task InputValidationPreventsNetworkUse()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new Workflow(new TaskStore(_root)).GenerateAsync(_provider, new("")));
        Assert.Equal(0, _provider.Creates);
    }
    [Fact] public async Task ExplicitProviderRejectionIsFailedNotUnknown()
    {
        _provider.Reject = true;
        var store = new TaskStore(_root);
        Assert.Equal("Failed", (await new Workflow(store).GenerateAsync(_provider, new("chair"))).State);
        Assert.False(store.HasUnknownSubmissions());
    }
    private sealed class FakeProvider : IGenerationProvider
    {
        public string Region => "global";
        public string ConnectionId { get; set; } = "test-identity";
        public int Creates, Queries, Downloads;
        public bool LoseResponse, FailDownload, Reject, ResourceFailure;
        public byte[]? Payload;
        public string State = "Succeeded";
        public Task<string> CreateAsync(GenerationInput input, CancellationToken ct)
        { Creates++; if (Reject) throw new SubmissionRejectedException("credentials rejected"); if (LoseResponse) throw new IOException("response lost"); return Task.FromResult("9223372036854775800"); }
        public Task<RemoteTask> QueryAsync(string taskId, CancellationToken ct)
        { Queries++; return Task.FromResult(new RemoteTask(taskId, State, 3, new Uri("https://example.com/model.glb"))); }
        public Task<byte[]> DownloadAsync(Uri uri, CancellationToken ct)
        { Downloads++; if (ResourceFailure) throw new OutOfMemoryException(); if (FailDownload) throw new IOException("lost"); return Task.FromResult(Payload ?? GlbContainerValidatorTests.CreateTexturedTriangleGlb()); }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

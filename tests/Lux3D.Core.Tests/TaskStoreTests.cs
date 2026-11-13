using System.Text.Json;
using Xunit;
namespace Lux3D.Core.Tests;

public sealed class TaskStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lux3d-tests-" + Guid.NewGuid());
    [Fact] public void InterruptedSubmissionIsUnknownAndDoesNotCreateAnything()
    {
        var store = new TaskStore(_root);
        var task = new TaskRecord(Guid.NewGuid(), "global", "chair", null, "G1-Turbo", 50000, "Submitting", null, null, null, null, DateTimeOffset.UtcNow);
        store.Save(task);
        Assert.Equal("SubmissionUnknown", new TaskStore(_root).List().Single().State);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "tasks"), "*.json"));
    }
    [Fact] public void CachedAssetHasContentHashAndRejectsCorruption()
    {
        var store = new TaskStore(_root);
        var (path, hash) = store.StoreAsset(GlbContainerValidatorTests.CreateTexturedTriangleGlb());
        Assert.True(File.Exists(path)); Assert.Equal(64, hash.Length);
        Assert.Throws<AssetValidationException>(() => store.StoreAsset(new byte[100]));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "assets")));
    }
    [Fact] public void RequiredExtensionsAreRejected()
    {
        Assert.Throws<AssetValidationException>(() => GlbContainerValidator.Validate(GlbContainerValidatorTests.BuildGlb("""{"asset":{"version":"2.0"},"extensionsRequired":["KHR_draco_mesh_compression"]}""")));
    }
    [Fact] public void ConcurrentMutationsAreExcluded()
    {
        var store = new TaskStore(_root);
        using var first = store.LockMutation();
        Assert.Throws<IOException>(() => store.LockMutation());
    }
    [Fact] public void HistoryIsPagedButUnknownGuardChecksOlderRecords()
    {
        var store = new TaskStore(_root);
        for (int i = 0; i < 65; i++) store.Save(new TaskRecord(Guid.NewGuid(), "global", new string('椅', 4000), null,
            "G1-Turbo", 50000, i == 0 ? "Submitting" : "Ready", null, null, null, null, DateTimeOffset.UtcNow.AddSeconds(i)));
        Assert.Equal(30, store.List().Length);
        Assert.Equal(30, store.List(30).Length);
        Assert.Equal(5, store.List(60).Length);
        Assert.True(store.HasUnknownSubmissions());
        string wire = JsonSerializer.Serialize(new RpcResponse(1, Guid.NewGuid(), LuxJson.Element(store.List()), null), LuxJson.Options);
        Assert.True(wire.Length < 2 * 1024 * 1024);
    }
    [Fact] public async Task BoundedProtocolRejectsOverlongAndTruncatedMessages()
    {
        await Assert.ThrowsAsync<IOException>(() => BoundedLine.ReadAsync(new StringReader("12345\n"), default, 4));
        await Assert.ThrowsAsync<IOException>(() => BoundedLine.ReadAsync(new StringReader("abc"), default));
        Assert.Equal("abc", await BoundedLine.ReadAsync(new StringReader("abc\n"), default));
    }
    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("mismatched-id")]
    [InlineData("null-region")]
    public async Task UnreadableRecordPreservesOtherHistoryAndBlocksUnsafeGeneration(string damage)
    {
        var store = new TaskStore(_root);
        var good = Record();
        store.Save(good);
        Guid badId = Guid.NewGuid();
        string badPath = Path.Combine(_root, "tasks", badId.ToString("N") + ".json");
        string original = damage switch
        {
            "malformed" => "{ broken secret-detail",
            "oversized" => new string('x', 128 * 1024 + 1),
            "mismatched-id" => JsonSerializer.Serialize(Record(), LuxJson.Options),
            _ => JsonSerializer.Serialize(Record() with { OperationId = badId, Region = null! }, LuxJson.Options)
        };
        File.WriteAllText(badPath, original);
        var records = store.List();
        Assert.Equal(2, records.Length);
        Assert.Contains(good, records);
        var placeholder = records.Single(t => t.OperationId == badId);
        Assert.Equal("SubmissionUnknown", placeholder.State);
        Assert.Contains("original file is preserved", placeholder.Error);
        Assert.Null(placeholder.TaskId);
        Assert.True(store.HasUnknownSubmissions());
        var error = Assert.Throws<IOException>(() => store.Get(badId));
        Assert.DoesNotContain("secret-detail", error.Message);
        Assert.Throws<ArgumentException>(() => store.Save(placeholder));
        var provider = new NoNetworkProvider();
        var workflow = new Workflow(store);
        await Assert.ThrowsAsync<IOException>(() => workflow.RefreshAsync(provider, badId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.GenerateAsync(provider, new("chair")));
        Assert.Equal(0, provider.Calls);
        Assert.Equal(original, File.ReadAllText(badPath));
        Assert.Equal(good, store.Get(good.OperationId));

        var confirmed = await workflow.GenerateAsync(provider, new("chair", AcknowledgeUnknownSubmission: true));
        Assert.Equal("Queued", confirmed.State);
        Assert.Equal(1, provider.Calls);
        Assert.True(store.HasUnknownSubmissions());
        Assert.Equal(original, File.ReadAllText(badPath));
    }
    [Theory]
    [InlineData("unexpected.json")]
    [InlineData("00000000000000000000000000000000.json")]
    [InlineData("AABBCCDDEEFF00112233445566778899.json")]
    [InlineData("aabbccddeeff00112233445566778899.JSON")]
    public async Task NoncanonicalRecordNameCannotHideHistoryOrBypassUnknownGuard(string name)
    {
        var store = new TaskStore(_root);
        var good = Record();
        store.Save(good);
        string path = Path.Combine(_root, "tasks", name);
        const string original = "{ original damaged bytes";
        File.WriteAllText(path, original);
        Assert.Equal(good, Assert.Single(store.List()));
        Assert.True(store.HasUnknownSubmissions());
        var provider = new NoNetworkProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Workflow(store).GenerateAsync(provider, new("chair")));
        Assert.Equal(0, provider.Calls);
        Assert.Equal(original, File.ReadAllText(path));
    }
    [Fact] public void InterruptedAtomicWriteTemporaryFileDoesNotBlockGeneration()
    {
        var store = new TaskStore(_root);
        File.WriteAllText(Path.Combine(_root, "tasks", Guid.NewGuid().ToString("N") + ".json.partial.tmp"), "partial");
        Assert.Empty(store.List());
        Assert.False(store.HasUnknownSubmissions());
    }
    private static TaskRecord Record() => new(Guid.NewGuid(), "global", "chair", null, "G1-Turbo", 50000,
        "Queued", "42", null, null, null, DateTimeOffset.UtcNow, "test-connection");
    private sealed class NoNetworkProvider : IGenerationProvider
    {
        public string Region => "global";
        public string ConnectionId => "test-connection";
        public int Calls;
        public Task<string> CreateAsync(GenerationInput input, CancellationToken cancellationToken) { Calls++; return Task.FromResult("43"); }
        public Task<RemoteTask> QueryAsync(string taskId, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("Unexpected query"); }
        public Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("Unexpected download"); }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

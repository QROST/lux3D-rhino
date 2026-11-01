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
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

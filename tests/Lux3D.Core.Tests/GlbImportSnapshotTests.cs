using System.Security.Cryptography;
using Xunit;

namespace Lux3D.Core.Tests;

public sealed class GlbImportSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lux3d-import-snapshot-tests-" + Guid.NewGuid());
    private string Source(byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "模型 sample.glb");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task BackgroundPreparationKeepsIndependentValidatedBytesAndCleansOnlyItsSnapshot()
    {
        byte[] bytes = GlbContainerValidatorTests.CreateTexturedTriangleGlb();
        string source = Source(bytes);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using GlbImportSnapshot snapshot = await Task.Run(() => GlbImportSnapshot.Create(source, sha));
        Assert.Equal(sha, snapshot.Sha256.ToLowerInvariant());
        Assert.Equal(bytes, File.ReadAllBytes(snapshot.Path));
        Assert.NotEqual(source, snapshot.Path);
        File.WriteAllText(source, "source replaced after capture");
        Assert.Equal(bytes, File.ReadAllBytes(snapshot.Path));
        snapshot.Dispose();
        snapshot.Dispose();
        Assert.False(File.Exists(snapshot.Path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(snapshot.Path)));
        Assert.Equal("source replaced after capture", File.ReadAllText(source));
    }

    [Fact]
    public void MismatchedTaskHashIsRejectedWithoutChangingSource()
    {
        byte[] bytes = GlbContainerValidatorTests.CreateTexturedTriangleGlb();
        string source = Source(bytes);
        Assert.Throws<InvalidOperationException>(() => GlbImportSnapshot.Create(source, new string('0', 64)));
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    [Fact]
    public void InvalidContainerIsRejectedBeforeRhinoImport()
    {
        string source = Source(new byte[] { 0, 1, 2, 3 });
        Assert.Throws<AssetValidationException>(() => GlbImportSnapshot.Create(source, null));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, File.ReadAllBytes(source));
    }

    [Fact]
    public void SnapshotAndDirectoryArePrivateOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        using GlbImportSnapshot snapshot = GlbImportSnapshot.Create(Source(GlbContainerValidatorTests.CreateTexturedTriangleGlb()), null);
        UnixFileMode otherUsers = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(snapshot.Path) & otherUsers);
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(Path.GetDirectoryName(snapshot.Path)!) & otherUsers);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

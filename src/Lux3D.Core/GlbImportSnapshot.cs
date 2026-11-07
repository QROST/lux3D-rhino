using System.Security.Cryptography;

namespace Lux3D.Core;

/// <summary>A private validated file snapshot. Contains no Rhino objects and can be prepared off the UI thread.</summary>
public sealed class GlbImportSnapshot : IDisposable
{
    private int _disposed;
    private GlbImportSnapshot(string path, string sha256) { Path = path; Sha256 = sha256; }
    public string Path { get; }
    public string Sha256 { get; }

    public static GlbImportSnapshot Create(string source, string? expectedSha256, HostImportTrace? trace = null)
    {
        trace?.Record("preflight_file_start");
        byte[] bytes = File.ReadAllBytes(source);
        GlbContainerValidator.Validate(bytes);
        trace?.Record("preflight_container_valid");
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        if (expectedSha256 is not null && !string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(global::Lux3D.Core.LocalText.Get("The GLB hash does not match the task record."));
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lux3D-Rhino", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string path = System.IO.Path.Combine(directory, "asset.glb");
            TaskStore.WriteAtomic(path, bytes);
            trace?.Record("preflight_file_ready");
            return new GlbImportSnapshot(path, sha);
        }
        catch
        {
            try { Directory.Delete(directory, true); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

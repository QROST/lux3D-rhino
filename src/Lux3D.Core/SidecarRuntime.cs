using System.Runtime.InteropServices;

namespace Lux3D.Core;

/// <summary>Find a full host path with .NET 8 even when Rhino's GUI PATH differs from a shell's.</summary>
internal static class SidecarRuntime
{
    public static string FindDotNet8()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Lux3D 插件支持 macOS 和 Windows 上的 Rhino 8。");

        string hostName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (string root in CandidateRoots().Distinct(comparer))
        {
            try
            {
                // Empty/relative PATH entries must not resolve an executable in a model or package directory.
                if (!Path.IsPathFullyQualified(root)) continue;
                string host = Path.Combine(root, hostName);
                if (!File.Exists(host)) continue;
                var info = new FileInfo(host);
                string resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
                string shared = Path.Combine(Path.GetDirectoryName(resolved)!, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(shared)) continue;
                if (Directory.EnumerateDirectories(shared).Any(path =>
                    Version.TryParse(Path.GetFileName(path), out var version) && version.Major == 8 &&
                    File.Exists(Path.Combine(path, "System.Private.CoreLib.dll"))))
                    return resolved;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An inaccessible or stale candidate must not hide a later valid installation.
            }
        }
        throw new IOException("未找到可用的 .NET 8 Runtime。请安装本机架构的 .NET 8 Runtime，或设置 DOTNET_ROOT 后重新启动 Rhino。");
    }

    private static IEnumerable<string> CandidateRoots()
    {
        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        string? specific = Environment.GetEnvironmentVariable("DOTNET_ROOT_" + architecture);
        if (!string.IsNullOrWhiteSpace(specific)) yield return specific;
        string? configured = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                    yield return Path.Combine(programFiles, "dotnet", "x64");
                yield return Path.Combine(programFiles, "dotnet");
            }
        }
        else
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                yield return "/usr/local/share/dotnet/x64";
            yield return "/usr/local/share/dotnet";
            // Homebrew's bin/dotnet may be a shell wrapper, not a symlink to the real host.
            // The stable opt paths expose libexec/dotnet next to its shared runtime directory.
            string[] prefixes = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? new[] { "/opt/homebrew", "/usr/local" } : new[] { "/usr/local", "/opt/homebrew" };
            foreach (string prefix in prefixes)
            {
                yield return Path.Combine(prefix, "opt", "dotnet@8", "libexec");
                yield return Path.Combine(prefix, "opt", "dotnet", "libexec");
            }
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
        }
        foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(path)) yield return path.Trim().Trim('"');
    }
}

namespace Lux3D.Provider;

public interface ICredentialStore
{
    Task<string?> GetAsync(string account, CancellationToken cancellationToken = default);
    Task SetAsync(string account, string secret, CancellationToken cancellationToken = default);
    Task DeleteAsync(string account, CancellationToken cancellationToken = default);
}

/// <summary>Selects OS-protected, per-user credential persistence. Never falls back to plaintext.</summary>
public static class CredentialStoreFactory
{
    public const string DefaultService = "ai.aholo.lux3d.rhino";

    public static ICredentialStore CreateDefault()
    {
        if (OperatingSystem.IsMacOS()) return new MacKeychainCredentialStore(DefaultService);
        if (OperatingSystem.IsWindows()) return new WindowsDpapiCredentialStore(DefaultService);
        throw new PlatformNotSupportedException("Lux3D 凭证存储仅支持 macOS 钥匙串和 Windows 当前用户 DPAPI。");
    }
}

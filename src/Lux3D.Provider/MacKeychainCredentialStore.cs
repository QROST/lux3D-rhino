using System.Runtime.InteropServices;
using System.Text;

namespace Lux3D.Provider;

public interface ICredentialStore
{
    Task<string?> GetAsync(string account, CancellationToken cancellationToken = default);
    Task SetAsync(string account, string secret, CancellationToken cancellationToken = default);
    Task DeleteAsync(string account, CancellationToken cancellationToken = default);
}

/// <summary>Uses macOS Security.framework directly. Secrets never enter process arguments or plaintext files.</summary>
public sealed class MacKeychainCredentialStore : ICredentialStore
{
    private readonly byte[] _service;
    private const int NotFound = -25300;
    public MacKeychainCredentialStore(string service = "ai.aholo.lux3d.rhino") { _service = Encoding.UTF8.GetBytes(service); }
    public Task<string?> GetAsync(string account, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken); byte[] name = Encoding.UTF8.GetBytes(account);
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service, (uint)name.Length, name, out uint length, out IntPtr data, out IntPtr item);
        if (status == NotFound) return Task.FromResult<string?>(null);
        Ensure(status);
        try { byte[] value = new byte[length]; Marshal.Copy(data, value, 0, checked((int)length)); try { return Task.FromResult<string?>(Encoding.UTF8.GetString(value)); } finally { Array.Clear(value); } }
        finally { if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data); if (item != IntPtr.Zero) CFRelease(item); }
    }
    public Task SetAsync(string account, string secret, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken); byte[] name = Encoding.UTF8.GetBytes(account), value = Encoding.UTF8.GetBytes(secret);
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service, (uint)name.Length, name, out _, out IntPtr existing, out IntPtr item);
        try
        {
            if (status == NotFound) Ensure(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)_service.Length, _service, (uint)name.Length, name, (uint)value.Length, value, out item));
            else { Ensure(status); Ensure(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)value.Length, value)); }
            return Task.CompletedTask;
        }
        finally { Array.Clear(value); if (existing != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, existing); if (item != IntPtr.Zero) CFRelease(item); }
    }
    public Task DeleteAsync(string account, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken); byte[] name = Encoding.UTF8.GetBytes(account);
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service, (uint)name.Length, name, out _, out IntPtr data, out IntPtr item);
        if (status == NotFound) return Task.CompletedTask;
        Ensure(status);
        try { Ensure(SecKeychainItemDelete(item)); return Task.CompletedTask; }
        finally { if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data); if (item != IntPtr.Zero) CFRelease(item); }
    }
    private static void Check(CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("当前凭证存储使用 macOS 钥匙串。"); }
    private static void Ensure(int status) { if (status != 0) throw new ProviderException("keychain_error", $"macOS 钥匙串访问失败 (OSStatus {status})。"); }
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint passwordLength, byte[] passwordData, out IntPtr itemRef);
    [DllImport(Security)] private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr value);
}

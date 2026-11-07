using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Lux3D.Provider;

/// <summary>
/// Current-user DPAPI encrypted files, with protected ACLs allowing only that user and SYSTEM.
/// Uses .NET's included ACL assemblies and Windows APIs; no Credential Manager blob-size truncation.
/// Storage requires a local, persistent-ACL filesystem and a normal, non-reparse user profile path.
/// Same-user processes and administrators are outside this credential store's protection boundary.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiCredentialStore : ICredentialStore
{
    public const int MaximumSecretBytes = 1024 * 1024;
    private const int MaximumEncryptedBytes = MaximumSecretBytes + 128 * 1024;
    private static readonly byte[] PayloadMagic = "LXDPAPI1"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _service;
    private readonly SemaphoreSlim _gate = new(1);

    // Construction does not access native storage or create directories.
    public WindowsDpapiCredentialStore(string service = CredentialStoreFactory.DefaultService)
    {
        ValidateName(service, nameof(service)); _service = service;
    }

    public async Task<string?> GetAsync(string account, CancellationToken cancellationToken = default)
    {
        Check(account, cancellationToken);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = GetDirectory(create: false);
            if (directory is null) return null;
            string path = AccountPath(directory, account);
            if (!IsRegularPath(path, directory: false, allowMissing: true)) return null;
            byte[] encrypted;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan))
            {
                // Files receive their protected ACL at CreateNew; a read-only handle must not
                // attempt WRITE_DAC/WRITE_OWNER. Share.Delete permits another process to atomically
                // replace the pathname while this read continues against its complete old snapshot.
                if (stream.Length is <= 0 or > MaximumEncryptedBytes) throw Error(global::Lux3D.Core.LocalText.Get("Invalid Windows credential file size. Sign in again."));
                encrypted = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(encrypted, cancellationToken).ConfigureAwait(false);
                if (stream.ReadByte() != -1) throw Error(global::Lux3D.Core.LocalText.Get("The Windows credential file changed while being read."));
            }
            byte[]? plain = null;
            try
            {
                plain = Transform(encrypted, account, protect: false);
                cancellationToken.ThrowIfCancellationRequested();
                return DecodePayload(plain);
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { throw StorageError(ex); }
        finally { _gate.Release(); }
    }

    public async Task SetAsync(string account, string secret, CancellationToken cancellationToken = default)
    {
        Check(account, cancellationToken); ArgumentNullException.ThrowIfNull(secret);
        int byteCount;
        try { byteCount = Utf8.GetByteCount(secret); }
        catch (EncoderFallbackException) { throw Error(global::Lux3D.Core.LocalText.Get("The credential contains invalid text encoding.")); }
        if (byteCount is <= 0 or > MaximumSecretBytes) throw Error(global::Lux3D.Core.LocalText.Get("Credentials must be between 1 byte and 1 MiB; oversized values will not be truncated or stored."));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null; byte[]? plain = null; byte[]? encrypted = null;
        try
        {
            plain = EncodePayload(secret, byteCount);
            encrypted = Transform(plain, account, protect: true);
            cancellationToken.ThrowIfCancellationRequested();
            string directory = GetDirectory(create: true)!;
            string destination = AccountPath(directory, account);
            IsRegularPath(destination, directory: false, allowMissing: true);
            temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            // CreateNew + explicit protected DACL means no permissive temporary-file window.
            using (var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Write,
                FileShare.None, 4096, FileOptions.WriteThrough, FileAcl(CurrentUser())))
            {
                await stream.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            IsRegularPath(directory, directory: true, allowMissing: false);
            IsRegularPath(destination, directory: false, allowMissing: true);
            // Same-directory rename replaces the previous complete ciphertext. No copy/delete fallback.
            // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH; never COPY_ALLOWED.
            if (!MoveFileExW(temporary, destination, 0x1 | 0x8)) throw NativeError(global::Lux3D.Core.LocalText.Get("write"));
            temporary = null;
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { throw StorageError(ex); }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string account, CancellationToken cancellationToken = default)
    {
        Check(account, cancellationToken);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = GetDirectory(create: false);
            if (directory is null) return;
            string path = AccountPath(directory, account);
            if (!IsRegularPath(path, directory: false, allowMissing: true)) return;
            cancellationToken.ThrowIfCancellationRequested(); File.Delete(path);
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { throw StorageError(ex); }
        finally { _gate.Release(); }
    }

    private string? GetDirectory(bool create)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local)) throw Error(global::Lux3D.Core.LocalText.Get("Windows LocalAppData is unavailable."));
        local = Path.GetFullPath(local);
        string root = Path.GetPathRoot(local)!;
        if (root.StartsWith("\\\\", StringComparison.Ordinal)) throw Error(global::Lux3D.Core.LocalText.Get("Windows credentials require a local user directory; network-redirected directories are not supported."));
        if (!GetVolumeInformationW(root, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, out uint flags, IntPtr.Zero, 0)) throw NativeError(global::Lux3D.Core.LocalText.Get("volume check"));
        if ((flags & 0x8) == 0) throw Error(global::Lux3D.Core.LocalText.Get("The credential directory's filesystem does not support persistent ACLs; credentials cannot be stored securely."));
        // Reject junctions/symlinks/mount points in the actual profile path before following them.
        for (DirectoryInfo? part = new(local); part is not null; part = part.Parent)
            IsRegularPath(part.FullName, directory: true, allowMissing: false);
        var owner = CurrentUser();
        string path = local;
        foreach (string name in new[] { "Lux3D Rhino", "Credentials", Hash(_service) })
        {
            path = Path.Combine(path, name);
            bool exists = IsRegularPath(path, directory: true, allowMissing: true);
            if (!exists && !create) return null;
            var directory = new DirectoryInfo(path);
            if (!exists) directory.Create(DirectoryAcl(owner));
            IsRegularPath(path, directory: true, allowMissing: false);
            // Creation is a no-op for existing directories, so explicitly remove inherited/old grants.
            directory.SetAccessControl(DirectoryAcl(owner));
        }
        return path;
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw Error(global::Lux3D.Core.LocalText.Get("Could not determine the current Windows user identity."));
    }
    private static DirectorySecurity DirectoryAcl(SecurityIdentifier owner)
    {
        var acl = new DirectorySecurity(); acl.SetOwner(owner); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }
    private static FileSecurity FileAcl(SecurityIdentifier owner)
    {
        var acl = new FileSecurity(); acl.SetOwner(owner); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return acl;
    }
    private static bool IsRegularPath(string path, bool directory, bool allowMissing)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) when (allowMissing) { return false; }
        catch (DirectoryNotFoundException) when (allowMissing) { return false; }
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw Error(global::Lux3D.Core.LocalText.Get("The Windows credential path contains a link or an unexpected file type; access was stopped."));
        return true;
    }
    private static string AccountPath(string directory, string account) => Path.Combine(directory, Hash(account) + ".dpapi");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value))).ToLowerInvariant();
    private static byte[] EncodePayload(string secret, int length)
    {
        byte[] payload = new byte[PayloadMagic.Length + sizeof(int) + length + SHA256.HashSizeInBytes];
        PayloadMagic.CopyTo(payload, 0); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), length);
        Utf8.GetBytes(secret.AsSpan(), payload.AsSpan(12, length));
        SHA256.HashData(payload.AsSpan(0, 12 + length), payload.AsSpan(12 + length));
        return payload;
    }
    private static string DecodePayload(byte[] payload)
    {
        if (payload.Length < 45 || !payload.AsSpan(0, 8).SequenceEqual(PayloadMagic)) throw Error(global::Lux3D.Core.LocalText.Get("Invalid Windows credential format. Sign in again."));
        int length = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4));
        if (length is <= 0 or > MaximumSecretBytes || payload.Length != 12 + length + SHA256.HashSizeInBytes) throw Error(global::Lux3D.Core.LocalText.Get("Invalid Windows credential length. Sign in again."));
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes]; SHA256.HashData(payload.AsSpan(0, 12 + length), digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, payload.AsSpan(12 + length))) throw Error(global::Lux3D.Core.LocalText.Get("Windows credential integrity check failed. Sign in again."));
        return Utf8.GetString(payload.AsSpan(12, length));
    }
    private byte[] Transform(byte[] input, string account, bool protect)
    {
        // Context binding prevents copying a ciphertext file to a different service/account slot.
        byte[] entropy = SHA256.HashData(Utf8.GetBytes("Lux3D DPAPI v1\0" + _service + "\0" + account));
        GCHandle inputHandle = default, entropyHandle = default;
        DataBlob output = default;
        try
        {
            inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            var data = new DataBlob { Length = (uint)input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var extra = new DataBlob { Length = (uint)entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            // CRYPTPROTECT_UI_FORBIDDEN only; never CRYPTPROTECT_LOCAL_MACHINE.
            bool ok = protect ? CryptProtectData(ref data, null, ref extra, IntPtr.Zero, IntPtr.Zero, 0x1, out output)
                : CryptUnprotectData(ref data, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, 0x1, out output);
            if (!ok) throw NativeError(protect ? global::Lux3D.Core.LocalText.Get("encryption") : global::Lux3D.Core.LocalText.Get("decryption (the Windows user or profile may have changed)"));
            if (output.Data == IntPtr.Zero || output.Length is 0 or > MaximumEncryptedBytes) throw Error(global::Lux3D.Core.LocalText.Get("Windows DPAPI returned an invalid data length."));
            byte[] result = new byte[checked((int)output.Length)]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            try
            {
                if (output.Data != IntPtr.Zero && output.Length <= MaximumEncryptedBytes)
                {
                    // Clear valid DPAPI native results before freeing them; never iterate an
                    // unexpected native length without a bound. LocalFree must run even on failure.
                    byte[] zeros = new byte[4096];
                    for (uint offset = 0; offset < output.Length; offset += (uint)zeros.Length)
                        Marshal.Copy(zeros, 0, IntPtr.Add(output.Data, (int)offset), (int)Math.Min((uint)zeros.Length, output.Length - offset));
                }
            }
            finally
            {
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
                if (inputHandle.IsAllocated) inputHandle.Free();
                if (entropyHandle.IsAllocated) entropyHandle.Free();
                CryptographicOperations.ZeroMemory(entropy);
            }
        }
    }

    private static void Check(string account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(global::Lux3D.Core.LocalText.Get("DPAPI credential storage is available only on Windows."));
        ValidateName(account, nameof(account));
    }
    private static void ValidateName(string value, string argument)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException(global::Lux3D.Core.LocalText.Get("Invalid credential service or account identifier."), argument);
    }
    private static bool IsStorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or CryptographicException or DecoderFallbackException;
    private static ProviderException StorageError(Exception ex) => Error(global::Lux3D.Core.LocalText.Format("Windows secure credential storage access failed ({0}); check current-user permissions or sign in again.", ex.GetType().Name));
    private static ProviderException NativeError(string action) => Error(global::Lux3D.Core.LocalText.Format("Windows credential {0} failed (Win32 {1}).", action, Marshal.GetLastWin32Error()));
    private static ProviderException Error(string message) => new("windows_credential_store", message);

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public uint Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string source, string destination, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, IntPtr volumeName, uint nameSize, IntPtr serialNumber, IntPtr componentLength, out uint flags, IntPtr fileSystemName, uint fileSystemNameSize);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr LocalFree(IntPtr memory);
}

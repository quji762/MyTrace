using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;

namespace Pulse.Storage;

/// <summary>
/// The credential vault: structured secrets (one JSON bundle per provider) encrypted
/// with DPAPI CurrentUser and written under %LOCALAPPDATA%\PulseWin\vault.
/// CurrentUser-encrypted content only the current user can decrypt (the .NET docs
/// list passwords/keys as the canonical use). Machine-serial key derivation is
/// explicitly forbidden, as is plaintext JSON — see the migration guide's security table.
/// Windows Credential Manager (CredWrite/CredRead) holds the master wrapping key so
/// the vault file alone is useless on another machine or another Windows user.
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly string VaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "vault");

    private readonly object _lock = new();

    public string? GetSecret(ProviderId provider, string accountId)
    {
        lock (_lock)
        {
            var entry = ReadEntry(provider);
            return entry?.Secret;
        }
    }

    public void SetSecret(ProviderId provider, string accountId, string secret)
    {
        lock (_lock)
        {
            var entry = new VaultEntry
            {
                Provider = provider.ToString(),
                AccountId = accountId,
                Secret = secret,
                UpdatedAt = DateTimeOffset.Now,
            };
            WriteEntry(provider, entry);
        }
    }

    public void RemoveSecret(ProviderId provider, string accountId)
    {
        lock (_lock)
        {
            var path = EntryPath(provider);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public static bool IsWindowsSupported() =>
        OperatingSystem.IsWindows();

    // --- Per-provider encrypted entries -----------------------------------------

    private static string EntryPath(ProviderId provider) =>
        Path.Combine(VaultDirectory, $"{provider}.bin");

    private sealed record VaultEntry
    {
        public string Provider { get; init; } = "";
        public string AccountId { get; init; } = "";
        public string Secret { get; init; } = "";
        public DateTimeOffset UpdatedAt { get; init; }
    }

    private static VaultEntry? ReadEntry(ProviderId provider)
    {
        var path = EntryPath(provider);
        if (!File.Exists(path)) return null;

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var plaintext = ProtectedData.Unprotect(encrypted);
            var json = Encoding.UTF8.GetString(plaintext);
            return JsonSerializer.Deserialize<VaultEntry>(json);
        }
        catch (Exception)
        {
            // A corrupted or foreign-user entry is not a credential; treat as absent
            // rather than crashing the refresh pass.
            return null;
        }
    }

    private static void WriteEntry(ProviderId provider, VaultEntry entry)
    {
        Directory.CreateDirectory(VaultDirectory);
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry));
        var encrypted = ProtectedData.Protect(plaintext);
        File.WriteAllBytes(EntryPath(provider), encrypted);
    }
}

/// <summary>
/// Minimal Windows Credential Manager interop (CredRead/CredWrite/CredDelete), used
/// as the preferred store for simple API-key secrets per the migration guide.
/// The target name names the provider so nothing but this app's entries are touched.
/// </summary>
public sealed class CredentialManagerStore : ICredentialStore
{
    private const string TargetPrefix = "PulseWin.";

    public string? GetSecret(ProviderId provider, string accountId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return CredRead(TargetName(provider));
    }

    public void SetSecret(ProviderId provider, string accountId, string secret)
    {
        if (!OperatingSystem.IsWindows()) return;
        CredWrite(TargetName(provider), secret);
    }

    public void RemoveSecret(ProviderId provider, string accountId)
    {
        if (!OperatingSystem.IsWindows()) return;
        CredDelete(TargetName(provider));
    }

    private static string TargetName(ProviderId provider) => TargetPrefix + provider;

    // --- interop ----------------------------------------------------------------

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private const uint CRED_TYPE_GENERIC = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    private static string? CredRead(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var credentialPtr))
            return null;
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    private static void CredWrite(string target, string secret)
    {
        var blob = Encoding.UTF8.GetBytes(secret);
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var credential = new CREDENTIAL
            {
                Flags = 0,
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException($"CredWrite failed (error {Marshal.GetLastWin32Error()})");
        }
        finally
        {
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    private static void CredDelete(string target)
    {
        _ = CredDelete(target, CRED_TYPE_GENERIC, 0);
    }
}

/// <summary>Native DPAPI entry point (System.Security.Cryptography.ProtectedData
/// equivalent, kept dependency-light here).</summary>
internal static class ProtectedData
{
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static byte[] Protect(byte[] plaintext)
    {
        var input = FromByteArray(plaintext);
        if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                CRYPTPROTECT_UI_FORBIDDEN, out var output) || output.pbData == IntPtr.Zero)
            throw new InvalidOperationException("DPAPI protect failed");
        return ToByteArray(output);
    }

    public static byte[] Unprotect(byte[] encrypted)
    {
        var input = FromByteArray(encrypted);
        if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                CRYPTPROTECT_UI_FORBIDDEN, out var output) || output.pbData == IntPtr.Zero)
            throw new InvalidOperationException("DPAPI unprotect failed");
        return ToByteArray(output);
    }

    private static DATA_BLOB FromByteArray(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = data.Length };
        blob.pbData = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] ToByteArray(DATA_BLOB blob)
    {
        try
        {
            var bytes = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(blob.pbData);
        }
    }
}

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;

namespace Pulse.Storage;

/// <summary>
/// The credential vault: one DPAPI CurrentUser blob per provider, holding every
/// account slot for that provider. CurrentUser already binds the blob to this
/// Windows user. A random key in Credential Manager is supplied as DPAPI entropy,
/// so the file alone is not enough even for the same user profile copied elsewhere
/// without that key. Machine-serial derivation and plaintext JSON are not used.
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "vault");

    private readonly string _directory;
    private readonly Func<byte[]?> _entropy;
    private readonly object _lock = new();

    public DpapiCredentialStore()
        : this(null, null)
    {
    }

    /// <param name="directory">Vault folder. Tests pass a temp directory.</param>
    /// <param name="entropy">Wrapping key. Null uses Credential Manager; a func that returns null skips entropy.</param>
    public DpapiCredentialStore(string? directory, Func<byte[]?>? entropy)
    {
        _directory = directory ?? DefaultDirectory;
        _entropy = entropy ?? CredentialManagerStore.GetOrCreateEntropy;
    }

    public string? GetSecret(ProviderId provider, string accountId)
    {
        lock (_lock)
        {
            var secrets = Load(provider);
            return secrets.TryGetValue(accountId, out var secret) ? secret : null;
        }
    }

    public void SetSecret(ProviderId provider, string accountId, string secret)
    {
        lock (_lock)
        {
            var secrets = Load(provider);
            secrets[accountId] = secret;
            Save(provider, secrets);
        }
    }

    public void RemoveSecret(ProviderId provider, string accountId)
    {
        lock (_lock)
        {
            var secrets = Load(provider);
            if (!secrets.Remove(accountId)) return;
            if (secrets.Count == 0)
            {
                var path = EntryPath(provider);
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            Save(provider, secrets);
        }
    }

    public static bool IsWindowsSupported() =>
        OperatingSystem.IsWindows();

    private string EntryPath(ProviderId provider) =>
        Path.Combine(_directory, $"{provider}.bin");

    private Dictionary<string, string> Load(ProviderId provider)
    {
        var path = EntryPath(provider);
        if (!File.Exists(path)) return new Dictionary<string, string>();

        try
        {
            var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), _entropy());
            var json = Encoding.UTF8.GetString(plaintext);
            var (secrets, legacy) = Parse(json, provider);
            if (legacy)
            {
                try { Save(provider, secrets); }
                catch (Exception) { /* the old blob still reads; rewrite on the next save */ }
            }

            return secrets;
        }
        catch (Exception)
        {
            // A corrupted or foreign-user entry is not a credential.
            return new Dictionary<string, string>();
        }
    }

    private void Save(ProviderId provider, Dictionary<string, string> secrets)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(new VaultBundle(secrets));
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), _entropy());
        File.WriteAllBytes(EntryPath(provider), encrypted);
    }

    /// <summary>
    /// Current shape is <c>{"Secrets":{accountId: secret}}</c>. The previous file
    /// was one <c>VaultEntry</c> and is read as that account's secret.
    /// </summary>
    private static (Dictionary<string, string> Secrets, bool Legacy) Parse(string json, ProviderId provider)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return (new Dictionary<string, string>(), false);

        if (root.TryGetProperty("Secrets", out var secrets) && secrets.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>();
            foreach (var property in secrets.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                    map[property.Name] = value;
            }

            return (map, false);
        }

        if (root.TryGetProperty("Secret", out var secret) && secret.ValueKind == JsonValueKind.String
            && secret.GetString() is { } legacySecret)
        {
            var id = root.TryGetProperty("AccountId", out var account)
                     && account.ValueKind == JsonValueKind.String
                     && !string.IsNullOrEmpty(account.GetString())
                ? account.GetString()!
                : provider.ToString();
            return (new Dictionary<string, string> { [id] = legacySecret }, true);
        }

        return (new Dictionary<string, string>(), false);
    }

    private sealed record VaultBundle(Dictionary<string, string> Secrets);
}

/// <summary>
/// Windows Credential Manager (CredRead/CredWrite/CredDelete). The vault's
/// entropy key lives here. Per-account targets are available for callers that
/// store a secret directly; the primary account still reads a legacy target
/// written before account ids were part of the name.
/// </summary>
public sealed class CredentialManagerStore : ICredentialStore
{
    private const string TargetPrefix = "PulseWin.";
    private const string EntropyTarget = "PulseWin.VaultEntropy";

    public string? GetSecret(ProviderId provider, string accountId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return CredRead(TargetName(provider, accountId))
               ?? (accountId == provider.ToString() ? CredRead(LegacyTarget(provider)) : null);
    }

    public void SetSecret(ProviderId provider, string accountId, string secret)
    {
        if (!OperatingSystem.IsWindows()) return;
        CredWrite(TargetName(provider, accountId), secret);
    }

    public void RemoveSecret(ProviderId provider, string accountId)
    {
        if (!OperatingSystem.IsWindows()) return;
        CredDelete(TargetName(provider, accountId));
        if (accountId == provider.ToString())
            CredDelete(LegacyTarget(provider));
    }

    /// <summary>32 random bytes stored once. Null when Credential Manager cannot be written.</summary>
    public static byte[]? GetOrCreateEntropy()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var existing = CredRead(EntropyTarget);
            if (!string.IsNullOrEmpty(existing))
            {
                var parsed = Convert.FromBase64String(existing);
                if (parsed.Length > 0) return parsed;
            }

            var created = RandomNumberGenerator.GetBytes(32);
            CredWrite(EntropyTarget, Convert.ToBase64String(created));
            return created;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TargetName(ProviderId provider, string accountId) =>
        TargetPrefix + provider + "." + accountId;

    private static string LegacyTarget(ProviderId provider) => TargetPrefix + provider;

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

/// <summary>
/// Native DPAPI (crypt32). Optional entropy is the Credential Manager wrapping key.
/// Input buffers are freed on every path, including failure. Output from
/// CryptProtectData is released with LocalFree.
/// </summary>
public static class ProtectedData
{
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CryptProtectData")]
    private static extern bool CryptProtectPlain(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CryptProtectData")]
    private static extern bool CryptProtectEntropy(
        ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CryptUnprotectData")]
    private static extern bool CryptUnprotectPlain(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CryptUnprotectData")]
    private static extern bool CryptUnprotectEntropy(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static byte[] Protect(byte[] plaintext, byte[]? entropy = null)
    {
        var input = Alloc(plaintext);
        var extra = Alloc(entropy);
        try
        {
            DATA_BLOB output;
            bool ok;
            if (extra.pbData != IntPtr.Zero)
            {
                var copy = extra;
                ok = CryptProtectEntropy(ref input, null, ref copy, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out output);
            }
            else
            {
                ok = CryptProtectPlain(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out output);
            }

            if (!ok || output.pbData == IntPtr.Zero)
                throw new InvalidOperationException("DPAPI protect failed");
            return Read(output);
        }
        finally
        {
            Free(input);
            Free(extra);
        }
    }

    /// <summary>
    /// Decrypt. When <paramref name="entropy"/> is set, a blob written before the
    /// wrapping key existed is still accepted (the call without entropy).
    /// </summary>
    public static byte[] Unprotect(byte[] encrypted, byte[]? entropy = null)
    {
        var input = Alloc(encrypted);
        var extra = Alloc(entropy);
        try
        {
            if (extra.pbData != IntPtr.Zero)
            {
                var copy = extra;
                if (CryptUnprotectEntropy(ref input, IntPtr.Zero, ref copy, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out var withKey)
                    && withKey.pbData != IntPtr.Zero)
                    return Read(withKey);
            }

            if (CryptUnprotectPlain(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var plain)
                && plain.pbData != IntPtr.Zero)
                return Read(plain);

            throw new InvalidOperationException("DPAPI unprotect failed");
        }
        finally
        {
            Free(input);
            Free(extra);
        }
    }

    private static DATA_BLOB Alloc(byte[]? data)
    {
        if (data is not { Length: > 0 }) return default;
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = ptr };
    }

    private static void Free(DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero)
            Marshal.FreeHGlobal(blob.pbData);
    }

    private static byte[] Read(DATA_BLOB blob)
    {
        try
        {
            var bytes = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            // CryptProtectData allocates with LocalAlloc. FreeHGlobal calls LocalFree.
            Marshal.FreeHGlobal(blob.pbData);
        }
    }
}

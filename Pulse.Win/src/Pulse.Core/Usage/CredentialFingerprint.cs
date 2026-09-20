using System.Security.Cryptography;

namespace Pulse.Core.Usage;

/// <summary>
/// SHA-256 hex fingerprint of a credential, used as a stable cache-scope identity
/// that is never the credential itself (upstream Devin pattern).
/// </summary>
public static class CredentialFingerprint
{
    public static string Of(string secret)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

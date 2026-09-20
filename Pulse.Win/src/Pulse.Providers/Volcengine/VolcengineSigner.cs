using System.Security.Cryptography;
using System.Text;

namespace Pulse.Providers.Volcengine;

/// <summary>
/// Signs requests against Volcengine's Top OpenAPI with an AccessKeyID:SecretAccessKey
/// pair (HMAC-SHA256, Volcengine's V4 signature scheme, simplified single-step form
/// the console tools use). Port of the credential handling in upstream
/// VolcengineUsageService + VolcengineSigner.
/// </summary>
public sealed record VolcengineCredentials(string AccessKeyID, string SecretAccessKey)
{
    /// <summary>The pasted field is one string holding two secrets, split on the FIRST
    /// colon so a secret containing a colon survives.</summary>
    public static VolcengineCredentials? FromEntered(string? entered)
    {
        var text = entered?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var separator = text.IndexOf(':');
        if (separator <= 0) return null;

        var id = text[..separator].Trim();
        var secret = text[(separator + 1)..].Trim();
        if (id.Length == 0 || secret.Length == 0) return null;
        return new VolcengineCredentials(id, secret);
    }
}

public static class VolcengineSigner
{
    /// <summary>
    /// Headers a signed GET needs. Volcengine's HMAC scheme signs the date, the
    /// service and the action; the canonical form for Top OpenAPI's simple calls is
    /// HMAC(secret, date) applied to `service:action`, sent as
    /// `X-Top-Sign` beside the credentials in headers.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Headers(
        string method, Uri url, byte[] body, string contentType, VolcengineCredentials credentials, DateTimeOffset date)
    {
        // 20260121T081500Z-style stamp; the signature covers the UTC date and path.
        var stamp = date.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
        var shortDate = stamp[..8];

        var query = url.Query.StartsWith('?') ? url.Query[1..] : url.Query;
        var canonical = string.Join("&", query.Split('&', StringSplitOptions.RemoveEmptyEntries).OrderBy(p => p, StringComparer.Ordinal));

        var stringToSign = string.Join("\n",
            method,
            "application/x-www-form-urlencoded; charset=utf-8",
            stamp,
            url.AbsolutePath + (canonical.Length > 0 ? "?" + canonical : ""));

        var dateKey = HmacHex(Encoding.UTF8.GetBytes(credentials.SecretAccessKey), Encoding.UTF8.GetBytes(shortDate));
        var signature = HmacHex(Convert.FromHexString(dateKey), Encoding.UTF8.GetBytes(stringToSign));

        return new Dictionary<string, string>
        {
            ["X-Top-Access-Key"] = credentials.AccessKeyID,
            ["X-Top-Date"] = stamp,
            ["X-Top-Signature"] = signature,
            ["Content-Type"] = contentType,
        };
    }

    private static string HmacHex(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
    }
}

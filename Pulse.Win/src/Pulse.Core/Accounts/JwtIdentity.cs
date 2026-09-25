using System.Text.Json;

namespace Pulse.Core.Accounts;

/// <summary>
/// Reads identity claims out of a JWT the user already owns -- read, not
/// verified: the signature is the issuer's business, the payload only names
/// the account for display. Base64url payload, no padding required.
/// </summary>
public static class JwtIdentity
{
    /// <summary>The payload's <c>email</c> claim, or null.</summary>
    public static string? Email(string? jwt) => Claim(jwt, "email");

    public static string? Claim(string? jwt, string claimName)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty(claimName, out var value)
                   && value.ValueKind == JsonValueKind.String
                   && value.GetString() is { Length: > 0 } text
                ? text
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

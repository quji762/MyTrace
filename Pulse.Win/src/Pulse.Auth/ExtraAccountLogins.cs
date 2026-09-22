using System.Net;
using System.Text;
using System.Text.Json;

namespace Pulse.Auth;

/// <summary>
/// xAI Grok device flow; port of upstream OAuthLogin.Configuration(.grok) with
/// deviceFlow: .standard â€?the RFC 8628 specification's own shape (unlike OpenAI's).
/// Parameters from xAI's discovery document, client id is the CLI's.
///
/// Scope set is deliberate: `grok-cli:access` gates the CLI proxy, `email` keeps two
/// Grok accounts from both being offered as "Grok"; profile/api/conversation scopes
/// are dropped (the last would let this app read and write chats). `billing:read`
/// looks right and is REFUSED by the endpoint â€?do not add it.
/// </summary>
public sealed class GrokDeviceLogin
{
    public const string DeviceCodeUrl = "https://auth.x.ai/oauth2/device/code";
    public const string TokenUrl = "https://auth.x.ai/oauth2/token";
    public const string ClientID = "b1a00492-073a-47ea-816f-4c329264a828";
    public static readonly string[] Scopes = ["openid", "email", "offline_access", "grok-cli:access"];

    public async Task<DevicePrompt> StartAsync(CancellationToken cancellationToken = default)
    {
        using var client = HttpClientFactory.Shared();
        var response = await client.PostAsync(DeviceCodeUrl,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientID,
                ["scope"] = string.Join(" ", Scopes),
            }), cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"x.ai device endpoint: HTTP {(int)response.StatusCode}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var code = Str(root, "user_code");
        var handle = Str(root, "device_code");
        var page = Str(root, "verification_uri");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(handle) || string.IsNullOrEmpty(page))
            throw new InvalidOperationException("x.ai refused the device-code request");

        // xAI DOES offer verification_uri_complete; using it is the service's own
        // decision (GitHub deliberately sends none â€?that is the difference).
        var complete = Str(root, "verification_uri_complete");
        var interval = Num(root, "interval") ?? 5;
        // user_code is what the person types. device_code is what the token poll sends.
        return new DevicePrompt(code!, complete ?? page!, TimeSpan.FromSeconds(Math.Max(interval, 1)), complete, handle);
    }

    /// <summary>The device_code retained on the prompt. Polling with the user_code never completes.</summary>
    public static string RequireDeviceCode(DevicePrompt prompt) =>
        string.IsNullOrEmpty(prompt.DeviceCode)
            ? throw new InvalidOperationException("x.ai device_code was not retained")
            : prompt.DeviceCode;

    /// <summary>
    /// RFC 8628 polling: waiting and refusal BOTH arrive as a 400 â€?the body's
    /// `error` is what separates them, never the status.
    /// </summary>
    public async Task<OAuthTokens?> WaitForTokensAsync(DevicePrompt prompt, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.Now + TimeSpan.FromMinutes(15);
        using var client = HttpClientFactory.Shared();

        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await PollAsync(client, RequireDeviceCode(prompt), cancellationToken).ConfigureAwait(false);
            if (outcome.Tokens is { } tokens) return tokens;
            if (!outcome.StillWaiting) return null;
            await Task.Delay(prompt.Interval, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<(OAuthTokens? Tokens, bool StillWaiting)> PollAsync(
        HttpClient client, string deviceCode, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode,
            ["client_id"] = ClientID,
        });
        using var response = await client.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var access = Str(root, "access_token");
            if (string.IsNullOrEmpty(access)) return (null, false);
            var refresh = Str(root, "refresh_token");
            var expiresIn = Num(root, "expires_in");
            return (new OAuthTokens(access!, refresh,
                expiresIn is { } e ? DateTimeOffset.Now + TimeSpan.FromSeconds(e) : null), true);
        }

        // Both waiting and refusal arrive as a 400; the body decides.
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = Str(document.RootElement, "error");
            switch (error)
            {
                case "authorization_pending": return (null, true);
                case "slow_down": return (null, true);
                default: return (null, false);
            }
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static double? Num(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(property.GetString(), out var d) => d,
            _ => null,
        };
    }
}

/// <summary>
/// Grok Bot's extra accounts via Cursor's web login; port of upstream
/// CursorWebLogin. NOT OAuth: Cursor publishes no authorize/token pair for a third
/// party. 1) open the login page with a challenge+uuid, 2) poll â€?**404 means "not
/// yet"**, only a 200 with accessToken ends it.
///
/// The verifier is 32 random bytes base64url-encoded; the challenge is the base64url
/// of the SHA-256 of THAT ENCODED STRING, not of the raw bytes (read out of
/// Grok Bot.app upstream, not inferred). Tokens last ~60 days; no refresh endpoint.
/// </summary>
public sealed class CursorWebLogin
{
    private const string Website = "https://cursor.com";
    private const string Backend = "https://api2.cursor.sh";

    public sealed record Attempt(string LoginUrl, string Uuid, string Verifier);

    public Attempt Start()
    {
        // Challenge = SHA-256 of the ENCODED verifier string (Pkce.Create already does this).
        var (verifier, challenge) = Pkce.Create();
        var uuid = Guid.NewGuid().ToString();

        var loginUrl = $"{Website}/loginDeepControl?challenge={Uri.EscapeDataString(challenge)}" +
                       $"&uuid={Uri.EscapeDataString(uuid)}&mode=login&redirectTarget=sand";
        return new Attempt(loginUrl, uuid, verifier);
    }

    /// <summary>Polls until granted; 404 means "not yet", a 403 with an error body
    /// is the only hard stop. Returns the access token, or null on timeout.</summary>
    public async Task<OAuthTokens?> WaitForTokenAsync(Attempt attempt, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.Now + TimeSpan.FromMinutes(10);
        using var client = HttpClientFactory.Shared();

        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pollUrl = $"{Backend}/auth/poll?uuid={Uri.EscapeDataString(attempt.Uuid)}" +
                          $"&verifier={Uri.EscapeDataString(attempt.Verifier)}";
            using var response = await client.GetAsync(pollUrl, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                var access = Str(document.RootElement, "accessToken");
                if (!string.IsNullOrEmpty(access))
                {
                    var refresh = Str(document.RootElement, "refreshToken");
                    return new OAuthTokens(access!, refresh, null); // ~60 days, no refresh endpoint
                }
                // 200 without a token is still "not yet" (upstream keeps polling).
            }
            else if ((int)response.StatusCode == 403)
            {
                return null; // the only hard stop
            }
            // 404 and everything else: still waiting.

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }
}

/// <summary>
/// Token refresh for the three OAuth providers with refresh endpoints (Claude,
/// Codex, Grok). Carries the old refresh token forward when the reply omits one.
/// Never touches a CLI's own stored login, so a renewal cannot sign the user out
/// of their CLI (upstream OAuthLogin.refresh).
/// </summary>
public static class OAuthRefresh
{
    public static async Task<OAuthTokens?> RefreshAsync(
        ProviderConfig config, OAuthTokens current, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(current.RefreshToken)) return null;

        using var client = HttpClientFactory.Shared();
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["client_id"] = config.ClientID,
            ["scope"] = string.Join(" ", config.Scopes),
        };
        string body;
        string contentType;
        if (config.SendsJson)
        {
            body = JsonSerializer.Serialize(fields);
            contentType = "application/json";
        }
        else
        {
            // Build the form body without FormUrlEncodedContent's sync read.
            body = string.Join("&", fields.Select(kv =>
                Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
            contentType = "application/x-www-form-urlencoded";
        }

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        using var response = await client.PostAsync(config.TokenUrl, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var access = Str(root, "access_token");
        if (string.IsNullOrEmpty(access)) return null;

        // A reply without a refresh token keeps the old one valid; carry it forward.
        var refresh = Str(root, "refresh_token") ?? current.RefreshToken;
        var expiresIn = Num(root, "expires_in");
        return new OAuthTokens(access!, refresh,
            expiresIn is { } e ? DateTimeOffset.Now + TimeSpan.FromSeconds(e) : null);
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static double? Num(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var d) => d,
            _ => null,
        };
    }
}

/// <summary>The OAuth parameters per provider, read once (upstream Configuration.of).</summary>
public sealed record ProviderConfig(
    string ClientID,
    string TokenUrl,
    string[] Scopes,
    bool SendsJson,
    bool ExchangeCarriesState)
{
    public static ProviderConfig Claude { get; } = new(
        ClaudeLoopbackLogin.ClientID, ClaudeLoopbackLogin.TokenUrl, ClaudeLoopbackLogin.Scopes,
        SendsJson: true, ExchangeCarriesState: true);

    public static ProviderConfig Codex { get; } = new(
        OpenAIDeviceLogin.ClientID, OpenAIDeviceLogin.TokenUrl, OpenAIDeviceLogin.Scopes,
        SendsJson: false, ExchangeCarriesState: false);

    public static ProviderConfig Grok { get; } = new(
        GrokDeviceLogin.ClientID, GrokDeviceLogin.TokenUrl, GrokDeviceLogin.Scopes,
        SendsJson: false, ExchangeCarriesState: false);
}

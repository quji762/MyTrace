using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pulse.Auth;

/// <summary>
/// Token bundle an OAuth/device flow produces; stored via ICredentialStore with the
/// refresh token beside the access token so added accounts can renew independently
/// of the CLI's own login (upstream AccountCredentials).
/// </summary>
public sealed record OAuthTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt)
{
    public string Serialize() =>
        JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["access_token"] = AccessToken,
            ["refresh_token"] = RefreshToken,
            ["expires_at"] = ExpiresAt?.ToUnixTimeMilliseconds().ToString(),
        });

    public static OAuthTokens? Deserialize(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.TrimStart().StartsWith('{')) return null;
        try
        {
            using var document = JsonDocument.Parse(stored);
            var root = document.RootElement;
            var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            DateTimeOffset? expires = root.TryGetProperty("expires_at", out var e) &&
                                      long.TryParse(e.GetString(), out var millis)
                ? DateTimeOffset.FromUnixTimeMilliseconds(millis)
                : null;
            return new OAuthTokens(access!, refresh, expires);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The user-facing step of a device flow: a code on screen.</summary>
public sealed record DevicePrompt(string UserCode, string VerificationUrl, TimeSpan Interval, string? VerificationUrlComplete = null);

/// <summary>
/// GitHub Copilot device flow; port of upstream GitHubDeviceLogin.
/// Drives the VS Code Copilot plugin's public client, asking for `read:user` and
/// NOTHING else — a security decision, not a missing scope field: the usage endpoint
/// accepts any GitHub token, and borrowing one that carries repo/workflow hands a
/// percentage over the run of someone's source code.
///
/// The verification link must NOT carry the code: GitHub deliberately sends no
/// `verification_uri_complete`, and pre-filling is the device-code phishing attack.
/// Codes last fifteen minutes; polling patience matches.
/// </summary>
public static class GitHubDeviceLogin
{
    public const string ClientID = "Iv1.b507a08c87ecfe98";
    public const string Scope = "read:user";
    private const string CodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";

    public static async Task<DevicePrompt> StartAsync(CancellationToken cancellationToken = default)
    {
        using var client = HttpClientFactory.Shared();
        var response = await client.PostAsync(CodeUrl,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientID,
                ["scope"] = Scope,
            }), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var userCode = root.TryGetProperty("user_code", out var uc) ? uc.GetString() : null;
        var deviceCode = root.TryGetProperty("device_code", out var dc) ? dc.GetString() : null;
        var verification = root.TryGetProperty("verification_uri", out var vu) ? vu.GetString() : null;
        if (string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(verification))
            throw new InvalidOperationException("GitHub refused the device-code request");
        var interval = root.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out var i) ? Math.Clamp(i, 1, 60) : 5;

        // Only GitHub's own verification_uri_complete would be used; it never sends one.
        var complete = root.TryGetProperty("verification_uri_complete", out var vuc) ? vuc.GetString() : null;
        return new DevicePrompt(userCode, verification, TimeSpan.FromSeconds(interval), complete);
    }

    /// <summary>Polls until the user finishes or the deadline passes. Returns null on timeout.</summary>
    public static async Task<string?> WaitForTokenAsync(DevicePrompt prompt, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.Now + TimeSpan.FromMinutes(15);
        using var client = HttpClientFactory.Shared();

        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await client.PostAsync(TokenUrl,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientID,
                    ["device_code"] = prompt.UserCode.Length > 0 ? DeviceCodeOf(prompt) : "",
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }), cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.TryGetProperty("access_token", out var token) &&
                token.GetString() is { } access && access.Length > 0)
                return access;

            // The specification's own words, which GitHub does use here — unlike
            // the OpenAI flow, where 403/404 stand in for "still waiting".
            var error = root.TryGetProperty("error", out var err) ? err.GetString() : null;
            switch (error)
            {
                case "slow_down":
                    await Task.Delay(prompt.Interval + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    continue;
                case "authorization_pending" or null:
                    await Task.Delay(prompt.Interval, cancellationToken).ConfigureAwait(false);
                    continue;
                default:
                    throw new InvalidOperationException($"GitHub device flow refused: {error}");
            }
        }
        return null;
    }

    private static string DeviceCodeOf(DevicePrompt prompt) => prompt.VerificationUrlComplete ?? _deviceCodeBackingField;

    [ThreadStatic] private static string? _deviceCodeBackingField;
}

/// <summary>Shared HTTP client for auth flows.</summary>
public static class HttpClientFactory
{
    public static HttpClient Shared() => new() { Timeout = TimeSpan.FromSeconds(20) };
}

/// <summary>
/// Claude Code loopback OAuth; port of upstream OAuthLogin.Configuration(.claudeCode)
/// and LoopbackCallback. Redirect flow on ANY free loopback port, path /callback.
/// Scopes: `user:profile` ONLY — the CLI also asks inference/session scopes, which
/// would let this app spend the plan it is only supposed to report on. Token endpoint
/// takes JSON and the exchange carries `state`.
/// </summary>
public sealed class ClaudeLoopbackLogin
{
    public const string AuthorizeUrl = "https://claude.com/cai/oauth/authorize";
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    public const string ClientID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string RedirectPath = "/callback";
    public static readonly string[] Scopes = ["user:profile"];

    private readonly int _port;

    public ClaudeLoopbackLogin(int? port = null)
    {
        _port = port ?? GetFreePort();
    }

    public string RedirectUri => $"http://127.0.0.1:{_port}{RedirectPath}";

    /// <summary>The URL to open in the default browser.</summary>
    public string BuildAuthorizeUrl(string state)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = ClientID,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(" ", Scopes),
            ["state"] = state,
            ["code"] = "true", // extra authorize item the CLI sends
        };
        return AuthorizeUrl + "?" + string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value ?? "")}"));
    }

    /// <summary>Waits (up to timeout) for the browser's redirect carrying the code.</summary>
    public async Task<(string Code, string State)?> WaitForCallbackAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        try
        {
            listener.Start();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            using var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            var request = Encoding.UTF8.GetString(buffer, 0, read);

            // Minimal HTTP response so the browser tab closes cleanly.
            var response = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nConnection: close\r\n\r\n<html><body>You can close this window.</body></html>";
            var responseBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(responseBytes, cts.Token).ConfigureAwait(false);

            var line = request.Split("\r\n").FirstOrDefault() ?? "";
            var target = line.Split(' ').ElementAtOrDefault(1) ?? "";
            var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";
            var parameters = query.Split('&')
                .Select(pair => pair.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));

            if (!parameters.TryGetValue("code", out var code) || !parameters.TryGetValue("state", out var state))
                return null;
            return (code, state);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Exchanges the code. The token endpoint takes JSON and carries `state`.</summary>
    public static async Task<OAuthTokens?> ExchangeAsync(string code, string state, string redirectUri, string codeVerifier, CancellationToken cancellationToken = default)
    {
        using var client = HttpClientFactory.Shared();
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["state"] = state,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = ClientID,
            ["code_verifier"] = codeVerifier,
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(access)) return null;
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var seconds)
            ? DateTimeOffset.Now + TimeSpan.FromSeconds(seconds)
            : (DateTimeOffset?)null;
        return new OAuthTokens(access!, refresh, expiresIn);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

/// <summary>
/// OpenAI device-code flow for Codex; port of upstream OAuthLogin.DeviceFlow.openAI.
/// NOT RFC 8628: poll returns 403/404 for "still waiting", the token grant needs the
/// provider-generated proof key, and the exchange does NOT carry `state`. Scopes must
/// be the FULL published set — a narrower set ends on OpenAI's error page.
/// </summary>
public sealed class OpenAIDeviceLogin
{
    public const string BaseUrl = "https://auth.openai.com/api/accounts";
    public const string ClientID = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string TokenUrl = "https://auth.openai.com/oauth/token";
    public const string VerificationUrl = "https://auth.openai.com/codex/device";
    public static readonly string[] Scopes =
    [
        "openid", "profile", "email", "offline_access",
        "api.connectors.read", "api.connectors.invoke",
    ];

    public async Task<DevicePrompt> StartAsync(CancellationToken cancellationToken = default)
    {
        using var client = HttpClientFactory.Shared();
        using var content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["client_id"] = ClientID }),
            Encoding.UTF8, "application/json");
        var response = await client.PostAsync(BaseUrl + "/deviceauth/usercode", content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var code = Str(root, "user_code") ?? Str(root, "usercode");
        var id = Str(root, "device_auth_id");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(id))
            throw new InvalidOperationException("OpenAI refused the device-code request");
        var interval = Num(root, "interval") ?? 5;
        DeviceAuthId = id;
        return new DevicePrompt(code!, VerificationUrl, TimeSpan.FromSeconds(Math.Max(interval, 1)));
    }

    /// <summary>
    /// Polls; 403 and 404 both mean "still waiting" (their convention). On grant,
    /// exchanges the authorization code with the proof key the provider generated.
    /// </summary>
    public async Task<OAuthTokens?> WaitForTokensAsync(DevicePrompt prompt, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.Now + TimeSpan.FromMinutes(15);
        using var client = HttpClientFactory.Shared();
        var deviceAuthID = DeviceAuthId!;
        var userCode = prompt.UserCode;

        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var poll = await PollAsync(client, deviceAuthID, userCode, cancellationToken).ConfigureAwait(false);
            if (poll is { } pollResult && pollResult.Code is { } grantedCode && pollResult.Verifier is { } grantedVerifier)
            {
                var payload = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = grantedCode,
                    ["redirect_uri"] = "https://auth.openai.com/deviceauth/callback",
                    ["client_id"] = ClientID,
                    ["code_verifier"] = grantedVerifier,
                });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var root = document.RootElement;
                var access = Str(root, "access_token");
                if (string.IsNullOrEmpty(access)) return null;
                var refresh = Str(root, "refresh_token");
                var expiresIn = Num(root, "expires_in");
                return new OAuthTokens(
                    access!,
                    refresh,
                    expiresIn is { } e ? DateTimeOffset.Now + TimeSpan.FromSeconds(e) : null);
            }
            if (poll is not { } p || !p.StillWaiting) return null;
            await Task.Delay(prompt.Interval, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private string? DeviceAuthId;

    public void SetDeviceAuthId(string id) => DeviceAuthId = id;

    private async Task<(string? Code, string? Verifier, bool StillWaiting)?> PollAsync(
        HttpClient client, string deviceAuthID, string userCode, CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["device_auth_id"] = deviceAuthID,
                ["user_code"] = userCode,
            }),
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(BaseUrl + "/deviceauth/token", content, cancellationToken).ConfigureAwait(false);

        // Their convention: 403 and 404 both mean the user has not finished.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            return ((string? Code, string? Verifier, bool StillWaiting)?)(null, null, true);
        if (!response.IsSuccessStatusCode)
            return ((string? Code, string? Verifier, bool StillWaiting)?)(null, null, false);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var code = Str(root, "authorization_code") ?? Str(root, "code");
        var verifier = Str(root, "code_verifier") ?? Str(root, "verifier");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(verifier)) return ((string? Code, string? Verifier, bool StillWaiting)?)(null, null, false);
        return ((string? Code, string? Verifier, bool StillWaiting)?)(code, verifier, true);
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

/// <summary>PKCE helper (verifier = 32 random bytes base64url; challenge = base64url of its SHA-256).</summary>
public static class Pkce
{
    public static (string Verifier, string Challenge) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64Url(bytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

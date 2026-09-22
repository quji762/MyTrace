using System.Net.Http.Headers;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Ollama;

/// <summary>
/// Ollama Cloud, read from the signed-in settings page HTML; port of upstream
/// OllamaCloudClient/OllamaCloudUsageService. A browser SESSION, not a key: the
/// credential is a pasted Cookie header the user copies from their own browser's
/// network tab. Windows policy per the migration guide: never auto-decrypt Chrome
/// cookies (App-Bound Encryption is a security boundary, not an obstacle) — paste
/// or a later isolated WebView2 login, nothing else.
///
/// The page parser keeps the upstream contract: both the session and the weekly
/// window must be present; a changed or partially rendered page must never turn
/// a missing window into zero usage. HTML parsing uses regex over the labeled
/// usage blocks (the upstream XMLDocument XPath equivalent on .NET).
/// </summary>
public sealed class OllamaCloudProvider : HttpUsageProviderBase
{
    public const string SettingsUrl = "https://ollama.com/settings";

    private readonly Func<string?, string?> _credentialResolver;

    public OllamaCloudProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.OllamaCloud;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.OllamaCloud);
    protected override string Endpoint => SettingsUrl;

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context) =>
        _credentialResolver(account.AccountId);

    protected override HttpRequestMessage BuildRequest(string credential)
    {
        var header = OllamaSessionCookie.Normalize(credential);
        var request = new HttpRequestMessage(HttpMethod.Get, EndpointOrDefault);
        request.Headers.TryAddWithoutValidation("Cookie", header);
        request.Headers.Accept.ParseAdd("text/html");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }

    // The base's JSON parse does not apply: this endpoint answers HTML.
    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now) =>
        throw new NotSupportedException("HTML endpoint; parsed in ReadAsync");

    public override async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        var raw = ResolveCredential(account, context);
        if (string.IsNullOrWhiteSpace(raw))
            return ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, "session missing");

        string html;
        try
        {
            using var httpClient = HttpClientFactory.CreateClient();
            using var request = BuildRequest(raw!);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return ProviderReadResult.Failed(ProviderReadHealth.Unauthorized, "session expired");
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return ProviderReadResult.Failed(ProviderReadHealth.RateLimited);
            if (!response.IsSuccessStatusCode)
                return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, $"HTTP {(int)response.StatusCode}");
            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "unreachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "timeout");
        }

        try
        {
            var snapshot = OllamaCloudPage.Parse(html);
            var windows = new List<UsageWindow>
            {
                new("ollama.session", UsageWindowKind.FiveHour, null, snapshot.Session.UsedFraction,
                    5 * 3600, snapshot.Session.ResetsAt, IsExhausted: false),
                new("ollama.weekly", UsageWindowKind.Weekly, null, snapshot.Weekly.UsedFraction,
                    7 * 86400, snapshot.Weekly.ResetsAt, IsExhausted: false),
            };
            return ProviderReadResult.Ok(new ProviderUsage(
                ProviderId.OllamaCloud, account.AccountId, windows, context.Now,
                UsageState.Live, null, null, null, UsageRoute.WebSession));
        }
        catch (OllamaPageException ex)
        {
            return ProviderReadResult.Failed(
                ex.Kind == OllamaPageFailure.SignedOut ? ProviderReadHealth.Unauthorized : ProviderReadHealth.SchemaChanged,
                ex.Kind.ToString());
        }
    }
}

/// <summary>
/// Retain only authentication cookies; analytics and unrelated cookies are neither
/// saved nor forwarded. Do not accept bare API keys or header injection (upstream
/// OllamaSessionCookie.Normalize).
/// </summary>
public static class OllamaSessionCookie
{
    private static readonly string[] Names =
    [
        "wos-session", "__Secure-session", "__Secure-next-auth.session-token", "next-auth.session-token",
    ];

    public static string Normalize(string input)
    {
        if (input.Any(c => c < 32 || c > 126))
            throw new OllamaPageException(OllamaPageFailure.InvalidCookie);
        var header = input.Trim();
        if (header.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            header = header[7..].Trim();
        if (header.Length == 0)
            throw new OllamaPageException(OllamaPageFailure.MissingCookie);
        if (header.Length > 32_768)
            throw new OllamaPageException(OllamaPageFailure.InvalidCookie);

        var found = new List<string>();
        var seen = new HashSet<string>();
        foreach (var pair in header.Split(';'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
                throw new OllamaPageException(OllamaPageFailure.InvalidCookie);
            var name = parts[0].Trim();
            var value = parts[1].Trim();
            // A suffixed host variant (`wos-session.123456`) counts as the base name.
            var recognized = Names.Any(baseName =>
                name == baseName ||
                (name.StartsWith(baseName + ".") &&
                 name[(baseName.Length + 1)..].All(char.IsDigit) &&
                 name.Length > baseName.Length + 1));
            if (!recognized) continue;
            if (value.Length == 0 || value.Contains('"') || value.Contains('\\') || value.Contains(' '))
                throw new OllamaPageException(OllamaPageFailure.InvalidCookie);
            // A repeated name is not malformed: host-only and domain rows both
            // appear in every browser store. The first wins.
            if (!seen.Add(name)) continue;
            found.Add($"{name}={value}");
        }
        if (found.Count == 0)
            throw new OllamaPageException(OllamaPageFailure.InvalidCookie);
        return string.Join("; ", found);
    }
}

public enum OllamaPageFailure
{
    MissingCookie,
    InvalidCookie,
    SignedOut,
    InvalidPage,
}

public sealed class OllamaPageException(OllamaPageFailure kind) : Exception(kind.ToString())
{
    public OllamaPageFailure Kind { get; } = kind;
}

/// <summary>HTML page parser: the labeled usage meters. Both windows are required.</summary>
public static class OllamaCloudPage
{
    public const int MaximumBytes = 2 * 1024 * 1024;

    public static (UsageSnapshot Session, UsageSnapshot Weekly) Parse(string html)
    {
        if (html.Length > MaximumBytes || html.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
            throw new OllamaPageException(OllamaPageFailure.InvalidPage);

        // Strip scripts/styles so embedded text cannot masquerade as usage.
        foreach (var tag in new[] { "script", "style", "template" })
            html = System.Text.RegularExpressions.Regex.Replace(
                html, $"<{tag}\\b[^>]*>.*?</{tag}>", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        // A sign-in form is the page saying the session is over.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                html, "<form[^>]*action=\"[^\"]*(signin|login)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new OllamaPageException(OllamaPageFailure.SignedOut);

        // Require BOTH windows: a changed or partially rendered page must never
        // turn the missing window into zero usage.
        return (Window("Session usage", html), Window("Weekly usage", html));
    }

    public sealed record UsageSnapshot(double UsedFraction, DateTimeOffset? ResetsAt);

    /// <summary>`NN% used` inside the labeled block, with at most one `data-time` reset.</summary>
    private static UsageSnapshot Window(string label, string html)
    {
        // The block runs from the label to the next section label or the end.
        var labelIndex = html.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (labelIndex < 0) throw new OllamaPageException(OllamaPageFailure.InvalidPage);
        var other = label == "Session usage" ? "Weekly usage" : "Session usage";
        var otherIndex = html.IndexOf(other, labelIndex + label.Length, StringComparison.OrdinalIgnoreCase);
        var block = html.Substring(labelIndex, (otherIndex < 0 ? html.Length : otherIndex) - labelIndex);

        // The provider's explicit total, "NN% used", not a model segment's width.
        var percentMatches = System.Text.RegularExpressions.Regex.Matches(block,
            @"([0-9]+(?:\.[0-9]+)?)\s*%\s*used", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (percentMatches.Count > 1)
            throw new OllamaPageException(OllamaPageFailure.InvalidPage);
        if (percentMatches.Count == 0)
            throw new OllamaPageException(OllamaPageFailure.InvalidPage);
        var percent = double.Parse(percentMatches[0].Groups[1].Value);
        if (!double.IsFinite(percent) || percent is < 0 or > 100)
            throw new OllamaPageException(OllamaPageFailure.InvalidPage);

        // At most one reset stamp inside the block.
        var times = System.Text.RegularExpressions.Regex.Matches(block, @"data-time=""([^""]+)""");
        if (times.Count > 1)
            throw new OllamaPageException(OllamaPageFailure.InvalidPage);
        DateTimeOffset? reset = null;
        if (times.Count == 1)
        {
            if (!DateTimeOffset.TryParse(times[0].Groups[1].Value, null,
                    System.Globalization.DateTimeStyles.None, out var parsed))
                throw new OllamaPageException(OllamaPageFailure.InvalidPage);
            reset = parsed;
        }

        return new UsageSnapshot(percent / 100, reset);
    }
}

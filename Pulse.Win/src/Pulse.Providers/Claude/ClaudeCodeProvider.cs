using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Claude;

/// <summary>
/// Reads Claude Code's usage; port of upstream ClaudeCodeUsageService (endpoint
/// route). GET api.anthropic.com/api/oauth/usage (undocumented) with the OAuth
/// access token Claude Code already stored — on Windows the CLI writes
/// `~/.claude/.credentials.json` (the macOS keychain route does not exist here,
/// and DPAPI-protected variants are read through the same JSON shape).
///
/// The status-line/desktop-session fallback routes are push-based macOS app
/// integrations; they land with the Windows status-line hook integration. The
/// endpoint is the route that answers whenever asked.
/// </summary>
public sealed class ClaudeCodeProvider : HttpUsageProviderBase
{
    public const string EndpointUrl = "https://api.anthropic.com/api/oauth/usage";

    private readonly Func<string?, string?> _credentialResolver;
    private readonly Func<string?>? _cliTokenLocator;

    public ClaudeCodeProvider(Func<string?, string?>? credentialResolver = null, Func<string?>? cliTokenLocator = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
        _cliTokenLocator = cliTokenLocator;
    }

    public override ProviderId Id => ProviderId.ClaudeCode;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.ClaudeCode);
    protected override string Endpoint => EndpointUrl;

    protected override IReadOnlyDictionary<string, string> ExtraHeaders() =>
        new Dictionary<string, string>
        {
            ["anthropic-beta"] = "oauth-2025-04-20",
            ["User-Agent"] = "claude-cli (external, cli)",
        };

    /// <summary>Pasted token wins; otherwise borrow the CLI's stored login.</summary>
    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context)
    {
        var pasted = _credentialResolver(account.Label);
        if (!string.IsNullOrWhiteSpace(pasted)) return pasted.Trim();
        return _cliTokenLocator?.Invoke() ?? LocateCliToken();
    }

    public override async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        var result = await base.ReadAsync(account, context, cancellationToken).ConfigureAwait(false);

        // A network stumble shouldn't hide a perfectly good captured reading,
        // but a live reading outranks a captured one: the capture keeps its turn
        // only when there is no live reading to be had. This is a push route —
        // it only moves while a session runs — so the reading's age is part of
        // what it means.
        var needsFallback = result.Health is ProviderReadHealth.CredentialExpired
            or ProviderReadHealth.Unauthorized
            or ProviderReadHealth.ProviderUnavailable;
        if (!needsFallback) return result;

        if (Pulse.Core.ClaudeHook.StatusLineCapture.Read() is not { } captured)
            return result;

        var windows = new List<UsageWindow>();
        if (captured.FiveHourPercent is { } five)
            windows.Add(new UsageWindow(
                Id: "claudeCode.statusline.five_hour",
                Kind: UsageWindowKind.FiveHour,
                Scope: null,
                UsedFraction: Math.Clamp(five, 0, 100) / 100,
                WindowSeconds: 5 * 3600,
                ResetsAt: captured.FiveHourReset));
        if (captured.SevenDayPercent is { } seven)
            windows.Add(new UsageWindow(
                Id: "claudeCode.statusline.seven_day",
                Kind: UsageWindowKind.Weekly,
                Scope: null,
                UsedFraction: Math.Clamp(seven, 0, 100) / 100,
                WindowSeconds: 7 * 86400,
                ResetsAt: captured.SevenDayReset));

        if (windows.Count == 0) return result;

        // Between sessions the figures are whatever they were at last use:
        // older than ten minutes reads as stale, not live (upstream freshFor).
        var fresh = context.Now - captured.CapturedAt <= TimeSpan.FromMinutes(10);
        return ProviderReadResult.Ok(new ProviderUsage(
            Provider: ProviderId.ClaudeCode,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: captured.CapturedAt,
            State: fresh ? UsageState.Live : UsageState.Stale,
            Plan: null,
            CreditBalance: null,
            CreditRemaining: null,
            Origin: UsageRoute.StatusLine));
    }

    /// <summary>
    /// The credentials blob Claude Code stores on Windows: `~/.claude/.credentials.json`,
    /// `claudeAiOauth.accessToken` with `expiresAt` in milliseconds. An expired
    /// token counts as absent rather than being spent on a call that can only 401.
    /// </summary>
    public static string? LocateCliToken(string? userProfile = null)
    {
        try
        {
            var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(home, ".claude", ".credentials.json");
            if (!File.Exists(path)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
                return null;
            if (!oauth.TryGetProperty("accessToken", out var accessToken) || accessToken.ValueKind != JsonValueKind.String)
                return null;

            if (oauth.TryGetProperty("expiresAt", out var expiresAt) &&
                expiresAt.ValueKind == JsonValueKind.Number &&
                expiresAt.TryGetInt64(out var millis) &&
                DateTimeOffset.FromUnixTimeMilliseconds(millis) <= DateTimeOffset.Now)
            {
                return null; // expired
            }
            return accessToken.GetString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now) =>
        ClaudeMapping.Parse(document.RootElement, account.AccountId, now, null);
}

/// <summary>Static mapping core, test-driven against captured replies.</summary>
public static class ClaudeMapping
{
    /// <summary>
    /// `limits[]` is the fuller answer (per-model windows too); the top-level
    /// `five_hour`/`seven_day` utilization pair is the fallback. `severity` is the
    /// provider's own judgement: anything not plainly fine is spent — an unknown
    /// value errs towards "you're blocked", the safer way to be wrong. **A warning
    /// is not a block** (observed upstream at 76% used with severity warning).
    /// </summary>
    public static ProviderUsage Parse(JsonElement root, string accountId, DateTimeOffset now, string? plan)
    {
        var windows = new List<UsageWindow>();

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (Window(limit) is { } window) windows.Add(window);
            }
        }

        if (windows.Count == 0)
        {
            foreach (var (key, kind, seconds) in new[]
                     {
                         ("five_hour", UsageWindowKind.FiveHour, 5 * 3600),
                         ("seven_day", UsageWindowKind.Weekly, 7 * 86400),
                     })
            {
                if (!root.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Object) continue;
                var percent = Num(node, "utilization");
                if (percent is null) continue;

                windows.Add(new UsageWindow(
                    Id: $"claudeCode.{key}",
                    Kind: kind,
                    Scope: null,
                    UsedFraction: Math.Clamp(percent.Value, 0, 100) / 100,
                    WindowSeconds: seconds,
                    ResetsAt: Str(node, "resets_at") is { } reset && DateTimeOffset.TryParse(reset, null, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null,
                    IsExhausted: IsSpent(node)));
            }
        }

        return new ProviderUsage(
            Provider: ProviderId.ClaudeCode,
            AccountId: accountId,
            Windows: windows,
            ObservedAt: now,
            State: windows.Count == 0 ? UsageState.Unavailable : UsageState.Live,
            Plan: plan,
            CreditBalance: null,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint)
        {
            Unavailability = windows.Count == 0
                ? new Unavailability(UnavailabilityKind.SchemaChanged, "no limits reported")
                : null,
        };
    }

    private static UsageWindow? Window(JsonElement limit)
    {
        var kindName = Str(limit, "kind");
        var percent = Num(limit, "percent");
        if (kindName is null || percent is null) return null;

        UsageWindowKind kind;
        int seconds;
        switch (kindName)
        {
            case "session":
                kind = UsageWindowKind.FiveHour;
                seconds = 5 * 3600;
                break;
            case "weekly_all" or "weekly_scoped":
                kind = UsageWindowKind.Weekly;
                seconds = 7 * 86400;
                break;
            default:
                return null;
        }

        // A scoped limit names the model it applies to.
        var scope = limit.TryGetProperty("scope", out var scopeElement) &&
                    scopeElement.ValueKind == JsonValueKind.Object &&
                    scopeElement.TryGetProperty("model", out var model) &&
                    model.ValueKind == JsonValueKind.Object &&
                    model.TryGetProperty("display_name", out var displayName) &&
                    displayName.ValueKind == JsonValueKind.String
            ? displayName.GetString()
            : null;

        return new UsageWindow(
            Id: $"claudeCode.{kindName}.{scope ?? "all"}",
            Kind: kind,
            Scope: scope,
            UsedFraction: Math.Clamp(percent.Value, 0, 100) / 100,
            WindowSeconds: seconds,
            ResetsAt: Str(limit, "resets_at") is { } reset && DateTimeOffset.TryParse(reset, null, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null,
            IsExhausted: IsSpent(limit));
    }

    /// <summary>Whether Claude Code says this limit is spent.</summary>
    public static bool IsSpent(JsonElement node)
    {
        if (node.TryGetProperty("locked_reason", out var locked) && locked.ValueKind != JsonValueKind.Null)
            return true;

        if (node.TryGetProperty("severity", out var severity) && severity.ValueKind == JsonValueKind.String)
        {
            // Anything that isn't plainly fine is spent rather than guessed at.
            return severity.GetString()?.ToLowerInvariant() switch
            {
                "normal" or "ok" or "none" or "healthy" or "warning" or "warn" => false,
                _ => true,
            };
        }
        return false;
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

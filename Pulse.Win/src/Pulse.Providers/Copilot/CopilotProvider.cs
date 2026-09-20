using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Copilot;

/// <summary>
/// GitHub Copilot's quotas; port of upstream CopilotUsageService.
/// GET api.github.com/copilot_internal/user with the OAuth token in GitHub's
/// older `Authorization: token …` scheme (Bearer is refused) plus editor headers.
/// The reply reports what is LEFT (`percent_remaining` at 90 means 10% spent) —
/// inverted once here.
/// </summary>
public sealed class CopilotProvider : HttpUsageProviderBase
{
    public const string EndpointUrl = "https://api.github.com/copilot_internal/user";

    private readonly Func<string?, string?> _credentialResolver;

    public CopilotProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.Copilot;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Copilot);
    protected override string Endpoint => EndpointUrl;

    protected override AuthenticationHeaderValue? AuthHeader(string credential) =>
        // "token", not "Bearer": this endpoint takes the OAuth token in GitHub's
        // older scheme and refuses the other one.
        new AuthenticationHeaderValue("token", credential);

    protected override IReadOnlyDictionary<string, string> ExtraHeaders() =>
        new Dictionary<string, string>
        {
            ["X-Github-Api-Version"] = "2025-04-01",
            // It answers a plugin, so it is asked as one; dropping these has been
            // reported to change what comes back.
            ["Editor-Version"] = "vscode/1.96.2",
            ["Editor-Plugin-Version"] = "copilot-chat/0.26.7",
        };

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context) =>
        _credentialResolver(account.Label);

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SchemaException("root is not an object");

        var windows = CopilotMapping.Windows(root);
        if (windows.Count == 0)
            throw new SchemaException("no limits reported");

        return new ProviderUsage(
            Provider: ProviderId.Copilot,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: now,
            State: UsageState.Live,
            Plan: CopilotMapping.PlanName(GetString(root, "copilot_plan")),
            CreditBalance: null,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint);
    }
}

/// <summary>Static mapping core, driven by tests against captured replies.</summary>
public static class CopilotMapping
{
    /// <summary>The three quotas, in the order they are worth reading. `completions`
    /// is included — on a free plan it is the largest allowance of the three.</summary>
    private static readonly (string Key, string Scope)[] Lanes =
    [
        ("premium_interactions", "Premium requests"),
        ("chat", "Chat"),
        ("completions", "Completions"),
    ];

    public static List<UsageWindow> Windows(JsonElement root)
    {
        var windows = new List<UsageWindow>();
        if (!root.TryGetProperty("quota_snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Object)
            return windows;

        // One reset for the account, not one per quota: the per-snapshot
        // `quota_reset_at` comes back as 0.
        var resets = ParseDate(GetStringField(root, "quota_reset_date_utc")) ?? ParseDate(GetStringField(root, "quota_reset_date"));

        foreach (var (key, scope) in Lanes)
        {
            if (!snapshots.TryGetProperty(key, out var snapshot) || snapshot.ValueKind != JsonValueKind.Object)
                continue;
            if (Window(snapshot, scope, key, resets) is { } window) windows.Add(window);
        }
        return windows;
    }

    public static UsageWindow? Window(JsonElement snapshot, string scope, string key, DateTimeOffset? resets)
    {
        var entitlement = Num(snapshot, "entitlement") ?? 0;
        var unlimited = snapshot.TryGetProperty("unlimited", out var unlim) && unlim.ValueKind == JsonValueKind.True;

        // A quota the plan does not include is left out, not drawn at 100%:
        // it comes back with `has_quota: false`, nothing issued, 100% remaining.
        var hasQuota = snapshot.TryGetProperty("has_quota", out var hq) && hq.ValueKind == JsonValueKind.True
            ? true
            : snapshot.TryGetProperty("has_quota", out _) && hq.ValueKind == JsonValueKind.False ? false : (bool?)null;
        if (hasQuota == false) return null;

        // Without the flag, an older reply is told apart by the percentage: a
        // lane the plan excludes reads 100% remaining with nothing issued, while
        // a lane you have RUN OUT OF also has nothing left — and dropping that
        // one hides the alarm at the moment it matters.
        if (hasQuota == null && !unlimited && entitlement <= 0
            && (Num(snapshot, "remaining") ?? 0) <= 0
            && (Num(snapshot, "percent_remaining") ?? 0) >= 100)
            return null;

        // An unlimited lane has no share to show.
        if (unlimited) return null;
        var remaining = Num(snapshot, "percent_remaining");
        if (remaining is null) return null;

        var overagePermitted = snapshot.TryGetProperty("overage_permitted", out var op) && op.ValueKind == JsonValueKind.True;

        return new UsageWindow(
            Id: $"copilot.{key}",
            // A calendar month, which is what the reset date describes.
            Kind: UsageWindowKind.Monthly,
            Scope: scope,
            UsedFraction: Math.Clamp((100 - remaining.Value) / 100, 0, 1),
            // Enough to sort by, and NOT a length the service stated.
            WindowSeconds: 30 * 86400,
            ResetsAt: resets,
            ReportsLength: false,
            // Spent is not the same as over the allowance: a lane with overage
            // permitted keeps working past its included share and is billed for it.
            IsExhausted: remaining <= 0 && !overagePermitted);
    }

    /// <summary>GitHub's internal plan names, tidied; unfamiliar passed through.</summary>
    public static string? PlanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "individual" => "Individual",
            "free" => "Free",
            "business" => "Business",
            "enterprise" => "Enterprise",
            _ => raw,
        };
    }

    /// <summary>`2026-10-01T00:00:00.000Z`, or the bare `2026-10-01` older replies give.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (DateTimeOffset.TryParse(text, CultureInfoAdapter.Iso, DateTimeStyles.None, out var parsed))
            return parsed;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfoAdapter.Iso, DateTimeStyles.None, out var date))
            return new DateTimeOffset(date, TimeOnly.MinValue, TimeSpan.Zero);
        return null;
    }

    private static string? GetStringField(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    public static double? Num(JsonElement element, string name)
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

internal static class CultureInfoAdapter
{
    public static CultureInfo Iso { get; } = CultureInfo.InvariantCulture;
}

internal static class DateTimeStyles
{
    public const System.Globalization.DateTimeStyles None = System.Globalization.DateTimeStyles.None;
}

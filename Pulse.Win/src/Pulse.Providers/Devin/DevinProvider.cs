using System.Security.Cryptography;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Devine;

/// <summary>
/// Devin's quota endpoint route; port of upstream DevinUsageService (endpoint half).
/// GET app.devin.ai/api/&lt;org&gt;/billing/quota/usage with a Bearer token the user pastes.
/// The app-cache route (its editor's state.vscdb) is platform-local to the macOS app
/// store layout and lands later with the Windows locator.
///
/// The live reply reports what has been USED where the cached row reports what is
/// left (measured upstream): `daily_percentage: 2` = 2% used, no inversion here.
/// The path's shape varies (slug vs internal org id), so each spelling is tried.
/// </summary>
public sealed class DevinProvider : HttpUsageProviderBase
{
    public const string Host = "https://app.devin.ai";

    private readonly Func<string?, string?> _credentialResolver;

    public DevinProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.Devin;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Devin);

    // The endpoint value is unused directly; paths are tried in order below.
    protected override string Endpoint => Host + "/api/";

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context) =>
        _credentialResolver(account.Label);

    private DevinCredential? Parsed;

    public override async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        var raw = _credentialResolver(account.Label);
        if (string.IsNullOrWhiteSpace(raw))
            return ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, "credential missing");

        Parsed = DevinCredential.Parse(raw);
        if (Parsed is null || Parsed.Paths.Count == 0)
            return ProviderReadResult.Failed(ProviderReadHealth.SchemaChanged, "organization missing");

        // Each spelling in turn; a refused token is refused at every spelling,
        // so only an unreachable/server error is worth retrying.
        ProviderReadResult last = ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "unreachable");
        foreach (var path in Parsed.Paths)
        {
            _pinnedPath = path;
            var result = await base.ReadAsync(account, context, cancellationToken).ConfigureAwait(false);
            if (result.Usage is not null) return result;
            // Map 401/403 (Unauthorized) straight through; only continue on
            // server errors / 404-shaped provider-unavailable results.
            if (result.Health is ProviderReadHealth.Unauthorized or ProviderReadHealth.RateLimited
                or ProviderReadHealth.SchemaChanged)
                return result;
            last = result;
        }
        return last;
    }

    private string? _pinnedPath;
    protected override string EndpointOrDefault => Host + "/api/" + _pinnedPath;

    protected override HttpRequestMessage BuildRequest(string credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, EndpointOrDefault);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Accept.ParseAdd("application/json");
        // How the service picks between organisations on one account. Sent only
        // where the internal id is what was given — a slug is not one.
        if (Parsed?.InternalID is { } id)
            request.Headers.TryAddWithoutValidation("x-cog-org-id", id);
        return request;
    }

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var windows = DevinMapping.Windows(document.RootElement);
        var balance = DevinMapping.OverageBalance(document.RootElement);
        if (windows.Count == 0 && balance is null)
            throw new SchemaException("no limits reported");

        return new ProviderUsage(
            Provider: ProviderId.Devin,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: now,
            State: UsageState.Live,
            Plan: DevinMapping.PlanName(document.RootElement),
            CreditBalance: balance is { } b ? $"${b:0.00}" : null,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint);
    }
}

/// <summary>Parsed form of the pasted credential: token + organization.</summary>
public sealed record DevinCredential(string Token, string Organization, string? AccountID)
{
    /// <summary>
    /// One pasted string; accepted in any combination:
    /// `eyJ… my-team` / `Authorization: Bearer eyJ… org_1a2b3c` /
    /// `eyJ… https://app.devin.ai/org/my-team/settings`.
    /// </summary>
    public static DevinCredential? Parse(string? pasted)
    {
        var text = pasted?.Trim() ?? "";
        if (text.Length == 0) return null;

        // A whole header line, as copied out of a browser's network tab.
        var colon = text.IndexOf(':');
        if (colon > 0 && text[..colon].ToLowerInvariant() == "authorization")
            text = text[(colon + 1)..].Trim();
        if (text.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
            text = text[7..].Trim();

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length == 0) return null;

        var token = parts[0];
        var organization = parts.Length > 1 ? Normalize(parts[1]) : null;
        return new DevinCredential(token, organization ?? "", AccountID: null);
    }

    /// <summary>A slug, an internal `org_…` id, or an app.devin.ai URL carrying one.</summary>
    public static string? Normalize(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0) return null;

        if (Uri.TryCreate(value, UriKind.Absolute, out var url) &&
            (url.Host == "devin.ai" || url.Host.EndsWith(".devin.ai")))
        {
            var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && (segments[0] == "org" || segments[0] == "organizations"))
                value = $"{segments[0]}/{segments[1]}";
        }

        value = value.Trim('/');
        if (value.Length == 0) return null;
        if (value.StartsWith("org/") || value.StartsWith("organizations/")) return value;
        return IsInternalID(value) ? $"organizations/{value}" : $"org/{value}";
    }

    public static bool IsInternalID(string value) =>
        value.StartsWith("org_") || value.StartsWith("org-");

    public string? InternalID =>
        Organization.StartsWith("organizations/") ? Organization["organizations/".Length..] : null;

    /// <summary>The paths to try, in order (the API is undocumented; spellings vary).</summary>
    public System.Collections.Generic.List<string> Paths
    {
        get
        {
            var paths = new System.Collections.Generic.List<string>();
            if (Organization.Length == 0) return paths;
            if (InternalID is { } id) paths.Add(id);
            paths.Add(Organization);
            if (Organization.StartsWith("org/"))
            {
                var slug = Organization["org/".Length..];
                paths.Add(slug);
                if (IsInternalID(slug)) paths.Add($"organizations/{slug}");
            }
            var seen = new System.Collections.Generic.HashSet<string>();
            return paths.Where(seen.Add).Select(p => $"{p}/billing/quota/usage").ToList();
        }
    }
}

/// <summary>Static mapping core, test-driven.</summary>
public static class DevinMapping
{
    /// <summary>
    /// The reply reports USED percentages (measured upstream: the cached row said
    /// 98%/99% remaining while this said 2%/1% used). CodexBar reads a value of 1
    /// or less as a fraction and multiplies by 100 — deliberately NOT copied: a
    /// genuine 0.4% used would then be drawn as 40%.
    /// </summary>
    public static List<UsageWindow> Windows(JsonElement root)
    {
        var windows = new List<UsageWindow>();

        // Absent means yes: only an explicit false says there is no allowance.
        var hasQuota = !root.TryGetProperty("has_quota_allocation", out var hq) || hq.ValueKind != JsonValueKind.False;

        if (hasQuota)
        {
            var hideDaily = root.TryGetProperty("hide_daily_quota", out var hd) && hd.ValueKind == JsonValueKind.True;
            var hideWeekly = root.TryGetProperty("hide_weekly_quota", out var hw) && hw.ValueKind == JsonValueKind.True;

            if (!hideDaily && Num(root, "daily_percentage") is { } daily)
            {
                windows.Add(new UsageWindow(
                    Id: "devin-daily",
                    Kind: UsageWindowKind.Daily,
                    Scope: null,
                    UsedFraction: Math.Clamp(daily / 100, 0, 1),
                    WindowSeconds: 86400,
                    ResetsAt: ParseDate(Str(root, "daily_reset_at"))));
            }

            if (!hideWeekly && Num(root, "weekly_percentage") is { } weekly)
            {
                windows.Add(new UsageWindow(
                    Id: "devin-weekly",
                    Kind: UsageWindowKind.Weekly,
                    Scope: null,
                    UsedFraction: Math.Clamp(weekly / 100, 0, 1),
                    WindowSeconds: 604800,
                    ResetsAt: ParseDate(Str(root, "weekly_reset_at"))));
            }
        }

        return windows;
    }

    public static double? OverageBalance(JsonElement root)
    {
        if (Num(root, "overage_balance") is { } dollars) return dollars;
        if (Num(root, "overage_balance_cents") is { } cents) return cents / 100;
        return null;
    }

    public static string? PlanName(JsonElement root)
    {
        foreach (var name in new[] { "plan_name", "planName", "plan", "tier" })
        {
            if (root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
                property.GetString() is { } value && value.Length > 0)
                return value;
        }
        return null;
    }

    /// <summary>ISO 8601, epoch seconds, or epoch milliseconds — all three appear.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;
        if (double.TryParse(text, out var epoch) && epoch > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch > 10_000_000_000 ? epoch : epoch * 1000));
        return null;
    }

    private static string? Str(JsonElement element, string name)
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

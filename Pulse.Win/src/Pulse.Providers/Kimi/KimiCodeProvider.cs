using System.Globalization;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Kimi;

/// <summary>
/// Kimi Code provider; port of upstream KimiCodeUsageService.
/// GET https://api.kimi.com/coding/v1/usages with Bearer API key.
/// Two limit families: limits[] timed windows (duration x timeUnit) and `usage`
/// rolling weekly (resetTime but no length -> ReportsLength=false). All counters
/// are strings; limits[].detail reports "remaining" -> inverted once at this boundary.
/// </summary>
public sealed class KimiCodeProvider : HttpUsageProviderBase
{
    public const string EndpointUrl = "https://api.kimi.com/coding/v1/usages";

    private readonly Func<string?, string?> _credentialResolver;

    public KimiCodeProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.KimiCode;

    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.KimiCode);

    protected override string Endpoint => EndpointUrl;

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context) =>
        _credentialResolver(account.Label);

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SchemaException("root is not an object");

        var windows = new List<UsageWindow>();

        // Timed windows from limits[]: window.duration x window.timeUnit.
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var limit in limits.EnumerateArray())
            {
                var seconds = WindowSeconds(limit);
                if (seconds is int s) // unknown time units drop the entry entirely (upstream rule)
                {
                    var detail = limit.TryGetProperty("detail", out var d) ? d : default;
                    var usedFraction = UsedFromDetail(detail);
                    var reset = ParseTimestamp(GetString(detail, "resetTime"));
                    windows.Add(new UsageWindow(
                        Id: $"limit.{index}.{s}",
                        Kind: UsageWindowKind.Other,
                        Scope: null,
                        UsedFraction: usedFraction,
                        WindowSeconds: s,
                        ResetsAt: reset));
                }
                index++;
            }
        }

        // Rolling weekly usage: limit/used/resetTime at top level.
        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            var usedFraction = UsedFromDetail(usageElement);
            var reset = ParseTimestamp(GetString(usageElement, "resetTime"));
            // No reported length -> seconds is a 7d sort key only.
            windows.Add(new UsageWindow(
                Id: "weekly",
                Kind: UsageWindowKind.Weekly,
                Scope: null,
                UsedFraction: usedFraction,
                WindowSeconds: 7 * 86400,
                ResetsAt: reset,
                ReportsLength: false));
        }

        windows.Sort((a, b) => a.WindowSeconds.CompareTo(b.WindowSeconds));

        string? plan = null;
        if (root.TryGetProperty("user", out var user) &&
            user.TryGetProperty("membership", out var membership))
        {
            var level = GetString(membership, "level");
            if (!string.IsNullOrEmpty(level))
            {
                var raw = level.StartsWith("LEVEL_", StringComparison.OrdinalIgnoreCase) ? level["LEVEL_".Length..] : level;
                // Title-case for display: LEVEL_INTERMEDIATE -> "Intermediate".
                plan = char.ToUpperInvariant(raw[0]) + raw[1..].ToLowerInvariant();
            }
        }

        return new ProviderUsage(
            Provider: ProviderId.KimiCode,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: now,
            State: UsageState.Live,
            Plan: plan,
            CreditBalance: null, // totalQuota empty; parallel.limit is concurrency, not balance
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint);
    }

    /// <summary>
    /// Map window.duration x window.timeUnit to seconds; unknown unit -> null (drop).
    /// </summary>
    public static int? WindowSeconds(JsonElement limit)
    {
        if (limit.ValueKind != JsonValueKind.Object) return null;
        if (!limit.TryGetProperty("window", out var window) || window.ValueKind != JsonValueKind.Object) return null;
        if (!window.TryGetProperty("duration", out var durationElement) || durationElement.ValueKind != JsonValueKind.Number)
            return null;
        if (!durationElement.TryGetInt32(out var duration)) return null;
        var unit = GetString(window, "timeUnit");
        return unit switch
        {
            "TIME_UNIT_SECOND" => duration,
            "TIME_UNIT_MINUTE" => duration * 60,
            "TIME_UNIT_HOUR" => duration * 3600,
            "TIME_UNIT_DAY" => duration * 86400,
            _ => null,
        };
    }

    /// <summary>
    /// Compute used fraction from limit/used/remaining string fields. detail reports
    /// "remaining" when "used" is absent -> invert once here (upstream boundary rule).
    /// </summary>
    public static double UsedFromDetail(JsonElement detail)
    {
        var limit = ParseCount(GetString(detail, "limit"));
        var used = ParseCount(GetString(detail, "used"));
        var remaining = ParseCount(GetString(detail, "remaining"));

        if (limit is { } l && l > 0)
        {
            if (used is { } u) return PercentNormalization.Normalize(u / l);
            if (remaining is { } r) return PercentNormalization.InvertRemaining(r / l);
        }
        return 0.0;
    }

    private static double? ParseCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
            !double.IsNaN(v) && !double.IsInfinity(v))
            return v;
        return null;
    }
}

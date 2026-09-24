using System.Globalization;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.V2EX;

/// <summary>
/// V2EX's AI Chat allowance, read with a Personal Access Token.
/// Port of upstream V2EXUsageService.
///
/// One documented route: GET https://edge.v2ex.com/api/v2/chat/quota.
///
/// **The window does not run until it is used.** V2EX starts a five-hour
/// window when it receives the next message — a reading with `active: false`
/// reports the allowance that *would* be granted and `period_end` of zero.
/// No reset time and no length are claimed when inactive.
///
/// The extra pack (`extra_usage`, 加油包) is a second allowance with its own
/// stated size, no expiry, and no window — spent only after the five-hour
/// pool is gone. Reported as a separate window, never folded in.
/// </summary>
public sealed class V2EXProvider : HttpUsageProviderBase
{
    public const string EndpointUrl = "https://edge.v2ex.com/api/v2/chat/quota";

    private readonly Func<string?, string?> _credentialResolver;

    public V2EXProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.V2EX;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.V2EX);
    protected override string Endpoint => EndpointUrl;

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context)
    {
        var pasted = _credentialResolver(account.AccountId);
        return string.IsNullOrWhiteSpace(pasted) ? null : pasted.Trim();
    }

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SchemaException("root is not an object");

        // V2EX answers 200 and says no in the body.
        if (root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
            throw new SchemaException("success=false");

        if (!root.TryGetProperty("result", out var quota) || quota.ValueKind != JsonValueKind.Object)
            throw new SchemaException("missing result object");

        var windows = BuildWindows(quota);
        if (windows.Count == 0)
            throw new SchemaException("no limits reported");

        return new ProviderUsage(
            Provider: ProviderId.V2EX,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: now,
            State: UsageState.Live,
            Plan: null,
            CreditBalance: null,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint);
    }

    /// <summary>The window, and the pack when one has been bought.</summary>
    internal static List<UsageWindow> BuildWindows(JsonElement quota)
    {
        var windows = new List<UsageWindow>();
        var isActive = quota.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;

        var used = GetDouble(quota, "used_tokens");
        var total = GetDouble(quota, "total_tokens");
        if (Fraction(used, total) is { } frac)
        {
            windows.Add(new UsageWindow(
                Id: "window",
                Kind: UsageWindowKind.FiveHour,
                Scope: null,
                UsedFraction: frac,
                WindowSeconds: 5 * 3600,
                // Only while a window is running — zero is not a date.
                ResetsAt: isActive ? UnixDate(GetDouble(quota, "period_end")) : null,
                ReportsLength: isActive,
                IsExhausted: IsSpent(GetDouble(quota, "remaining_tokens"))));
        }

        if (quota.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object
            && (GetLong(extra, "pack_count") ?? 0) > 0)
        {
            var eUsed = GetDouble(extra, "used_tokens");
            var eTotal = GetDouble(extra, "total_tokens");
            if (Fraction(eUsed, eTotal) is { } eFrac)
            {
                windows.Add(new UsageWindow(
                    Id: "extra",
                    Kind: UsageWindowKind.TopUp,
                    Scope: null,
                    UsedFraction: eFrac,
                    WindowSeconds: 30 * 86_400,
                    ResetsAt: null,
                    ReportsLength: false,
                    IsExhausted: IsSpent(GetDouble(extra, "remaining_tokens"))));
            }
        }

        return windows;
    }

    /// <summary>How much of a stated allowance is gone, or null where none was stated.</summary>
    internal static double? Fraction(double? used, double? total)
    {
        if (total is not { } t || !double.IsFinite(t) || t <= 0) return null;
        if (used is not { } u || !double.IsFinite(u)) return null;
        return Math.Clamp(u / t, 0, 1);
    }

    /// <summary>V2EX's own remainder. Absent ≠ spent.</summary>
    internal static bool IsSpent(double? remaining) =>
        remaining is { } r && double.IsFinite(r) && r <= 0;

    /// <summary>Unix seconds. Zero is not a date.</summary>
    internal static DateTimeOffset? UnixDate(double? seconds) =>
        seconds is { } s && double.IsFinite(s) && s > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)s)
            : null;

    private static new double? GetDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static long? GetLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var l) => l,
            _ => null,
        };
    }
}

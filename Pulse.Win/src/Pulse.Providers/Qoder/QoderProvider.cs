using System.Globalization;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Qoder;

/// <summary>
/// Qoder subscription credits, read through its account page API.
/// Port of upstream QoderUsageService.
/// The credential is a browser session token the user pastes (not a CLI-managed
/// key): it is stored in the local DPAPI vault and sent only as an
/// Authorization: Bearer header over HTTPS to qoder.com or qoder.com.cn.
/// totalQuota is the account's own; sharedQuota is a team pool. Two rings, never one sum.
/// Both spellings are accepted per field: the mainland reply mixes them
/// (nextResetAt in camelCase beside total_quota in snake_case).
/// </summary>
public sealed class QoderProvider : HttpUsageProviderBase
{
    private readonly Func<string?, string?> _credentialResolver;
    private readonly bool _useChina;

    public QoderProvider(Func<string?, string?>? credentialResolver = null, bool useChina = false)
    {
        _credentialResolver = credentialResolver ?? (key => key);
        _useChina = useChina;
    }

    public override ProviderId Id => ProviderId.Qoder;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Qoder);
    protected override string Endpoint =>
        _useChina ? "https://qoder.com.cn/api/v2/me/usages/big_model_credits"
                  : "https://qoder.com/api/v2/me/usages/big_model_credits";

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context)
    {
        var pasted = _credentialResolver(account.AccountId);
        return string.IsNullOrWhiteSpace(pasted) ? null : pasted.Trim();
    }

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new SchemaException("root is not an object");

        var windows = new List<UsageWindow>();

        // The personal summary must be stated and readable; its absence is a
        // schema change, not proof that no allowance remains.
        if (!TryGet(root, "totalQuota", "total_quota", out var total) || total.ValueKind != JsonValueKind.Object)
            throw new SchemaException("missing totalQuota summary");
        if (!TryGetPool(total, out var personal))
            throw new SchemaException("unreadable totalQuota summary");

        // A limit of zero is not drawn: no ring at 100% for an allowance never
        // granted. A positive limit stays a reading even at zero remaining.
        if (personal.Limit > 0)
        {
            var remaining = GetDouble(personal.Summary, "remainingValue") ?? GetDouble(personal.Summary, "remaining_value");
            var exhausted = remaining is { } r && double.IsFinite(r)
                ? r <= 0
                : personal.Used >= personal.Limit;
            windows.Add(new UsageWindow("total", UsageWindowKind.Monthly, "Total",
                Math.Clamp(personal.Used / personal.Limit, 0, 1), 30 * 86_400,
                null, false, null, exhausted)
            { Expiry = NextExpiry(total, now) });
        }

        // An absent team pool is normal; an unreadable one is not proof that no
        // allowance remains. Only a valid zero pool is omitted.
        if (TryGet(root, "sharedQuota", "shared_quota", out var sharedContainer)
            && sharedContainer.ValueKind == JsonValueKind.Object)
        {
            if (!TryGetPool(sharedContainer, out var shared))
                throw new SchemaException("unreadable sharedQuota summary");
            if (shared.Limit > 0)
            {
                var remaining = GetDouble(shared.Summary, "remainingValue") ?? GetDouble(shared.Summary, "remaining_value");
                var exhausted = remaining is { } r && double.IsFinite(r)
                    ? r <= 0
                    : shared.Used >= shared.Limit;
                windows.Add(new UsageWindow("shared", UsageWindowKind.Monthly, "Shared",
                    Math.Clamp(shared.Used / shared.Limit, 0, 1), 30 * 86_400,
                    null, false, null, exhausted));
            }
        }

        // A complete answer: the account holds no allowance at all. Storing it
        // clears the account's previous reading from memory and disk, so a
        // later failure, relaunch, or --json export cannot restore an allowance
        // Qoder has withdrawn (upstream v1.4.1).
        if (windows.Count == 0)
            return new ProviderUsage(ProviderId.Qoder, account.AccountId, windows, now,
                UsageState.Unavailable, GetString(root, "planName"), null, null, UsageRoute.WebSession)
            { Unavailability = new Unavailability(UnavailabilityKind.NoCredits) };

        return new ProviderUsage(ProviderId.Qoder, account.AccountId, windows, now,
            UsageState.Live, GetString(root, "planName"), null, null, UsageRoute.WebSession);
    }

    private readonly record struct QoderPool(double Used, double Limit, JsonElement Summary);

    /// <summary>A summary Qoder stated in full, or false. Negative figures are not
    /// a summary anybody stated; they mean the reply was misread.</summary>
    private static bool TryGetPool(JsonElement container, out QoderPool pool)
    {
        pool = default;
        if (!TryGet(container, "quotaSummary", "quota_summary", out var summary)
            || summary.ValueKind != JsonValueKind.Object)
            return false;
        var used = GetDouble(summary, "usedValue") ?? GetDouble(summary, "used_value");
        var limit = GetDouble(summary, "limitValue") ?? GetDouble(summary, "limit_value");
        if (used is not { } u || !double.IsFinite(u) || u < 0) return false;
        if (limit is not { } l || !double.IsFinite(l) || l < 0) return false;
        pool = new QoderPool(u, l, summary);
        return true;
    }

    /// <summary>
    /// Entries of quotaDetail with a stated end date: the pieces the summary
    /// adds up. Details are read for their dates only and **never allowed to
    /// cost the summary**: an entry this cannot read is left out, and a detail
    /// list it cannot read at all is an empty one. The plan's own entry carries
    /// <c>expires_at: 0</c>, no date.
    /// </summary>
    private static UsageExpiry? NextExpiry(JsonElement total, DateTimeOffset now)
    {
        if (!TryGet(total, "quotaDetail", "quota_detail", out var details)
            || details.ValueKind != JsonValueKind.Array)
            return null;

        var packs = new List<(double Remaining, DateTimeOffset At)>();
        foreach (var detail in details.EnumerateArray())
        {
            if (detail.ValueKind != JsonValueKind.Object) continue;
            if (IsExplicitFalse(detail, "isActive") || IsExplicitFalse(detail, "is_active")) continue;
            var remaining = GetDouble(detail, "remainingValue") ?? GetDouble(detail, "remaining_value");
            var expires = GetDate(detail, "expiresAt") ?? GetDate(detail, "expires_at");
            if (remaining is { } r && expires is { } e)
                packs.Add((r, e));
        }

        return UsageExpiry.Soonest(packs, now);
    }

    /// <summary>A date Qoder stated, or null: ISO 8601 text, or a Unix stamp in
    /// seconds or milliseconds; zero is no date.</summary>
    private static DateTimeOffset? GetDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var stamp) && stamp > 0 =>
                stamp > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(stamp)
                    : DateTimeOffset.FromUnixTimeSeconds(stamp),
            JsonValueKind.String when value.GetString() is { } text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>Absent or null counts as active; only an explicit false is out.</summary>
    private static bool IsExplicitFalse(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;

    /// <summary>Either spelling of a field, whichever the site replied with.</summary>
    private static bool TryGet(JsonElement element, string camel, string snake, out JsonElement value)
    {
        if (element.TryGetProperty(camel, out value)) return true;
        return element.TryGetProperty(snake, out value);
    }
}

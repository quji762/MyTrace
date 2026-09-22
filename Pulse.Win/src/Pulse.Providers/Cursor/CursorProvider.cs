using System.Globalization;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;
using Pulse.Providers.Local;

namespace Pulse.Providers.Cursor;

/// <summary>
/// Cursor's limits; port of upstream CursorUsageService.
/// GET cursor.com/api/usage-summary (undocumented) with the session cookie the
/// editor stored. The credential is NOT a browser cookie: it is built from the
/// editor's own `state.vscdb` as `WorkosCursorSessionToken: accountId::token`.
/// Windows version: the user pastes the token (or the raw cookie value); no
/// SQLite parsing of the editor's store in this milestone.
///
/// Billing model facts the parser depends on:
/// - The plan includes TWO pools (Cursor Models / Other Models), reported as
///   percentages; Grok Bot is a separate provider and is not read here.
/// - `autoPercentUsed` 0.0267 means 0.0267%, NOT 2.67% â€?a fraction would be a
///   hundredfold overstatement (settled by arithmetic upstream).
/// - Pools' 30 days are a sort key (billing cycles run 28â€?1 days):
///   ReportsLength stays false.
/// </summary>
public sealed class CursorProvider : HttpUsageProviderBase
{
    public const string EndpointUrl = "https://cursor.com/api/usage-summary";

    private readonly Func<string?, string?> _credentialResolver;

    public CursorProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.Cursor;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Cursor);
    protected override string Endpoint => EndpointUrl;

    /// <summary>The pasted value may be the raw token or a full cookie header.</summary>
    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context)
    {
        var stored = _credentialResolver(account.AccountId);
        if (string.IsNullOrWhiteSpace(stored))
        {
            if (!AccountScope.IsPrimary(account)) return null;
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var database = Path.Combine(roaming, "Cursor", "User", "globalStorage", "state.vscdb");
            stored = CursorEditorLogin.SessionCookie(database, DateTimeOffset.UtcNow);
        }

        if (string.IsNullOrWhiteSpace(stored)) return null;

        var value = stored.Trim();
        if (value.StartsWith("WorkosCursorSessionToken=", StringComparison.OrdinalIgnoreCase))
            value = value["WorkosCursorSessionToken=".Length..].Trim();
        if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            value = value["Cookie:".Length..].Trim();
        return value;
    }

    protected override IReadOnlyDictionary<string, string> ExtraHeaders() =>
        new Dictionary<string, string>
        {
            // The credential travels as a named cookie, not an Authorization header.
            // (Header injection guard: a stored value containing a newline never
            // reaches here â€?TryAddWithoutValidation would drop the request.)
            ["Cookie"] = $"WorkosCursorSessionToken={Uri.EscapeDataString(CredentialValue!)}",
        };

    private string? CredentialValue;

    public override async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        CredentialValue = ResolveCredential(account, context);
        if (string.IsNullOrEmpty(CredentialValue))
            return ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, "credential missing");

        return await base.ReadAsync(account, context, cancellationToken).ConfigureAwait(false);
    }

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SchemaException("root is not an object");

        var windows = CursorMapping.Windows(root);
        if (windows.Count == 0)
            throw new SchemaException("no limits reported");

        return new ProviderUsage(
            Provider: ProviderId.Cursor,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: now,
            State: UsageState.Live,
            Plan: CursorMapping.PlanName(GetString(root, "membershipType")),
            CreditBalance: CursorMapping.RemainingCents(root) is { } cents
                ? $"${(cents / 100).ToString("0.00", CultureInfo.InvariantCulture)}"
                : null,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint);
    }
}

/// <summary>Static mapping core, test-driven against captured replies.</summary>
public static class CursorMapping
{
    public static List<UsageWindow> Windows(JsonElement root)
    {
        var resets = root.TryGetProperty("billingCycleEnd", out var cycle) && cycle.ValueKind == JsonValueKind.String
            ? ParseTimestamp(cycle.GetString())
            : null;

        // A team account reports the same shape under another name.
        var plan = ObjectAt(root, "individualUsage", "plan") ?? ObjectAt(root, "teamUsage", "pooled");
        var found = new List<UsageWindow>();

        // The two pools the plan includes, in the order and under the names the
        // account page gives them.
        if (Pool(Num(plan, "autoPercentUsed"), "cursorModels", "Cursor Models", resets) is { } a) found.Add(a);
        if (Pool(Num(plan, "apiPercentUsed"), "otherModels", "Other Models", resets) is { } b) found.Add(b);

        // An account shape that reports no pools still has the money. Only a
        // fallback: on an account that DOES report pools this is a different
        // denominator and would read as a third, contradictory limit.
        if (found.Count == 0 && Money(plan, "plan", UsageWindowKind.Monthly, resets) is { } planWindow)
            found.Add(planWindow);

        // Spending past the plan. Off unless the account turned it on.
        var onDemand = ObjectAt(root, "individualUsage", "onDemand") ?? ObjectAt(root, "teamUsage", "onDemand");
        if (Money(onDemand, "onDemand", UsageWindowKind.Spend, resets) is { } onDemandWindow)
            found.Add(onDemandWindow);

        return found;
    }

    /// <summary>One of the plan's two pools: a PERCENTAGE, not a fraction.</summary>
    public static UsageWindow? Pool(double? percent, string id, string scope, DateTimeOffset? resets)
    {
        if (percent is null) return null;
        return new UsageWindow(
            Id: id,
            Kind: UsageWindowKind.Monthly,
            Scope: scope,
            UsedFraction: Math.Clamp(percent.Value / 100, 0, 1),
            // A billing cycle runs 28â€?1 days; thirty only orders the rows.
            WindowSeconds: 30 * 86400,
            ResetsAt: resets,
            ReportsLength: false,
            IsExhausted: false);
    }

    /// <summary>A pot measured in money (cents) rather than as a share of a pool.</summary>
    public static UsageWindow? Money(JsonElement? allowance, string id, UsageWindowKind kind, DateTimeOffset? resets)
    {
        if (allowance is not { } node || node.ValueKind != JsonValueKind.Object) return null;
        if (node.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) return null;

        var used = Num(node, "used");
        var limit = Num(node, "limit");
        if (used is null || limit is null || limit <= 0) return null;

        return new UsageWindow(
            Id: id,
            Kind: kind,
            Scope: null,
            UsedFraction: Math.Clamp(used.Value / limit.Value, 0, 1),
            WindowSeconds: 30 * 86400,
            ResetsAt: resets,
            ReportsLength: false,
            IsExhausted: false);
    }

    /// <summary>What is left of the plan's allowance, in dollars (reply carries cents).</summary>
    public static double? RemainingCents(JsonElement root)
    {
        var plan = ObjectAt(root, "individualUsage", "plan") ?? ObjectAt(root, "teamUsage", "pooled");
        return Num(plan, "remaining");
    }

    /// <summary>"pro_plus" â†?"Pro+". Unfamiliar tiers tidied and passed through.</summary>
    public static string? PlanName(string? membership)
    {
        if (string.IsNullOrEmpty(membership)) return null;
        var name = "";
        foreach (var partRaw in membership.Split('_'))
        {
            var part = partRaw == "plus" ? "+"
                : char.ToUpperInvariant(partRaw[0]) + partRaw[1..].ToLowerInvariant();
            name += name.IsEmpty() || part == "+" ? part : " " + part;
        }
        return name.Length == 0 ? null : name;
    }

    private static bool IsEmpty(this string s) => s.Length == 0;

    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;
        return null;
    }

    private static JsonElement? ObjectAt(JsonElement root, string outer, string inner)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(outer, out var o) || o.ValueKind != JsonValueKind.Object) return null;
        if (!o.TryGetProperty(inner, out var i) || i.ValueKind != JsonValueKind.Object) return null;
        return i;
    }

    public static double? Num(JsonElement? element, string name)
    {
        if (element is not { } node || node.ValueKind != JsonValueKind.Object) return null;
        if (!node.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var d) => d,
            _ => null,
        };
    }
}

namespace Pulse.Core.Providers;

/// <summary>
/// One usage/quota window for a provider account, a faithful port of upstream
/// <c>UsageWindow</c> (Sources/Pulse/Usage/ProviderUsage.swift).
/// </summary>
/// <remarks>
/// Port rules inherited from upstream (Docs/providers/README.md):
/// - Never invent percentages: <see cref="UsedFraction"/> comes only from the provider.
/// - <see cref="IsExhausted"/> is set only from a provider-reported flag, never from
///   usedFraction &gt;= 1.0.
/// - <see cref="WindowSeconds"/> may be a mere sort key when <see cref="ReportsLength"/>
///   is false; never divide by it in that case.
/// </remarks>
public sealed record UsageWindow(
    string Id,
    UsageWindowKind Kind,
    string? Scope,
    double UsedFraction,
    int WindowSeconds,
    DateTimeOffset? ResetsAt,
    bool ReportsLength = true,
    UsageEstimate? Estimate = null,
    bool IsExhausted = false,
    UsageExpiry? Expiry = null);

/// <summary>
/// Credits that leave on a date of their own (a bonus pack inside a larger
/// allowance): how many, and when. An expiry is not a reset — it takes
/// credits away rather than giving them back — so it never feeds reset
/// detection or the countdown (upstream UsageWindow.Expiry).
/// </summary>
public sealed record UsageExpiry(double Amount, DateTimeOffset At);

/// <summary>Kind of a quota window. Mirrors upstream UsageWindow.Kind.</summary>
public enum UsageWindowKind
{
    FiveHour,
    Weekly,
    Spend,
    Balance,
    Daily,
    Messages,
    Monthly,
    Other,
    TopUp,
}

/// <summary>
/// Marks that the denominator of a window is inferred rather than reported.
/// Mirrors upstream Estimate (planPrice / sinceTopUp / yourBudget).
/// </summary>
public sealed record UsageEstimate(UsageEstimateSource Source);

public enum UsageEstimateSource
{
    PlanPrice,
    SinceTopUp,
    YourBudget,
}

/// <summary>Comparable credit balance used for low-balance alerts.</summary>
public sealed record CreditAmount(double Amount, string Currency);

/// <summary>Scope of a usage reading: which account identity produced it.</summary>
public sealed record UsageScope(string Value)
{
    public static readonly UsageScope Primary = new("primary");
}

/// <summary>
/// All known usage for one provider account; port of upstream <c>ProviderUsage</c>.
/// The first window in <see cref="Windows"/> drives the main ring.
/// </summary>
public sealed record ProviderUsage(
    ProviderId Provider,
    string AccountId,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset? ObservedAt,
    UsageState State,
    string? Plan,
    string? CreditBalance,
    CreditAmount? CreditRemaining,
    UsageRoute? Origin)
{
    public static ProviderUsage Unavailable(
        ProviderId provider,
        string accountId,
        DateTimeOffset at,
        Unavailability reason) =>
        new(provider, accountId, Array.Empty<UsageWindow>(), at, UsageState.Unavailable,
            Plan: null, CreditBalance: null, CreditRemaining: null, Origin: null)
        { Unavailability = reason };

    /// <summary>Set only when <see cref="State"/> is <see cref="UsageState.Unavailable"/>.</summary>
    public Unavailability? Unavailability { get; init; }
}

public enum UsageState
{
    Live,
    Stale,
    Unavailable,
}

/// <summary>
/// Shared, provider-agnostic unavailability reasons. Port of upstream Unavailability:
/// texts never name the provider so failures do not leak provider internals into the UI.
/// </summary>
public sealed record Unavailability(UnavailabilityKind Kind, string? Detail = null);

public enum UnavailabilityKind
{
    NotConfigured,
    CredentialMissing,
    Unauthorized,
    SchemaChanged,
    ProviderUnavailable,
    RateLimited,
    UnsupportedPlatform,

    /// <summary>Appended last: persisted cache files serialize the kind as a
    /// number, so existing values must keep their place. The provider stated a
    /// complete answer — the account holds no allowance at all. An answer, not
    /// an outage, and not a ring at 100% either (upstream v1.4.1).</summary>
    NoCredits,
}

/// <summary>
/// Identity of a monitored account. The primary account id equals the provider name,
/// mirroring upstream AccountKey where account == provider.rawValue for the primary.
/// </summary>
public sealed record AccountKey(ProviderId Provider, string AccountId)
{
    public string CacheKey => string.Create(null, stackalloc char[128], $"{Provider}:{AccountId}");
}

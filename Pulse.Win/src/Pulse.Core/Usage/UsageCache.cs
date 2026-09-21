using Pulse.Core.Accounts;
using Pulse.Core.Providers;

namespace Pulse.Core.Usage;

/// <summary>
/// Last-good cache ported from upstream UsageCache semantics:
/// - Windows whose reset has passed are dropped immediately, not aged.
/// - Windows that never claim a reset age out after <see cref="MaximumAge"/> (24h upstream).
/// - Cache entries are keyed by full account scope (provider + account id) so switching
///   accounts never shows the previous account's numbers.
/// </summary>
public sealed class UsageCache
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);

    private readonly object _lock = new();
    private readonly Dictionary<string, CacheEntry> _entries = new();

    private sealed record CacheEntry(ProviderUsage Usage);

    /// <summary>Store a fresh reading, pruning expired windows first.</summary>
    public void Store(ProviderUsage usage, DateTimeOffset now)
    {
        var pruned = PruneWindows(usage, now);
        lock (_lock)
        {
            if (pruned.Windows.Count == 0 && pruned.CreditRemaining is null)
            {
                _entries.Remove(Key(pruned.Provider, pruned.AccountId));
                return;
            }

            _entries[Key(pruned.Provider, pruned.AccountId)] = new CacheEntry(pruned with
            {
                State = UsageState.Stale,
                Origin = UsageRoute.AppCache,
            });
        }
    }

    /// <summary>
    /// Return the last-good reading for an account, or null. Never returns <see cref="UsageState.Live"/>.
    /// </summary>
    public ProviderUsage? Get(ProviderId provider, string accountId, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(Key(provider, accountId), out var entry))
                return null;

            var usage = entry.Usage;
            if (usage.ObservedAt is { } observed && now - observed > MaximumAge)
            {
                _entries.Remove(Key(provider, accountId));
                return null;
            }

            return usage;
        }
    }

    public void Remove(ProviderId provider, string accountId)
    {
        lock (_lock) _entries.Remove(Key(provider, accountId));
    }

    /// <summary>Drop windows whose reset time has already passed; they must not masquerade as valid quota.</summary>
    public static ProviderUsage PruneWindows(ProviderUsage usage, DateTimeOffset now)
    {
        if (usage.Windows.Count == 0)
            return usage;

        var kept = new List<UsageWindow>(usage.Windows.Count);
        foreach (var w in usage.Windows)
        {
            // Balance/spend windows have no reset; they are governed by MaximumAge instead.
            if (w.ResetsAt is { } reset && reset <= now)
                continue;
            kept.Add(w);
        }

        return kept.Count == usage.Windows.Count ? usage : usage with { Windows = kept };
    }

    private static string Key(ProviderId provider, string accountId) =>
        new AccountKey(provider, accountId).CacheKey;
}

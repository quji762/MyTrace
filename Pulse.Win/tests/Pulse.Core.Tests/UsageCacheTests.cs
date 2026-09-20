using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Xunit;

namespace Pulse.Core.Tests;

public class UsageCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static ProviderUsage Usage(
        DateTimeOffset? resetsAt,
        DateTimeOffset? observedAt = null,
        string accountId = "claudeCode") =>
        new(
            Provider: ProviderId.ClaudeCode,
            AccountId: accountId,
            Windows: new[] { new UsageWindow("fiveHour", UsageWindowKind.FiveHour, null, 0.4, 5 * 3600, resetsAt) },
            ObservedAt: observedAt ?? Now,
            State: UsageState.Live,
            Plan: null,
            CreditBalance: null,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint);

    [Fact]
    public void Stored_Reading_Restores_As_Stale()
    {
        var cache = new UsageCache();
        cache.Store(Usage(Now.AddHours(3)), Now);

        var restored = cache.Get(ProviderId.ClaudeCode, "claudeCode", Now.AddMinutes(5));

        Assert.NotNull(restored);
        Assert.Equal(UsageState.Stale, restored!.State); // never claims Live
        Assert.Equal(UsageRoute.AppCache, restored.Origin);
    }

    [Fact]
    public void Window_Whose_Reset_Has_Passed_Is_Dropped_Not_Aged()
    {
        var cache = new UsageCache();
        cache.Store(Usage(Now.AddMinutes(-1)), Now); // reset already passed

        Assert.Null(cache.Get(ProviderId.ClaudeCode, "claudeCode", Now.AddMinutes(1)));
    }

    [Fact]
    public void Window_Without_Reset_Expires_After_24h()
    {
        var cache = new UsageCache();
        cache.Store(Usage(null), Now);

        Assert.NotNull(cache.Get(ProviderId.ClaudeCode, "claudeCode", Now.AddHours(23)));
        Assert.Null(cache.Get(ProviderId.ClaudeCode, "claudeCode", Now.AddHours(25)));
    }

    [Fact]
    public void Accounts_Are_Strictly_Isolated()
    {
        var cache = new UsageCache();
        cache.Store(Usage(Now.AddHours(3), accountId: "claudeCode"), Now);
        cache.Store(Usage(Now.AddHours(5), accountId: "extra-1"), Now);

        var primary = cache.Get(ProviderId.ClaudeCode, "claudeCode", Now)!;
        var extra = cache.Get(ProviderId.ClaudeCode, "extra-1", Now)!;

        // Switching accounts must never show the previous account's numbers.
        Assert.Equal("claudeCode", primary.AccountId);
        Assert.Equal("extra-1", extra.AccountId);
        Assert.Equal(TimeSpan.FromHours(3), primary.Windows[0].ResetsAt - Now);
    }

    [Fact]
    public void Remove_Deletes_Entry()
    {
        var cache = new UsageCache();
        cache.Store(Usage(Now.AddHours(3)), Now);
        cache.Remove(ProviderId.ClaudeCode, "claudeCode");
        Assert.Null(cache.Get(ProviderId.ClaudeCode, "claudeCode", Now));
    }
}

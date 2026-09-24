using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Qoder;
using Xunit;

namespace Pulse.ProviderContract.Tests;

/// <summary>
/// Contract tests for the Qoder provider parser, using an upstream-shaped
/// fixture (Apache-2.0, qunqin24/Pulse). Pins the v1.4.1 semantics: pack
/// expiry, dual-spelling fields, zero pools not drawn, and no-credits as an
/// answer rather than a schema fault.
/// </summary>
public class QoderContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static ProviderUsage Parse(string json)
    {
        var provider = new QoderProvider(_ => "test-cookie");
        using var document = JsonDocument.Parse(json);
        var account = new MonitoredAccount { Provider = ProviderId.Qoder, AccountId = "Qoder" };
        var method = typeof(QoderProvider).GetMethod("ParseSuccess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (ProviderUsage)method.Invoke(provider, new object[] { document, account, Now })!;
    }

    [Fact]
    public void Fixture_Parses_Personal_Window_With_Pack_Expiry()
    {
        var usage = Parse(LoadFixture("Fixtures/qoder-pro-usage.json"));

        Assert.Equal(UsageState.Live, usage.State);
        Assert.Equal(ProviderId.Qoder, usage.Provider);
        Assert.Equal(UsageRoute.WebSession, usage.Origin);

        // The zero shared pool is a placeholder: omitted, never a ring at 100%.
        var window = Assert.Single(usage.Windows);
        Assert.Equal("total", window.Id);
        Assert.Equal("Total", window.Scope);
        Assert.Equal(0.25, window.UsedFraction, 5);
        Assert.Null(window.ResetsAt);
        Assert.False(window.ReportsLength); // windowSeconds is a sort key only
        Assert.False(window.IsExhausted);   // remainingValue 375 is stated and positive

        // The soonest packs to lapse: 86 and 14 end on the same local day (one
        // hour apart, no real timezone splits them); the Nov 1 pack, the
        // expires_at 0 plan entry, the already-lapsed and inactive ones do not count.
        var expiry = window.Expiry;
        Assert.NotNull(expiry);
        Assert.Equal(100, expiry!.Amount, 5);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 0, 30, 0, TimeSpan.Zero), expiry.At);
    }

    [Fact]
    public void Mixed_Spellings_Are_Accepted_Per_Field()
    {
        const string json = """
            { "quotaKey": "big_model_credits",
              "nextResetAt": "2026-10-01T00:00:00Z",
              "total_quota": { "quota_summary": { "used_value": 10, "limit_value": 100 } },
              "shared_quota": { "quota_summary": { "used_value": 5, "limit_value": 50 } } }
            """;
        var usage = Parse(json);

        Assert.Equal(UsageState.Live, usage.State);
        Assert.Equal(2, usage.Windows.Count);
        Assert.Equal(0.1, usage.Windows[0].UsedFraction, 5);
        Assert.Equal(0.1, usage.Windows[1].UsedFraction, 5);
    }

    [Fact]
    public void Unix_Stamp_Dates_Parse_In_Seconds_And_Milliseconds()
    {
        const string json = """
            { "totalQuota": { "quotaSummary": { "usedValue": 1, "limitValue": 500 },
              "quotaDetail": [
                { "remainingValue": 7, "expiresAt": 1792283400 },
                { "remainingValue": 3, "expiresAt": 1792283400000 } ] } }
            """;
        var usage = Parse(json);

        var window = Assert.Single(usage.Windows);
        // 1792283400 is 2026-10-18T00:30:00Z; both stamps are the same instant,
        // so both packs land in the same-day sum.
        var expiry = window.Expiry;
        Assert.NotNull(expiry);
        Assert.Equal(10, expiry!.Amount, 5);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 0, 30, 0, TimeSpan.Zero), expiry.At);
    }

    [Fact]
    public void Zero_Personal_Allowance_Is_NoCredits_Answer_Not_A_Fault()
    {
        const string json = """
            { "totalQuota": { "quotaSummary": { "usedValue": 0, "limitValue": 0 } } }
            """;
        var usage = Parse(json);

        Assert.Empty(usage.Windows);
        Assert.Equal(UsageState.Unavailable, usage.State);
        Assert.NotNull(usage.Unavailability);
        Assert.Equal(UnavailabilityKind.NoCredits, usage.Unavailability!.Kind);
    }

    [Fact]
    public void Positive_Limit_At_Zero_Remaining_Is_Exhausted_Not_NoCredits()
    {
        const string json = """
            { "totalQuota": { "quotaSummary": { "usedValue": 500, "limitValue": 500, "remainingValue": 0 } } }
            """;
        var usage = Parse(json);

        Assert.Equal(UsageState.Live, usage.State);
        var window = Assert.Single(usage.Windows);
        Assert.True(window.IsExhausted); // from remainingValue, not only used >= limit
    }

    [Fact]
    public void Missing_Personal_Summary_Is_A_Schema_Change()
    {
        const string json = """{ "sharedQuota": { "quotaSummary": { "usedValue": 1, "limitValue": 10 } } }""";
        Assert.Throws<System.Reflection.TargetInvocationException>(() => Parse(json));
    }

    [Fact]
    public void Unreadable_Shared_Summary_Is_A_Schema_Change()
    {
        const string json = """
            { "totalQuota": { "quotaSummary": { "usedValue": 1, "limitValue": 10 } },
              "sharedQuota": { "somethingElse": true } }
            """;
        Assert.Throws<System.Reflection.TargetInvocationException>(() => Parse(json));
    }

    private static string LoadFixture(string relativePath)
    {
        var root = FindRepoRoot();
        return File.ReadAllText(Path.Combine(root, "Tests", "PulseTests", "Fixtures", Path.GetFileName(relativePath)));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Tests", "PulseTests")))
            dir = dir.Parent!;
        return dir!.FullName;
    }
}

using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Kimi;
using Xunit;

namespace Pulse.ProviderContract.Tests;

/// <summary>
/// Contract tests for the Kimi Code provider parser. Upstream keeps no fixture files
/// for Kimi; samples below mirror the inline JSON literals in
/// Tests/PulseTests/KimiCode*Tests and Docs/providers/kimi.md.
/// </summary>
public class KimiContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static ProviderUsage Parse(string json)
    {
        var provider = new KimiCodeProvider(_ => "test-key");
        using var document = JsonDocument.Parse(json);
        var account = new MonitoredAccount { Provider = ProviderId.KimiCode, AccountId = "kimiCode" };
        var method = typeof(KimiCodeProvider).GetMethod("ParseSuccess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (ProviderUsage)method.Invoke(provider, new object[] { document, account, Now })!;
    }

    [Fact]
    public void Timed_Window_Minutes_Is_Parsed()
    {
        const string json = """
            {
              "user": { "membership": { "level": "LEVEL_INTERMEDIATE" } },
              "usage": { "limit": "1000", "used": "250", "remaining": "750",
                         "resetTime": "2026-09-28T00:00:00Z" },
              "limits": [
                { "window": { "duration": 5, "timeUnit": "TIME_UNIT_HOUR" },
                  "detail": { "limit": "100", "used": "40", "remaining": "60",
                              "resetTime": "2026-09-21T15:30:00.123Z" } }
              ]
            }
            """;
        var usage = Parse(json);

        Assert.Equal("Intermediate", usage.Plan); // LEVEL_ prefix stripped
        Assert.Null(usage.CreditBalance);         // parallel.limit is concurrency, not balance

        var hourly = usage.Windows.Single(w => w.Id.StartsWith("limit."));
        Assert.Equal(5 * 3600, hourly.WindowSeconds);
        Assert.Equal(0.40, hourly.UsedFraction, 3); // used/limit directly
        Assert.NotNull(hourly.ResetsAt);            // fractional-second timestamp parsed

        var weekly = usage.Windows.Single(w => w.Id == "weekly");
        Assert.Equal(0.25, weekly.UsedFraction, 3);
        Assert.False(weekly.ReportsLength);         // rolling weekly: no reported length
        Assert.Equal(7 * 86400, weekly.WindowSeconds); // sort key only
    }

    [Fact]
    public void Remaining_Only_Detail_Is_Inverted_Once()
    {
        const string json = """
            {
              "usage": { "limit": "100", "used": "10", "resetTime": "2026-09-28T00:00:00Z" },
              "limits": [
                { "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
                  "detail": { "limit": "50", "remaining": "10" } }
              ]
            }
            """;
        var usage = Parse(json);
        var window = usage.Windows.Single(w => w.Id.StartsWith("limit."));
        // remaining 10 of 50 -> 80% used, inverted exactly once at the boundary.
        Assert.Equal(0.80, window.UsedFraction, 3);
    }

    [Fact]
    public void Unknown_Time_Unit_Drops_Entry()
    {
        const string json = """
            {
              "usage": { "limit": "100", "used": "10" },
              "limits": [
                { "window": { "duration": 5, "timeUnit": "TIME_UNIT_FORTNIGHT" },
                  "detail": { "limit": "50", "used": "25" } }
              ]
            }
            """;
        var usage = Parse(json);
        Assert.DoesNotContain(usage.Windows, w => w.Id.StartsWith("limit."));
        Assert.Single(usage.Windows, w => w.Id == "weekly");
    }

    [Fact]
    public void Windows_Sort_By_Duration_Ascending()
    {
        const string json = """
            {
              "usage": { "limit": "100", "used": "10", "resetTime": "2026-09-28T00:00:00Z" },
              "limits": [
                { "window": { "duration": 1, "timeUnit": "TIME_UNIT_DAY" },
                  "detail": { "limit": "50", "used": "5" } },
                { "window": { "duration": 30, "timeUnit": "TIME_UNIT_MINUTE" },
                  "detail": { "limit": "50", "used": "5" } }
              ]
            }
            """;
        var usage = Parse(json);
        var seconds = usage.Windows.Select(w => w.WindowSeconds).ToArray();
        Assert.Equal(seconds.OrderBy(s => s).ToArray(), seconds);
    }

    [Fact]
    public void String_Counters_With_Unparseable_Value_Do_Not_Crash()
    {
        const string json = """
            {
              "usage": { "limit": "unlimited", "used": "n/a" },
              "limits": []
            }
            """;
        var usage = Parse(json);
        var weekly = usage.Windows.Single(w => w.Id == "weekly");
        Assert.Equal(0.0, weekly.UsedFraction, 3);
    }
}

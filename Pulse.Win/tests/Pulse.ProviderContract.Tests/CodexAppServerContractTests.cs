using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Codex;
using Xunit;

namespace Pulse.ProviderContract.Tests;

/// <summary>
/// Contract tests for the `codex app-server` fallback response parser — the shape
/// `account/rateLimits/read` returns, which names its fields differently from the
/// HTTP endpoint (upstream CodexAppServerTests / parseAppServerResponse).
/// </summary>
public class CodexAppServerContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private const string AccountId = "codex";

    private static ProviderUsage Parse(string json) =>
        CodexMapping.ParseAppServerResponse(JsonDocument.Parse(json).RootElement.Clone(), AccountId, Now);

    [Fact]
    public void RateLimitsByLimitId_Groups_Produce_Windows()
    {
        const string json = """
            {
              "rateLimitsByLimitId": {
                "codex": {
                  "primary": { "usedPercent": 42, "windowDurationMins": 300, "resetsAt": 1789085506 },
                  "secondary": { "usedPercent": 10, "windowDurationMins": 10080, "resetsAt": 1789690306 }
                },
                "compact": {
                  "limitName": "Compaction",
                  "primary": { "usedPercent": 70, "windowDurationMins": 720 }
                }
              },
              "planType": "plus"
            }
            """;
        var usage = Parse(json);

        Assert.Equal("Plus", usage.Plan);
        Assert.Equal(3, usage.Windows.Count);

        // Unnamed account-wide group first, then named per-model ones.
        var primary = usage.Windows.First(w => w.Id == "codex.primary");
        Assert.Equal(UsageWindowKind.FiveHour, primary.Kind); // 300 min → 5h
        Assert.Equal(0.42, primary.UsedFraction, 3);
        Assert.Null(primary.Scope);

        var weekly = usage.Windows.First(w => w.Id == "codex.secondary");
        Assert.Equal(UsageWindowKind.Weekly, weekly.Kind);

        var compact = usage.Windows.First(w => w.Id == "compact.primary");
        Assert.Equal("Compaction", compact.Scope);
        Assert.Equal(UsageWindowKind.Other, compact.Kind); // 12h has no named kind
    }

    [Fact]
    public void Flat_RateLimits_Object_Is_Treated_As_Account_Wide_Group()
    {
        const string json = """
            {
              "rateLimits": {
                "primary": { "usedPercent": 25, "windowDurationMins": 300 }
              }
            }
            """;
        var usage = Parse(json);
        var window = Assert.Single(usage.Windows);
        Assert.Equal("codex.primary", window.Id);
        Assert.Null(window.Scope);
    }

    [Fact]
    public void Ordinary_Usage_Refused_Marks_The_Fullest_Window()
    {
        // ordinaryUsageAllowed is a sibling of the groups, not a member: it
        // applies to all of them, and nil is not the same as false.
        const string json = """
            {
              "rateLimitsByLimitId": {
                "codex": {
                  "primary": { "usedPercent": 90, "windowDurationMins": 300 },
                  "secondary": { "usedPercent": 20, "windowDurationMins": 10080 }
                }
              },
              "ordinaryUsageAllowed": false
            }
            """;
        var usage = Parse(json);
        var primary = usage.Windows.Single(w => w.Id == "codex.primary");
        var secondary = usage.Windows.Single(w => w.Id == "codex.secondary");
        Assert.True(primary.IsExhausted);  // the fullest window carries the mark
        Assert.False(secondary.IsExhausted);
    }

    [Fact]
    public void Unlimited_Credits_Suppress_The_Balance()
    {
        const string json = """
            {
              "rateLimits": { "primary": { "usedPercent": 5, "windowDurationMins": 300 } },
              "credits": { "unlimited": true, "balance": "120.00" }
            }
            """;
        var usage = Parse(json);
        Assert.Null(usage.CreditBalance);
    }

    [Fact]
    public void Empty_Groups_Are_Unavailable_Not_Crashing()
    {
        var usage = Parse("{}");
        Assert.Equal(UsageState.Unavailable, usage.State);
        Assert.NotNull(usage.Unavailability);
    }
}

using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.OpenCodeGo;
using Xunit;

namespace Pulse.ProviderContract.Tests;

/// <summary>
/// Contract tests for the OpenCode Go provider parser. Upstream keeps no fixture
/// files for OpenCode Go; samples mirror the inline JSON in
/// Tests/PulseTests/OpenCodeGo*Tests and Docs/providers/opencode-go.md.
/// </summary>
public class OpenCodeGoContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static ProviderUsage Parse(string json)
    {
        var provider = new OpenCodeGoProvider(_ => "test-key");
        using var document = JsonDocument.Parse(json);
        var account = new MonitoredAccount { Provider = ProviderId.OpenCodeGo, AccountId = "openCodeGo" };
        var method = typeof(OpenCodeGoProvider).GetMethod("ParseSuccess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (ProviderUsage)method.Invoke(provider, new object[] { document, account, Now })!;
    }

    [Fact]
    public void Three_Fixed_Windows_Are_Parsed()
    {
        const string json = """
            {
              "usage": {
                "rolling": { "status": "ok", "percent": 12.5, "resetsAt": "2026-09-21T16:40:00.000Z" },
                "weekly":  { "status": "ok", "percent": 3.1,  "resetsAt": "2026-09-28T00:00:00.000Z" },
                "monthly": { "status": "ok", "percent": 0.8,  "resetsAt": "2026-10-21T00:00:00.000Z" }
              }
            }
            """;
        var usage = Parse(json);

        Assert.Equal(3, usage.Windows.Count);

        var rolling = usage.Windows.Single(w => w.Id == "rolling");
        Assert.Equal(UsageWindowKind.FiveHour, rolling.Kind);
        Assert.Equal(0.125, rolling.UsedFraction, 3); // percent 12.5 -> 0.125 used
        Assert.Equal(5 * 3600, rolling.WindowSeconds);
        Assert.False(rolling.IsExhausted);

        Assert.Equal(UsageWindowKind.Weekly, usage.Windows.Single(w => w.Id == "weekly").Kind);
        Assert.Equal(UsageWindowKind.Monthly, usage.Windows.Single(w => w.Id == "monthly").Kind);

        // Shortest first.
        Assert.Equal(5 * 3600, usage.Windows[0].WindowSeconds);
    }

    [Fact]
    public void Status_Not_Ok_Marks_Exhausted()
    {
        const string json = """
            {
              "usage": {
                "rolling": { "status": "exhausted", "percent": 100, "resetsAt": "2026-09-21T16:40:00.000Z" }
              }
            }
            """;
        var usage = Parse(json);
        Assert.True(usage.Windows[0].IsExhausted);
    }

    [Fact]
    public void Missing_Window_Is_Omitted()
    {
        const string json = """
            {
              "usage": {
                "rolling": { "status": "ok", "percent": 1.0 }
              }
            }
            """;
        var usage = Parse(json);
        Assert.Single(usage.Windows);
        Assert.Equal("rolling", usage.Windows[0].Id);
        Assert.Null(usage.Windows[0].ResetsAt);
    }

    [Fact]
    public void Missing_Usage_Object_Is_Schema_Change()
    {
        const string json = """{ "something": "else" }""";
        var provider = new OpenCodeGoProvider(_ => "test-key");
        using var document = JsonDocument.Parse(json);
        var account = new MonitoredAccount { Provider = ProviderId.OpenCodeGo, AccountId = "openCodeGo" };
        var method = typeof(OpenCodeGoProvider).GetMethod("ParseSuccess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.ThrowsAny<Exception>(() =>
            method.Invoke(provider, new object[] { document, account, Now }));
    }
}

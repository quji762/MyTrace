using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Providers.Kiro;
using Xunit;

namespace Pulse.ProviderContract.Tests;

/// <summary>
/// Contract tests for the Kiro provider parser, using upstream fixtures from
/// Tests/PulseTests/Fixtures/kiro-pro-plus-usage.json (Apache-2.0, qunqin24/Pulse).
/// </summary>
public class KiroContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static ProviderReadResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var account = new MonitoredAccount { Provider = ProviderId.Kiro, AccountId = "Kiro" };
        return KiroProvider.ParseReply(document.RootElement.Clone(), account, Now);
    }

    [Fact]
    public void Fixture_Parses_All_Bounded_Credit_Pools()
    {
        var json = LoadFixture("kiro-pro-plus-usage.json");
        var result = Parse(json);

        Assert.Equal(ProviderReadHealth.Healthy, result.Health);
        var usage = result.Usage!;
        Assert.Equal(UsageState.Live, usage.State);
        Assert.Equal(ProviderId.Kiro, usage.Provider);
        Assert.Equal(UsageRoute.KiroAcp, usage.Origin);
        Assert.Equal("KIRO PRO+", usage.Plan);

        Assert.Equal(2, usage.Windows.Count);
        Assert.Equal(["credit", "bonus_credit"], usage.Windows.Select(w => w.Id));
        Assert.Equal(["Credits", "Bonus credits"], usage.Windows.Select(w => w.Scope));
        Assert.Equal(0.061725, usage.Windows[0].UsedFraction, 6);
        Assert.Equal(0.25, usage.Windows[1].UsedFraction, 6);
        Assert.All(usage.Windows, w =>
        {
            Assert.Equal(UsageWindowKind.Monthly, w.Kind);
            Assert.False(w.ReportsLength); // date-only reset: no fixed duration
            Assert.NotNull(w.ResetsAt);
        });
    }

    [Fact]
    public void Malformed_Limits_Are_Skipped_Not_Invented()
    {
        const string json = """
            {
              "success": true,
              "data": {
                "planName": "test",
                "billingCycleReset": "not-a-date",
                "usageBreakdowns": [
                  { "resourceType": "ZERO", "used": 1, "limit": 0, "hasLimit": true },
                  { "resourceType": "UNBOUNDED", "used": 1, "limit": 10, "hasLimit": false },
                  { "resourceType": "PERCENT", "displayName": "Percent only", "limit": 80, "percentage": 12.5, "hasLimit": true }
                ]
              }
            }
            """;
        var result = Parse(json);

        Assert.Equal(ProviderReadHealth.Healthy, result.Health);
        var windows = result.Usage!.Windows;
        Assert.Single(windows);
        Assert.Equal(0.125, windows[0].UsedFraction, 6);
        Assert.Null(windows[0].ResetsAt);
    }

    [Fact]
    public void Credit_Pool_Ids_Survive_Array_Reorder()
    {
        var json = LoadFixture("kiro-pro-plus-usage.json");
        using var document = JsonDocument.Parse(json);
        var payload = KiroUsageParser.Decode(document.RootElement);
        Assert.NotNull(payload.Data);

        // Reverse the breakdowns and confirm each scope keeps its id.
        var original = payload.Data!.UsageBreakdowns;
        var reversed = new KiroUsageParser.Payload(
            payload.Data.PlanName,
            payload.Data.BillingCycleReset,
            Enumerable.Reverse(original).ToList());

        var originalMap = KiroUsageParser.Windows(payload.Data)
            .ToDictionary(w => w.Scope!, w => w.Id);
        var reorderedMap = KiroUsageParser.Windows(reversed)
            .ToDictionary(w => w.Scope!, w => w.Id);

        Assert.Equal(originalMap, reorderedMap);
    }

    [Fact]
    public void Exhausted_From_Used_Gte_Limit()
    {
        const string json = """
            {
              "success": true,
              "data": {
                "planName": "test",
                "usageBreakdowns": [
                  { "resourceType": "CREDIT", "used": 200, "limit": 200, "hasLimit": true }
                ]
              }
            }
            """;
        var result = Parse(json);
        Assert.True(result.Usage!.Windows[0].IsExhausted);
    }

    [Fact]
    public void Unsuccessful_Envelope_Reports_Sign_In()
    {
        const string json = """{ "success": false, "message": "Please sign in" }""";
        var result = Parse(json);
        Assert.Equal(ProviderReadHealth.Unauthorized, result.Health);
        Assert.Contains("Sign in", result.Detail);
    }

    [Fact]
    public void Unsuccessful_Envelope_Reports_Version()
    {
        const string json = """{ "success": false, "message": "Method not found" }""";
        var result = Parse(json);
        Assert.Equal(ProviderReadHealth.UnsupportedPlatform, result.Health);
    }

    [Fact]
    public void Empty_Pools_Reports_No_Limits()
    {
        const string json = """
            { "success": true, "data": { "planName": "x", "usageBreakdowns": [] } }
            """;
        var result = Parse(json);
        Assert.Equal(ProviderReadHealth.SchemaChanged, result.Health);
        Assert.Equal("no limits reported", result.Detail);
    }

    [Fact]
    public void Acp_Failure_Classification()
    {
        var (h1, _) = KiroUsageParser.ClassifyFailure(new KiroAcpClient.AcpException(KiroAcpClient.FailureKind.ExecutableNotFound));
        Assert.Equal(ProviderReadHealth.ProviderUnavailable, h1);

        var (h2, _) = KiroUsageParser.ClassifyFailure(new KiroAcpClient.AcpException(KiroAcpClient.FailureKind.Server, "Method not found"));
        Assert.Equal(ProviderReadHealth.UnsupportedPlatform, h2);

        var (h3, _) = KiroUsageParser.ClassifyFailure(new KiroAcpClient.AcpException(KiroAcpClient.FailureKind.Server, "Please sign in"));
        Assert.Equal(ProviderReadHealth.Unauthorized, h3);

        var (h4, _) = KiroUsageParser.ClassifyFailure(new KiroAcpClient.AcpException(KiroAcpClient.FailureKind.TimedOut));
        Assert.Equal(ProviderReadHealth.ProviderUnavailable, h4);
    }

    private static string LoadFixture(string fileName)
    {
        var root = FindRepoRoot();
        return File.ReadAllText(Path.Combine(root, "Tests", "PulseTests", "Fixtures", fileName));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Tests", "PulseTests")))
            dir = dir.Parent!;
        return dir!.FullName;
    }
}

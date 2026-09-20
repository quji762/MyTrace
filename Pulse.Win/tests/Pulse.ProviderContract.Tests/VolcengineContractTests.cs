using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Volcengine;
using Xunit;

namespace Pulse.ProviderContract.Tests;

/// <summary>
/// Contract tests for the Volcengine parser, driven against the upstream fixtures
/// volcengine-coding-plan.json / volcengine-agent-plan.json (the signed API's two
/// shapes). The response shapes are second-hand upstream; the parsing is what the
/// fixtures lock.
/// </summary>
public class VolcengineContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Tests", "PulseTests")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    private static List<UsageWindow> CodingWindows(string fixtureName)
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "PulseTests", "Fixtures", fixtureName));
        using var document = JsonDocument.Parse(json);
        var quotaUsage = document.RootElement.GetProperty("Result").GetProperty("QuotaUsage");
        var provider = new VolcengineProvider(_ => "id:secret");
        var method = typeof(VolcengineProvider).GetMethod("CodingSuccess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var usage = (ProviderUsage)method.Invoke(provider, new object[] { quotaUsage, Account, Now })!;
        return usage.Windows.ToList();
    }

    private static readonly MonitoredAccount Account = new() { Provider = ProviderId.Volcengine, AccountId = "volcengine" };

    [Fact]
    public void Coding_Plan_Fixture_Parses_With_Scope_And_Sort()
    {
        var windows = CodingWindows("volcengine-coding-plan.json");

        // The unknown "fortnightly" label is LEFT OUT rather than guessed at.
        Assert.Equal(2, windows.Count);

        var weekly = windows.Single(w => w.Kind == UsageWindowKind.Weekly);
        Assert.Equal("Coding Plan", weekly.Scope);
        Assert.Equal(0.415, weekly.UsedFraction, 3);
        Assert.NotNull(weekly.ResetsAt);

        var fiveHour = windows.Single(w => w.Kind == UsageWindowKind.FiveHour);
        Assert.Equal(0.12, fiveHour.UsedFraction, 3);

        // Shortest first.
        Assert.True(windows[0].WindowSeconds <= windows[1].WindowSeconds);
    }

    [Fact]
    public void Agent_Plan_Fixture_Uses_Quota_And_Used()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "PulseTests", "Fixtures", "volcengine-agent-plan.json"));
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("Result");

        var provider = new VolcengineProvider(_ => "id:secret");
        var method = typeof(VolcengineProvider).GetMethod("AgentSuccess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var usage = (ProviderUsage)method.Invoke(provider, new object[] { result, Account, Now })!;

        var fiveHour = usage.Windows.Single(w => w.Kind == UsageWindowKind.FiveHour);
        Assert.Equal("Agent Plan", fiveHour.Scope);
        Assert.Equal(0.25, fiveHour.UsedFraction, 3); // used/quota (fixture: 250/1000)
    }

    [Fact]
    public void Zero_Quota_Window_Is_Dropped()
    {
        const string json = """{ "Result": { "AFPFiveHour": { "Quota": 0, "Used": 0 } } }""";
        using var document = JsonDocument.Parse(json);
        var provider = new VolcengineProvider(_ => "id:secret");
        var method = typeof(VolcengineProvider).GetMethod("AgentSuccess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var usage = (ProviderUsage)method.Invoke(provider, new object[] { document.RootElement.GetProperty("Result"), Account, Now })!;
        Assert.Empty(usage.Windows);
    }

    [Fact]
    public void Unknown_Label_Is_Left_Out()
    {
        Assert.Null(VolcengineMapping.Window("id", "fortnightly", 50, "Coding Plan", null));
    }

    [Fact]
    public void Monthly_Is_A_Sort_Key_Not_A_Length()
    {
        var window = VolcengineMapping.Window("id", "monthly", 50, "Coding Plan", null);
        Assert.NotNull(window);
        Assert.False(window!.ReportsLength);
        Assert.Equal(30 * 86400, window.WindowSeconds);
    }

    [Fact]
    public void Full_Window_Marks_Exhausted()
    {
        var window = VolcengineMapping.Window("id", "5h", 100, "Coding Plan", null);
        Assert.True(window!.IsExhausted);
    }

    [Fact]
    public void Epoch_Detection_At_1e11()
    {
        var seconds = VolcengineMapping.FromEpoch(1789085506);
        Assert.Equal(1789085506, seconds!.Value.ToUnixTimeSeconds()); // seconds in
        var millis = VolcengineMapping.FromEpoch(1789085506000);
        Assert.Equal(1789085506, millis!.Value.ToUnixTimeSeconds()); // ms in
        Assert.Null(VolcengineMapping.FromEpoch(0));
    }

    [Fact]
    public void Credential_Pair_Splits_On_First_Colon()
    {
        var pair = VolcengineCredentials.FromEntered("AKID:sec:ret");
        Assert.Equal("AKID", pair!.AccessKeyID);
        Assert.Equal("sec:ret", pair.SecretAccessKey); // a secret containing a colon survives
        Assert.Null(VolcengineCredentials.FromEntered("no-colon"));
        Assert.Null(VolcengineCredentials.FromEntered(":secret"));
    }
}

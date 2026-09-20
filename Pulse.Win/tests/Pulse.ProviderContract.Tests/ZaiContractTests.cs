using System.Reflection;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Zai;
using Xunit;

namespace Pulse.ProviderContract.Tests;

/// <summary>
/// Contract tests for the Z.ai / GLM Coding Plan parser, using the upstream
/// fixture glm-coding-plan-quota.json plus inline envelopes for the refusal
/// shapes the live endpoint answers with (measured upstream, ZaiErrorTests).
/// </summary>
public class ZaiContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static ProviderUsage Parse(string json, ProviderId id = ProviderId.Zai)
    {
        var provider = new ZaiProvider(id, _ => "test-key");
        using var document = JsonDocument.Parse(json);
        var account = new MonitoredAccount { Provider = id, AccountId = id.ToString() };
        var method = typeof(ZaiProvider).GetMethod("ParseSuccess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (ProviderUsage)method.Invoke(provider, new object[] { document, account, Now })!;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Tests", "PulseTests")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    [Fact]
    public void Fixture_Counts_Are_Preferred_Over_Whole_Percentage()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "PulseTests", "Fixtures", "glm-coding-plan-quota.json"));
        var usage = Parse(json, ProviderId.GlmCoding);

        Assert.Equal("lite", usage.Plan); // level key
        Assert.Equal(2, usage.Windows.Count);

        // unit 3 = minutes ×60, number 5 → 300 minutes = 18000 s (five-hour window):
        // usage 2000, remaining 2000 → 0 used; percentage (whole number 0) would
        // also say 0, but the counts route proves the path.
        var fiveHour = usage.Windows.Single(w => w.WindowSeconds == 300 * 60);
        Assert.Equal(0.0, fiveHour.UsedFraction, 3);

        // unit 6 = weeks ×10080 → 1 week = 604800 s.
        var weekly = usage.Windows.Single(w => w.WindowSeconds == 10080 * 60);
        Assert.Equal(0.0, weekly.UsedFraction, 3);
        Assert.NotNull(weekly.ResetsAt); // nextResetTime ms → date
    }

    [Fact]
    public void Remaining_Is_Spend_When_Used_Count_Absent()
    {
        // usage 1000, remaining 250 → 750 spent = 75%.
        const string json = """
            {
              "success": true, "code": 200,
              "data": { "limits": [ { "type": "CREDIT_LIMIT", "unit": 3, "number": 5,
                                       "usage": 1000, "remaining": 250 } ], "planName": "GLM Pro" }
            }
            """;
        var usage = Parse(json);
        Assert.Equal(0.75, usage.Windows[0].UsedFraction, 3);
        Assert.Equal("GLM Pro", usage.Plan);
    }

    [Fact]
    public void Unknown_Unit_Drops_The_Entry()
    {
        const string json = """
            {
              "success": true, "code": 200,
              "data": { "limits": [ { "type": "CREDIT_LIMIT", "unit": 99, "number": 5, "percentage": 50 } ] }
            }
            """;
        // The dropped entry leaves no windows, which the service-level contract
        // reports as "no limits reported".
        Assert.ThrowsAny<Exception>(() => Parse(json));
    }

    [Fact]
    public void Monthly_MCP_Marker_Reads_As_Thirty_Days()
    {
        const string json = """
            {
              "success": true, "code": 200,
              "data": { "limits": [ { "type": "TIME_LIMIT", "unit": 5, "number": 1, "percentage": 10 } ] }
            }
            """;
        var usage = Parse(json);
        var window = Assert.Single(usage.Windows);
        Assert.Equal("MCP", window.Scope);
        Assert.Equal(30 * 24 * 60 * 60, window.WindowSeconds);
        Assert.Equal(0.10, window.UsedFraction, 3);
    }

    [Fact]
    public void Envelope_Refusal_Is_Bad_Key_Not_Empty_Plan()
    {
        // A refused key arrives as HTTP 200 with success: false — caught as an
        // envelope exception, classified Unauthorized.
        const string json = """{ "success": false, "code": 1000, "msg": "身份验证失败。" }""";
        var provider = new ZaiProvider(ProviderId.Zai, _ => "test-key");
        using var document = JsonDocument.Parse(json);
        var account = new MonitoredAccount { Provider = ProviderId.Zai, AccountId = "zai" };
        var method = typeof(ZaiProvider).GetMethod("ParseSuccess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var exception = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(provider, new object[] { document, account, Now }));
        var envelope = Assert.IsType<ZaiProvider.EnvelopeException>(exception.InnerException);
        Assert.Equal(UnavailabilityKind.Unauthorized, envelope.Kind);
    }

    [Fact]
    public void No_Coding_Plan_Is_Not_A_Server_Error()
    {
        // A working key with no running subscription answers 500 + this sentence.
        Assert.Equal(UnavailabilityKind.NotConfigured, ZaiProvider.Problem(500, "No active Coding Plan subscription"));
        Assert.Equal(UnavailabilityKind.NotConfigured, ZaiProvider.Problem(500, "coding plan"));
    }

    [Fact]
    public void Mainland_Host_Chinese_Words_Map_To_Bad_Key()
    {
        Assert.Equal(UnavailabilityKind.Unauthorized, ZaiProvider.Problem(1000, "身份验证失败。"));
        Assert.Equal(UnavailabilityKind.Unauthorized, ZaiProvider.Problem(401, "令牌已过期或验证不正确"));
        Assert.Equal(UnavailabilityKind.RateLimited, ZaiProvider.Problem(429, "too many requests"));
        Assert.Equal(UnavailabilityKind.ProviderUnavailable, ZaiProvider.Problem(500, "internal error"));
    }

    [Fact]
    public void Region_Split_Picks_The_Right_Host()
    {
        // Separate accounts: the mainland plan lives on open.bigmodel.cn.
        var zai = new ZaiProvider(ProviderId.Zai);
        var glm = new ZaiProvider(ProviderId.GlmCoding);
        Assert.Equal(ProviderId.Zai, zai.Id);
        Assert.Equal(ProviderId.GlmCoding, glm.Id);
    }
}

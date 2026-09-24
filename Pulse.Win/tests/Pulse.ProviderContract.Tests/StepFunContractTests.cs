using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.StepFun;
using Xunit;

namespace Pulse.ProviderContract.Tests;

/// <summary>
/// Contract tests for the StepFun provider parser, using upstream fixtures
/// (Apache-2.0, qunqin24/Pulse) measured on real console replies.
/// </summary>
public class StepFunContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static ProviderReadResult Parse(string json)
    {
        var provider = new StepFunProvider(_ => "Oasis-Token=abc");
        using var document = JsonDocument.Parse(json);
        var account = new MonitoredAccount { Provider = ProviderId.StepFun, AccountId = "StepFun" };
        var method = typeof(StepFunProvider).GetMethod("ParseReply",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (ProviderReadResult)method.Invoke(provider, new object[] { document, account, Now })!;
    }

    [Fact]
    public void TokenPlan_Parses_One_Credits_Ring_With_Pack_Expiry()
    {
        var result = Parse(LoadFixture("Fixtures/stepfun-token-plan.json"));

        Assert.Equal(ProviderReadHealth.Healthy, result.Health);
        var usage = result.Usage!;
        Assert.Equal(UsageState.Live, usage.State);

        // One ring for the month's pool and any packs: spent from one balance.
        var window = Assert.Single(usage.Windows);
        Assert.Equal("stepfun.credits", window.Id);
        Assert.Equal(UsageWindowKind.Monthly, window.Kind);
        Assert.Equal((1600000000.0 - 1599913834.0) / 1600000000.0, window.UsedFraction, 8);
        Assert.False(window.ReportsLength); // thirty days is a sort key only
        Assert.Null(window.ResetsAt);       // next_reset_at "0" is no date
        Assert.False(window.IsExhausted);

        // The pack lapses 2026-10-05T08:00:56Z, still ahead of Now.
        var expiry = window.Expiry;
        Assert.NotNull(expiry);
        Assert.Equal(1599913834.0, expiry!.Amount, 1);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791187256), expiry.At);
    }

    [Fact]
    public void TopUp_Packs_Sum_Into_One_Ring_With_Soonest_Expiry()
    {
        var result = Parse(LoadFixture("Fixtures/stepfun-token-plan-topup.json"));

        var usage = result.Usage!;
        var window = Assert.Single(usage.Windows);
        Assert.Equal(0.45, window.UsedFraction, 8); // (800M - 440M) / 800M

        // A refill the reply states and that is still ahead.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790582456), window.ResetsAt);

        // The soonest-lapsing pack: the fuller one lapses two weeks later and
        // is never added to it.
        var expiry = window.Expiry;
        Assert.NotNull(expiry);
        Assert.Equal(40000000.0, expiry!.Amount, 1);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791187256), expiry.At);
    }

    [Fact]
    public void CodingPlan_Parses_Two_Stated_Windows()
    {
        var result = Parse(LoadFixture("Fixtures/stepfun-coding-plan.json"));

        var usage = result.Usage!;
        Assert.Equal(2, usage.Windows.Count);

        var fiveHour = usage.Windows[0];
        Assert.Equal("stepfun.5h", fiveHour.Id);
        Assert.Equal(UsageWindowKind.FiveHour, fiveHour.Kind);
        Assert.Equal(0.25, fiveHour.UsedFraction, 8); // 1 - 0.75 left
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790250000), fiveHour.ResetsAt);
        Assert.True(fiveHour.ReportsLength); // the plan states the window's length

        var weekly = usage.Windows[1];
        Assert.Equal("stepfun.weekly", weekly.Id);
        Assert.Equal(UsageWindowKind.Weekly, weekly.Kind);
        Assert.Equal(0.0, weekly.UsedFraction, 8); // 1 left is a full window
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790640000), weekly.ResetsAt);
        Assert.False(weekly.IsExhausted);
    }

    [Fact]
    public void Zeroed_Windows_With_No_Credits_Are_A_NoPlan_Answer_Not_A_Fault()
    {
        const string json = """
            { "status":1, "five_hour_usage_left_rate":0, "five_hour_usage_reset_time":"0",
              "weekly_usage_left_rate":0, "weekly_usage_reset_time":"0",
              "plan_credit_rate_limit":{ "subscription_credit_left_rate":0, "topup_credit_left_rate":0 } }
            """;
        var result = Parse(json);

        Assert.Equal(ProviderReadHealth.Healthy, result.Health);
        var usage = result.Usage!;
        Assert.Empty(usage.Windows);
        Assert.Equal(UsageState.Unavailable, usage.State);
        Assert.NotNull(usage.Unavailability);
        Assert.Equal(UnavailabilityKind.NoCredits, usage.Unavailability!.Kind);
    }

    [Fact]
    public void Refusal_Inside_A_200_Words_An_Auth_Failure_As_Unauthorized()
    {
        const string json = """
            { "status":0, "code":"unauthenticated", "message":"auth failed: bad token" }
            """;
        var result = Parse(json);

        Assert.Equal(ProviderReadHealth.Unauthorized, result.Health);
        Assert.Null(result.Usage);
    }

    [Fact]
    public void Refusal_Without_Auth_Words_Is_A_Schema_Change()
    {
        const string json = """{ "status":0, "desc":"something this cannot use" }""";
        var result = Parse(json);

        Assert.Equal(ProviderReadHealth.SchemaChanged, result.Health);
    }

    [Fact]
    public void Plan_Name_Comes_From_The_Status_Reply()
    {
        var method = typeof(StepFunProvider).GetMethod("PlanName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var name = (string?)method.Invoke(null, new object[] { LoadFixture("Fixtures/stepfun-plan-status.json") });
        Assert.Equal("Plus", name);
    }

    [Fact]
    public void Cookie_Allow_List_Keeps_Only_The_Published_Names()
    {
        var header = SessionCookie.Normalize(
            "Oasis-Token=abc; Oasis-Webid=dev1; INGRESSCOOKIE=lb1; other=dropped");
        Assert.Equal("Oasis-Token=abc; Oasis-Webid=dev1; INGRESSCOOKIE=lb1", header);

        // A paste with the console's own prefix is tolerated.
        Assert.Equal("Oasis-Token=abc", SessionCookie.Normalize("Cookie: Oasis-Token=abc"));

        // The session itself is required; a header without it is not a session.
        Assert.Throws<SessionCookie.CookieException>(() => SessionCookie.Normalize("INGRESSCOOKIE=lb1"));

        // A header built from an arbitrary string is a header injection if a
        // value carries a newline.
        Assert.Throws<SessionCookie.CookieException>(() => SessionCookie.Normalize("Oasis-Token=abc\nX: y"));
    }

    [Fact]
    public void Web_Id_Comes_From_The_Cookie_Or_The_Token_Claim()
    {
        Assert.Equal("dev1", SessionCookie.WebId("Oasis-Token=abc; Oasis-Webid=dev1"));

        // The refresh half of an access...refresh JWT pair carries the claim.
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { device_id = "dev9" }))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"eyJhbGciOiJIUzI1NiJ9.{payload}.sig";
        Assert.Equal("dev9", SessionCookie.WebId($"Oasis-Token={token}...{token}"));
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

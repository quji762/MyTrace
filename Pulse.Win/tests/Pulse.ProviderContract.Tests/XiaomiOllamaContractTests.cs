using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Ollama;
using Pulse.Providers.Xiaomi;
using Xunit;

namespace Pulse.ProviderContract.Tests;

/// <summary>
/// Contract tests for the Xiaomi MiMo console parser, driven against the upstream
/// fixtures (xiaomi-plan-usage/detail/balance/no-plan/signed-out), plus the cookie
/// normalizer's allow-list and injection guards.
/// </summary>
public class XiaomiMiMoContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Tests", "PulseTests")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    private static JsonElement Load(string name)
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "PulseTests", "Fixtures", name));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void Plan_Fixtures_Produce_The_Monthly_Ring()
    {
        var usage = Load("xiaomi-plan-usage.json");
        var detail = Load("xiaomi-plan-detail.json");

        var plan = XiaomiMapping.ParsePlan(detail, usage);
        Assert.NotNull(plan);
        Assert.Equal(3750000, plan!.Value.Used);
        Assert.Equal(10000000, plan.Value.Limit);
        Assert.Equal("coding-pro", plan.Value.Code);
        // The console's own UTC format, not ISO-8601.
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), plan.Value.PeriodEnd);
        Assert.Equal(0.375, (double)plan.Value.Used / plan.Value.Limit, 3);
    }

    [Fact]
    public void No_Plan_Is_Nil_Not_Zero()
    {
        var usage = Load("xiaomi-no-plan.json");
        var plan = XiaomiMapping.ParsePlan(null, usage);
        Assert.Null(plan); // a ring at 0% would say "a full month left"
    }

    [Fact]
    public void Expired_Plan_Is_Not_Drawn()
    {
        var usage = Load("xiaomi-plan-usage.json");
        var detail = JsonDocument.Parse(
            """{ "code": 0, "data": { "planCode": "coding-pro", "expired": true } }""").RootElement.Clone();
        Assert.Null(XiaomiMapping.ParsePlan(detail, usage));
    }

    [Fact]
    public void Balance_Fixture_Parses_With_Its_Own_Currency()
    {
        var balance = Load("xiaomi-balance.json");
        var parsed = XiaomiMapping.ParseBalance(balance);
        Assert.NotNull(parsed);
        Assert.Equal(42.75, parsed!.Value.Amount, 2);
        Assert.Equal("CNY", parsed.Value.Currency);
    }

    [Fact]
    public void Signed_Out_Envelope_Is_Rejected()
    {
        var envelope = Load("xiaomi-signed-out.json");
        // code != 0 → the plan payload is refused rather than parsed.
        Assert.Null(XiaomiMapping.ParsePlan(null, envelope));
        Assert.Null(XiaomiMapping.ParseBalance(envelope));
    }

    [Fact]
    public void Cookie_Normalizer_Keeps_Only_The_Named_Cookies()
    {
        var header = XiaomiMiMoProvider.NormalizeCookie(
            "api-platform_serviceToken=abc; userId=12345; analytics_id=z; api-platform_ph=ph1");
        Assert.Contains("api-platform_serviceToken=abc", header);
        Assert.Contains("userId=12345", header);
        Assert.Contains("api-platform_ph=ph1", header);
        Assert.DoesNotContain("analytics_id", header);
    }

    [Fact]
    public void Cookie_Normalizer_Requires_Both_Required_Names()
    {
        Assert.Throws<OllamaPageException>(() =>
            XiaomiMiMoProvider.NormalizeCookie("api-platform_serviceToken=abc"));
    }

    [Fact]
    public void Cookie_Normalizer_Rejects_Injection_And_Empty()
    {
        // A value carrying a newline is header injection.
        Assert.ThrowsAny<Exception>(() =>
            XiaomiMiMoProvider.NormalizeCookie("api-platform_serviceToken=abc\nX-Evil: 1; userId=u"));
        Assert.ThrowsAny<Exception>(() => XiaomiMiMoProvider.NormalizeCookie(""));
        // Cookie: prefix tolerated.
        var header = XiaomiMiMoProvider.NormalizeCookie("Cookie: api-platform_serviceToken=abc; userId=12345");
        Assert.Equal("api-platform_serviceToken=abc; userId=12345", header);
    }
}

/// <summary>
/// Contract tests for the Ollama Cloud session cookie normalizer (the page parser
/// is HTML and exercised through integration; the cookie boundary is the security
/// surface and locked here).
/// </summary>
public class OllamaCookieContractTests
{
    [Fact]
    public void Keeps_Only_Session_Cookies()
    {
        var header = OllamaSessionCookie.Normalize(
            "wos-session=abc; _ga=GA1.2.1; __Secure-next-auth.session-token=def");
        Assert.Contains("wos-session=abc", header);
        Assert.Contains("__Secure-next-auth.session-token=def", header);
        Assert.DoesNotContain("_ga", header);
    }

    [Fact]
    public void Accepts_Number_Suffixed_Host_Variants()
    {
        var header = OllamaSessionCookie.Normalize("wos-session.123456=xyz");
        Assert.Equal("wos-session.123456=xyz", header);
    }

    [Fact]
    public void Cookie_Prefix_Tolerated_Bare_Key_Rejected()
    {
        Assert.Contains("wos-session=abc", OllamaSessionCookie.Normalize("Cookie: wos-session=abc"));
        // A bare API key (not a cookie shape) is not a session.
        Assert.Throws<OllamaPageException>(() => OllamaSessionCookie.Normalize("sk-ollama-123"));
    }

    [Fact]
    public void Newline_Is_Injection()
    {
        Assert.ThrowsAny<Exception>(() => OllamaSessionCookie.Normalize("wos-session=abc\nX-Evil: 1"));
    }
}

using Pulse.Core;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>--json contract: cache-only, stable tokens, display percent rule.</summary>
public class JsonReportTests
{
    [Fact]
    public void DisplayPercent_Never_Reads_Zero_When_Used_Nor_Full_When_Not()
    {
        Assert.Equal(0, JsonReport.DisplayPercent(0));
        Assert.Equal(1, JsonReport.DisplayPercent(0.004));
        Assert.Equal(50, JsonReport.DisplayPercent(0.5));
        Assert.Equal(99, JsonReport.DisplayPercent(0.996));
        Assert.Equal(100, JsonReport.DisplayPercent(1));
    }

    [Fact]
    public void Emit_Reads_A_Banked_Cache_File_With_Stable_Tokens()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pulse-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var usage = new ProviderUsage(
                ProviderId.ClaudeCode,
                "ClaudeCode",
                [
                    new UsageWindow("claudeCode.five_hour", UsageWindowKind.FiveHour, null, 0.42, 5 * 3600,
                        DateTimeOffset.UtcNow.AddHours(2), IsExhausted: false),
                ],
                DateTimeOffset.UtcNow.AddMinutes(-3),
                UsageState.Stale,
                "Pro",
                null,
                null,
                UsageRoute.Endpoint);
            DurableUsageCache.Save(Path.Combine(directory, "ClaudeCode-ClaudeCode.json"), usage);

            var json = JsonReport.Emit(directory, DateTimeOffset.UtcNow);
            Assert.Contains("\"provider\":\"ClaudeCode\"", json);
            Assert.Contains("\"kind\":\"fiveHour\"", json);
            Assert.Contains("\"source\":\"endpoint\"", json);
            Assert.Contains("pulse://account/ClaudeCode", json);
            Assert.Contains("\"usedPercent\":42", json);
            Assert.Contains("\"generatedAt\"", json);
            Assert.Contains("\"ageSeconds\"", json);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Emit_On_An_Empty_Cache_Prints_An_Empty_Rail()
    {
        var json = JsonReport.Emit(Path.Combine(Path.GetTempPath(), $"pulse-empty-{Guid.NewGuid():N}"));
        Assert.Contains("\"accounts\":[]", json);
    }
}

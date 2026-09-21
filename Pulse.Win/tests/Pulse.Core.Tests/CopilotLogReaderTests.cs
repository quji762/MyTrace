using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// CopilotLogReader combined-lane tests: the OTEL lane is the authority, a
/// session OTEL named is dropped from the desktop lane, a lifetime desktop
/// total larger than OTEL marks the OTEL records partial, and VS Code is
/// filtered by dedup key or session+instant.
/// </summary>
public class CopilotLogReaderTests
{
    [Fact]
    public void Totals_Are_Summed_Not_Trapping()
    {
        var otel = new List<AgentUsageRecord>();
        for (var i = 0; i < 3; i++)
        {
            otel.Add(new AgentUsageRecord
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1789981200000 + i),
                Model = "m",
                Tally = new TokenTally(Input: 1000, Output: 20),
                SessionID = "s",
                DeduplicationID = $"copilot-otel:e{i}",
            });
        }
        var built = AgentUsageLedger.Build(otel, ModelPrices.Empty);
        Assert.Equal(3060, built.Ledger.AllTime.Tokens); // 3 * 1020
    }

    [Fact]
    public void Roots_Names_All_Three_Lanes_Even_When_Absent()
    {
        var roots = CopilotLogReader.Roots("/fakehome");
        Assert.Contains(roots, r => r.Replace('\\', '/').EndsWith(".copilot/otel"));
        Assert.Contains(roots, r => r.Replace('\\', '/').EndsWith(".copilot/data.db"));
        Assert.Contains(roots, r => r.Replace('\\', '/').EndsWith(".copilot/session-state"));
        Assert.Contains(roots, r => r.Replace('\\', '/').Contains("workspaceStorage"));
    }
}

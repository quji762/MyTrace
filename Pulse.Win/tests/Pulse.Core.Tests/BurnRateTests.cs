using Pulse.Core.Forecast;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Xunit;

namespace Pulse.Core.Tests;

public class BurnRateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static UsageWindow Window(
        double used, DateTimeOffset? reset, bool reportsLength = true, int seconds = 5 * 3600) =>
        new("w", UsageWindowKind.FiveHour, null, used, seconds, reset, reportsLength);

    [Fact]
    public void Estimate_Needs_Reset_And_ReportsLength()
    {
        var reset = Now + TimeSpan.FromMinutes(30);
        Assert.Null(BurnRate.For(Window(0.5, null), Now));                    // no reset
        Assert.Null(BurnRate.For(Window(0.5, reset, reportsLength: false), Now)); // sort-key length
    }

    [Fact]
    public void Estimate_Only_Within_Two_Hours_Of_Reset()
    {
        var far = Now + TimeSpan.FromHours(4);
        var near = Now + TimeSpan.FromMinutes(45);
        Assert.Null(BurnRate.For(Window(0.5, far), Now));  // far-out projection is noise
        Assert.NotNull(BurnRate.For(Window(0.5, near), Now));
    }

    [Fact]
    public void Estimate_Computes_Elapsed_And_Remaining()
    {
        // 5-hour window, 30 minutes left → 4.5h elapsed of 5h = 90%.
        var reset = Now + TimeSpan.FromMinutes(30);
        var estimate = BurnRate.For(Window(0.5, reset), Now);
        Assert.NotNull(estimate);
        Assert.Equal(0.9, estimate!.ElapsedFraction, 3);
        Assert.Equal(TimeSpan.FromMinutes(30), estimate.TimeRemaining);
    }

    [Fact]
    public void TimeToExhausted_Projects_From_The_Burn()
    {
        // 95% used at 90% elapsed → burn 1.055 > 1.0: exhausts inside the window.
        var reset = Now + TimeSpan.FromMinutes(30);
        var estimate = BurnRate.For(Window(0.95, reset), Now)!;
        var eta = estimate.TimeToExhausted;
        Assert.NotNull(eta);
        Assert.True(eta!.Value > TimeSpan.Zero);
        Assert.True(eta.Value < estimate.TimeRemaining);
    }

    [Fact]
    public void Slow_Burn_Gives_No_Exhaustion()
    {
        var reset = Now + TimeSpan.FromMinutes(30);
        var estimate = BurnRate.For(Window(0.1, reset), Now)!;
        // 10% used at 90% elapsed: burn 0.111, slower than the window — no wall.
        Assert.Null(estimate.TimeToExhausted);
        // Exactly at the window's pace (burn 1.0) is also not a wall.
        Assert.Null(BurnRate.For(Window(0.5, reset), Now)!.TimeToExhausted);
    }

    [Fact]
    public void Already_Exhausted_Is_Zero()
    {
        var reset = Now + TimeSpan.FromMinutes(30);
        var estimate = BurnRate.For(Window(1.0, reset), Now)!;
        Assert.Equal(TimeSpan.Zero, estimate.TimeToExhausted);
    }
}

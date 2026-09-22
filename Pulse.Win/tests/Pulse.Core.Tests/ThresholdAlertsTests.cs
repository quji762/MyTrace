using Pulse.Core.Notifications;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Xunit;

namespace Pulse.Core.Tests;

public class ThresholdAlertsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static UsageWindow Window(double used, bool exhausted = false, DateTimeOffset? reset = null) =>
        new("w", UsageWindowKind.FiveHour, null, used, 5 * 3600, reset ?? Now.AddHours(2), IsExhausted: exhausted);

    [Fact]
    public void Crossing_Threshold_Fires_Once()
    {
        var alerts = new ThresholdAlerts();
        var fired = new List<double>();
        alerts.ThresholdCrossed += (_, t) => fired.Add(t);

        alerts.Observe("a", Window(0.5), Now);   // below: nothing
        alerts.Observe("a", Window(0.85), Now);  // crosses 0.80: fire once
        alerts.Observe("a", Window(0.9), Now);   // still above: silent
        alerts.Observe("a", Window(0.97), Now);  // would cross 0.95: silent (disarmed)

        Assert.Equal([0.8], fired);
    }

    [Fact]
    public void Re_Arms_When_Reset_Moves()
    {
        var alerts = new ThresholdAlerts();
        var fired = new List<double>();
        alerts.ThresholdCrossed += (_, t) => fired.Add(t);

        alerts.Observe("a", Window(0.85, reset: Now.AddHours(2)), Now);
        alerts.Observe("a", Window(0.9, reset: Now.AddHours(2)), Now);   // silent
        alerts.Observe("a", Window(0.95, reset: Now.AddHours(7)), Now);  // new window: fire

        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void Exhausted_Flag_Fires_Regardless_Of_Percentage()
    {
        var alerts = new ThresholdAlerts();
        var fired = new List<double>();
        alerts.ThresholdCrossed += (_, t) => fired.Add(t);

        alerts.Observe("a", Window(0.0, exhausted: true), Now);
        Assert.Equal([1.0], fired);
    }

    [Fact]
    public void Usage_Dropping_Back_Re_Arms()
    {
        var alerts = new ThresholdAlerts();
        var fired = new List<double>();
        alerts.ThresholdCrossed += (_, t) => fired.Add(t);

        alerts.Observe("a", Window(0.85), Now);  // fire 0.8
        alerts.Observe("a", Window(0.3), Now);   // fell back: re-arm
        alerts.Observe("a", Window(0.86), Now);  // fire again

        Assert.Equal([0.8, 0.8], fired);
    }

    [Fact]
    public void Separate_Windows_Do_Not_Rearm_Each_Other()
    {
        // A 5-hour window and a 7-day window share an account but not a reset.
        // One key for both makes every refresh look like a new window and fires again.
        var alerts = new ThresholdAlerts();
        var fired = new List<double>();
        alerts.ThresholdCrossed += (_, t) => fired.Add(t);

        var fiveHour = Window(0.9, reset: Now.AddHours(2)) with { Id = "five" };
        var sevenDay = Window(0.2, reset: Now.AddDays(4)) with { Id = "seven" };
        var fiveKey = ThresholdAlerts.KeyFor(ProviderId.ClaudeCode, "ClaudeCode", fiveHour.Id);
        var sevenKey = ThresholdAlerts.KeyFor(ProviderId.ClaudeCode, "ClaudeCode", sevenDay.Id);
        Assert.NotEqual(fiveKey, sevenKey);

        alerts.Observe(fiveKey, fiveHour, Now);
        alerts.Observe(sevenKey, sevenDay, Now);
        alerts.Observe(fiveKey, fiveHour, Now);

        Assert.Equal([0.8], fired);
    }

    [Fact]
    public void Off_Does_Not_Throw_Or_Fire()
    {
        var alerts = new ThresholdAlerts([]);
        var fired = false;
        alerts.ThresholdCrossed += (_, _) => fired = true;
        alerts.Observe("k", Window(1, exhausted: true), Now);
        Assert.False(fired);
    }
}

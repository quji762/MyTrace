using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Refresh;
using Xunit;

namespace Pulse.Core.Tests;

public class AdaptiveRefreshTests
{
    [Fact]
    public void Interval_Stays_Within_Floor_And_Ceiling()
    {
        var signals = new AdaptiveRefresh.Signals();
        var interval = AdaptiveRefresh.NextInterval(AdaptiveRefresh.Floor, signals);
        Assert.InRange(interval, AdaptiveRefresh.Floor, AdaptiveRefresh.Ceiling);
    }

    [Fact]
    public void Locally_Unobservable_Providers_Cap_At_Five_Minutes()
    {
        // DeepSeek/Command Code: local signals are blind to prepaid balances.
        var signals = new AdaptiveRefresh.Signals { LocallyUnobservable = true };
        var interval = AdaptiveRefresh.NextInterval(TimeSpan.FromMinutes(30), signals);
        Assert.True(interval <= AdaptiveRefresh.UnwatchedCeiling);
    }

    [Fact]
    public void Hover_Rule_Shortens_Interval()
    {
        var hovered = new AdaptiveRefresh.Signals { RecentlyHovered = true };
        var interval = AdaptiveRefresh.NextInterval(TimeSpan.FromMinutes(20), hovered);
        Assert.Equal(AdaptiveRefresh.Floor, interval);
    }

    [Fact]
    public void Power_Constraint_Lengthens_Interval()
    {
        var constrained = new AdaptiveRefresh.Signals { PowerConstrained = true };
        var interval = AdaptiveRefresh.NextInterval(AdaptiveRefresh.Floor, constrained);
        Assert.True(interval >= TimeSpan.FromMinutes(15));
    }
}

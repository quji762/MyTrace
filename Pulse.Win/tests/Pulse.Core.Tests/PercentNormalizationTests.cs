using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Xunit;

namespace Pulse.Core.Tests;

public class PercentNormalizationTests
{
    [Fact]
    public void FromPercent_Converts_0_100_To_0_1()
    {
        Assert.Equal(0.0, PercentNormalization.FromPercent(0), 3);
        Assert.Equal(0.5, PercentNormalization.FromPercent(50), 3);
        Assert.Equal(1.0, PercentNormalization.FromPercent(100), 3);
    }

    [Fact]
    public void Nan_And_Infinity_Become_Zero()
    {
        Assert.Equal(0.0, PercentNormalization.Normalize(double.NaN), 3);
        Assert.Equal(0.0, PercentNormalization.Normalize(double.PositiveInfinity), 3);
    }

    [Fact]
    public void Values_Clamp_Outside_0_1()
    {
        Assert.Equal(0.0, PercentNormalization.Normalize(-2.0), 3);
        Assert.Equal(1.0, PercentNormalization.Normalize(5.0), 3);
    }

    [Fact]
    public void Tiny_Nonzero_Stays_Visible()
    {
        // 0.02% must not round to exactly 0 (invisible residual rule).
        var v = PercentNormalization.Normalize(0.0002);
        Assert.True(v > 0.0);
    }

    [Fact]
    public void Near_Full_Stays_Below_One_Unless_Truy_Full()
    {
        var v = PercentNormalization.Normalize(0.99999);
        Assert.True(v < 1.0);
        Assert.Equal(1.0, PercentNormalization.Normalize(1.0), 3);
    }

    [Fact]
    public void InvertRemaining_Is_Single_Boundary_Operation()
    {
        // Copilot/Kimi style: 75% remaining -> 25% used.
        Assert.Equal(0.25, PercentNormalization.InvertRemaining(0.75), 3);
        // Inverting twice would be a bug; document the one-way semantics.
        Assert.Equal(0.75, PercentNormalization.InvertRemaining(PercentNormalization.InvertRemaining(0.75)), 3);
    }
}

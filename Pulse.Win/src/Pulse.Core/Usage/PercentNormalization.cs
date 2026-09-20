namespace Pulse.Core.Usage;

/// <summary>
/// Percentage normalization ported from upstream percentValue() semantics:
/// NaN becomes 0; clamps to 0...1; rounds but keeps non-zero above 0 and
/// non-full below 1 so tiny residuals and 99.x% remain visible.
/// </summary>
public static class PercentNormalization
{
    public static double Normalize(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;

        var v = Math.Clamp(value, 0.0, 1.0);
        var rounded = Math.Round(v, 4);
        if (rounded <= 0.0 && v > 0.0) rounded = 0.0001;
        if (rounded >= 1.0 && v < 1.0) rounded = 0.9999;
        return rounded;
    }

    /// <summary>Convert a provider-reported percent (0...100) to a used fraction.</summary>
    public static double FromPercent(double percent) => Normalize(percent / 100.0);

    /// <summary>
    /// Invert a provider-reported "remaining" fraction at the service boundary, once.
    /// Downstream everything is "used". Callers must not invert twice.
    /// </summary>
    public static double InvertRemaining(double remainingFraction) => Normalize(1.0 - remainingFraction);
}

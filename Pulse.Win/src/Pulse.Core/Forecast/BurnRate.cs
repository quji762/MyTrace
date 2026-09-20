using System.Text;

using Pulse.Core.Providers;

namespace Pulse.Core.Forecast;

/// <summary>
/// Burn-rate forecast; port of upstream BurnRate semantics.
/// Single-reading rate = spent share / elapsed share of the window. Requires a
/// REPORTED percentage, a reset time, and a true window length (reportsLength):
/// the estimate is only ever offered while the window still has time left and
/// the remaining time is under two hours (upstream rule — a far-out projection
/// is noise, not information).
/// </summary>
public static class BurnRate
{
    public sealed record Estimate(
        double UsedFraction,
        double ElapsedFraction,
        TimeSpan TimeRemaining,
        double WindowSeconds)
    {
        /// <summary>Projected exhaustion instant, or null when the burn is slower
        /// than the window. Zero when already exhausted.</summary>
        public TimeSpan? TimeToExhausted
        {
            get
            {
                var burn = UsedFraction / ElapsedFraction;
                if (burn <= 0) return null;
                if (UsedFraction >= 1) return TimeSpan.Zero;

                // The rate is a share-of-window per share-of-window: at this burn
                // the remaining share empties in remainingFraction / burn of the
                // window's own length. A burn slower than 1.0 never exhausts
                // inside the window, so there is no wall to project.
                var remainingFraction = 1 - UsedFraction;
                if (burn <= 1.0) return null;
                var secondsToExhaustion = remainingFraction / burn * WindowSeconds;
                return secondsToExhaustion > 0 ? TimeSpan.FromSeconds(secondsToExhaustion) : null;
            }
        }
    }

    /// <summary>
    /// The estimate for one window, or null when the data cannot honestly answer.
    /// The window start is inferred from its own length (reset - length); a window
    /// whose length is a sort key is refused by ReportsLength upstream.
    /// </summary>
    public static Estimate? For(UsageWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is not { } reset || !window.ReportsLength)
            return null;
        if (window.WindowSeconds <= 0)
            return null;

        var windowStart = reset - TimeSpan.FromSeconds(window.WindowSeconds);
        if (now <= windowStart) return null;

        var elapsedFraction = (now - windowStart).TotalSeconds / window.WindowSeconds;
        if (elapsedFraction is <= 0 or >= 1) return null;

        // Only within two hours of the reset (upstream ETA rule): a far-out
        // projection from one reading is noise, not information.
        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromHours(2))
            return null;

        return new Estimate(window.UsedFraction, elapsedFraction, remaining, window.WindowSeconds);
    }
}

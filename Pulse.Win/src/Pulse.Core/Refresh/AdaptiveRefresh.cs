namespace Pulse.Core.Refresh;

/// <summary>
/// Adaptive refresh interval ported from upstream AdaptiveRefresh:
/// floor 120s / ceiling 1800s (2-30 minutes). Providers whose usage cannot be
/// observed locally (prepaid balances: DeepSeek, Command Code) are capped at a
/// 300s ceiling because local signals (transcript mtime, panel visibility) are
/// blind to them.
/// </summary>
public static class AdaptiveRefresh
{
    public static readonly TimeSpan Floor = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(1800);
    public static readonly TimeSpan UnwatchedCeiling = TimeSpan.FromSeconds(300);

    /// <summary>Signals that make the scheduler wait longer before the next refresh.</summary>
    public sealed record Signals
    {
        /// <summary>Last activity time of local CLI transcripts (Claude/Codex), if any.</summary>
        public DateTimeOffset? LastLocalActivity { get; init; }

        /// <summary>The rail/panel is currently visible to the user.</summary>
        public bool PanelVisible { get; init; }

        /// <summary>The user recently hovered the rail, wanting fresh numbers.</summary>
        public bool RecentlyHovered { get; init; }

        /// <summary>System is on battery or thermally constrained.</summary>
        public bool PowerConstrained { get; init; }

        /// <summary>Provider cannot be observed locally (prepaid balance style).</summary>
        public bool LocallyUnobservable { get; init; }
    }

    /// <summary>
    /// Compute the next interval from the previous one and current signals.
    /// A visible idle panel waits the ceiling (five minutes when the provider
    /// cannot be seen locally). Fresh transcript writes and a recent hover
    /// come back to the floor. A hidden panel or a constrained machine only
    /// lengthens the wait. The result stays inside [Floor, effective ceiling].
    /// </summary>
    public static TimeSpan NextInterval(TimeSpan previous, Signals signals)
    {
        var ceiling = signals.LocallyUnobservable ? UnwatchedCeiling : Ceiling;
        var interval = previous;

        if (signals.LastLocalActivity is { } lastActivity)
        {
            var sinceActivity = DateTimeOffset.UtcNow - lastActivity;
            if (sinceActivity < TimeSpan.Zero) sinceActivity = TimeSpan.Zero;
            if (sinceActivity < TimeSpan.FromMinutes(10))
                interval = Floor;
            else if (sinceActivity < TimeSpan.FromMinutes(30))
                interval = TimeSpan.FromMinutes(5);
            else
                interval = TimeSpan.FromMinutes(15);
        }
        else if (signals.PanelVisible)
        {
            // No local write to watch. Waiting the cap is the idle cadence;
            // balance providers stop at five minutes instead of half an hour.
            interval = ceiling;
        }

        if (signals.PowerConstrained)
        {
            // Battery: never faster than 15m, even under a 5m unwatched cap.
            ceiling = Max(ceiling, TimeSpan.FromMinutes(15));
            interval = Max(interval, TimeSpan.FromMinutes(15));
        }

        if (!signals.PanelVisible)
            interval = Max(interval, TimeSpan.FromMinutes(10));

        // Hover speeds things up only on AC — never below the battery floor.
        if (signals.RecentlyHovered && !signals.PowerConstrained)
            interval = Min(interval, Floor);

        return Clamp(interval, Floor, ceiling);
    }

    public static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        Max(Min(value, max), min);

    public static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;
    public static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
}

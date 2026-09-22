using Pulse.Core.Providers;

namespace Pulse.Core.Notifications;

/// <summary>
/// Threshold notifications, ported from upstream UsageAlerts semantics:
/// - Thresholds fire on crossing, not on every refresh at 100% (hysteresis via
///   armed/disarmed state — re-armed only when the window resets or usage drops
///   back below).
/// - A window marked exhausted by the provider fires regardless of percentage.
/// - Notifications are never the only channel: the rail itself carries the state.
/// - A reset time that has moved back re-arms (a new window began).
/// </summary>
public sealed class ThresholdAlerts
{
    private readonly double[] _thresholds;
    private readonly object _lock = new();
    private readonly Dictionary<string, AlertState> _states = new();

    public sealed record AlertState(bool Armed, DateTimeOffset? LastResetSeen);

    public event Action<string, double>? ThresholdCrossed;

    public ThresholdAlerts(double[]? thresholds = null)
    {
        // Upstream defaults: an early heads-up and a near-the-wall warning.
        _thresholds = thresholds ?? [0.8, 0.95];
    }

    /// <summary>
    /// One alert arm per window. Two windows on the same account (5-hour and 7-day)
    /// have different reset times; sharing a key makes each refresh look like a new window.
    /// </summary>
    public static string KeyFor(ProviderId provider, string accountId, string windowId) =>
        $"{provider}:{accountId}:{windowId}";

    /// <summary>Feed one window reading. Key must identify window + account.</summary>
    public void Observe(string key, UsageWindow window, DateTimeOffset? now = null)
    {
        // Off is an empty list. Indexing it would throw on the first reading.
        if (_thresholds.Length == 0) return;

        var at = now ?? DateTimeOffset.Now;
        List<double> crossed = [];

        lock (_lock)
        {
            var state = _states.GetValueOrDefault(key, new AlertState(Armed: true, null));

            // A reset that moved back (or a new reset stamp) re-arms the alert:
            // a new window began, whatever the last one did.
            var resetMoved = window.ResetsAt is { } reset &&
                             state.LastResetSeen is { } seen &&
                             reset != seen;

            // Upstream reset-turnover rule, simplified: a moved reset re-arms.
            var rearm = resetMoved;

            if (rearm) state = state with { Armed = true };
            // Keep a known reset when a later sample omits it — clearing the
            // timestamp would make the next reset look like the first.
            if (window.ResetsAt is { } nextReset)
                state = state with { LastResetSeen = nextReset };

            // Provider-flagged exhaustion fires even when percentage disagrees.
            if (window.IsExhausted && state.Armed)
            {
                crossed.Add(1.0);
                state = state with { Armed = false };
            }
            else if (!window.IsExhausted && state.Armed)
            {
                foreach (var threshold in _thresholds)
                {
                    if (window.UsedFraction >= threshold)
                    {
                        crossed.Add(threshold);
                        state = state with { Armed = false };
                        break; // one notification per crossing event
                    }
                }
            }

            // Usage fell back below the lowest threshold: re-arm for next time.
            if (window.UsedFraction < _thresholds[0] && !window.IsExhausted)
                state = state with { Armed = true };

            _states[key] = state;
        }

        foreach (var threshold in crossed)
            ThresholdCrossed?.Invoke(key, threshold);
    }

    public void Forget(string key)
    {
        lock (_lock) _states.Remove(key);
    }
}

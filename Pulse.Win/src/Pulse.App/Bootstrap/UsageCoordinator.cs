using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Refresh;

namespace Pulse.App.Bootstrap;

/// <summary>
/// Composition root for the usage pipeline: wires every registered provider to a
/// monitored account, drives the refresh engine on a low-frequency timer, and
/// forwards readings to the UI thread. One scheduler, one timer — never a timer
/// per provider (resource budget constraint).
/// </summary>
public sealed class UsageCoordinator : IAsyncDisposable
{
    public sealed record Reading(MonitoredAccount Account, ProviderReadResult Result);

    private readonly MonitoredAccount[] _accounts;
    private readonly RefreshEngine _engine;
    private readonly System.Threading.Timer? _timer;

    public UsageCoordinator(
        IReadOnlyDictionary<ProviderId, IUsageProvider> adapters,
        IEnumerable<MonitoredAccount> accounts,
        Action<Reading> onReading,
        ICredentialStore? store = null)
    {
        _accounts = accounts.Where(a => adapters.ContainsKey(a.Provider)).ToArray();

        _engine = new RefreshEngine(
            account => adapters.GetValueOrDefault(account.Provider),
            _ => new AdaptiveRefresh.Signals { PanelVisible = true },
            new DebugLogger());

        _engine.ReadingChanged += (account, result) => onReading(new Reading(account, result));

        // One low-frequency scheduler tick; the engine decides who is actually due.
        _timer = new System.Threading.Timer(
            _ => _ = _engine.RunDueAsync(_accounts, CancellationToken.None),
            null,
            dueTime: TimeSpan.Zero,
            period: TimeSpan.FromSeconds(30));
    }

    public void Start()
    {
        foreach (var account in _accounts)
            _engine.Schedule(account);
    }

    public ValueTask DisposeAsync() => _engine.DisposeAsync();

    private sealed class DebugLogger : ILogger
    {
        public void Log(string message) => System.Diagnostics.Debug.WriteLine($"[pulse] {message}");
    }
}

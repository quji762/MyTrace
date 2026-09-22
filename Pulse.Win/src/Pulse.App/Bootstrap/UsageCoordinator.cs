using System.IO;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Refresh;

namespace Pulse.App.Bootstrap;

/// <summary>
/// Composition root for the usage pipeline: wires every registered provider to
/// the accounts that exist right now, drives the refresh engine on one timer,
/// and forwards readings to the UI thread. Added accounts show up on the next
/// tick because the account list is read again each pass.
/// </summary>
public sealed class UsageCoordinator : IAsyncDisposable
{
    public sealed record Reading(MonitoredAccount Account, ProviderReadResult Result);

    private readonly Func<IReadOnlyList<MonitoredAccount>> _accounts;
    private readonly RefreshEngine _engine;
    private readonly System.Threading.Timer _timer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<string> _scheduled = new();
    private int _ticking;
    private int _again;
    private int _disposed;

    public UsageCoordinator(
        IReadOnlyDictionary<ProviderId, IUsageProvider> adapters,
        Func<IReadOnlyList<MonitoredAccount>> accounts,
        Action<Reading> onReading,
        Func<MonitoredAccount, AdaptiveRefresh.Signals>? signals = null)
    {
        _accounts = () => accounts()
            .Where(account => account.Enabled && adapters.ContainsKey(account.Provider))
            .ToArray();

        _engine = new RefreshEngine(
            account => adapters.GetValueOrDefault(account.Provider),
            signals ?? (_ => new AdaptiveRefresh.Signals { PanelVisible = true }),
            new DebugLogger(),
            cacheDirectory: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PulseWin", "usage-cache"));

        _engine.ReadingChanged += (account, result) => onReading(new Reading(account, result));

        // One low-frequency scheduler tick; the engine decides who is actually due.
        _timer = new System.Threading.Timer(_ => Tick(), null, dueTime: TimeSpan.Zero, period: TimeSpan.FromSeconds(30));
    }

    public void Start() => Tick();

    /// <summary>The rail was hovered. Due accounts are read on this tick, not the next 30s boundary.</summary>
    public void RequestHover()
    {
        foreach (var account in _accounts())
            _engine.RequestRefresh(account, RefreshReason.Hover);
        Interlocked.Exchange(ref _again, 1);
        Tick();
    }

    private void Tick()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        _ = RunTickAsync();
    }

    private async Task RunTickAsync()
    {
        try
        {
            var accounts = _accounts();
            lock (_scheduled)
            {
                foreach (var account in accounts)
                {
                    if (_scheduled.Add(Key(account)))
                        _engine.Schedule(account);
                }
            }

            await _engine.RunDueAsync(accounts, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception)
        {
            _ = 0;
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
            if (Interlocked.Exchange(ref _again, 0) == 1)
                Tick();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _timer.Dispose();
        _shutdown.Cancel();
        await _engine.DisposeAsync().ConfigureAwait(false);
        // CTS stays undisposed on purpose: an in-flight tick may still hold Token.
    }

    private static string Key(MonitoredAccount account) => $"{account.Provider}:{account.AccountId}";

    private sealed class DebugLogger : ILogger
    {
        public void Log(string message) => _ = 0;
    }
}

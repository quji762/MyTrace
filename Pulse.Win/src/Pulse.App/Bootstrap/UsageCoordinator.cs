using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Refresh;

namespace Pulse.App.Bootstrap;

/// <summary>
/// Composition root for the usage pipeline: wires accounts to providers, drives the
/// refresh engine on a low-frequency timer, and forwards readings to the UI thread.
/// One scheduler, one timer — never a timer per provider (resource budget constraint).
/// </summary>
public sealed class UsageCoordinator : IAsyncDisposable
{
    public sealed record Reading(MonitoredAccount Account, ProviderReadResult Result);

    private readonly MonitoredAccount?[] _accounts;
    private readonly RefreshEngine _engine;
    private readonly System.Threading.Timer? _timer;

    public UsageCoordinator(
        MonitoredAccount?[] accounts,
        Func<string?, IUsageProvider?[]> providerSetFactory,
        Action<Reading> onReading,
        string? sharedKey = null)
    {
        _accounts = accounts;
        var adapters = providerSetFactory(sharedKey ?? string.Empty);
        var providerByIndex = new System.Collections.Generic.Dictionary<ProviderId, IUsageProvider>();
        for (var i = 0; i < accounts.Length; i++)
        {
            if (accounts[i] is { } account && i < adapters.Length && adapters[i] is { } adapter)
                providerByIndex[account.Provider] = adapter;
        }

        _engine = new RefreshEngine(
            account => providerByIndex.GetValueOrDefault(account.Provider),
            _ => new AdaptiveRefresh.Signals { PanelVisible = true },
            new ConsoleLogger());

        _engine.ReadingChanged += (account, result) => onReading(new Reading(account, result));

        // One low-frequency scheduler tick; the engine decides who is actually due.
        _timer = new System.Threading.Timer(
            _ => _ = _engine.RunDueAsync(_accounts.Where(a => a is not null).Select(a => a!).ToList(), CancellationToken.None),
            null,
            dueTime: TimeSpan.Zero,
            period: TimeSpan.FromSeconds(30));
    }

    public void Start()
    {
        foreach (var account in _accounts)
            if (account is { } a) _engine.Schedule(a);
    }

    public ValueTask DisposeAsync() => _engine.DisposeAsync();

    private sealed class ConsoleLogger : ILogger
    {
        public void Log(string message) => System.Diagnostics.Debug.WriteLine($"[pulse] {message}");
    }
}

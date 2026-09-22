using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;

namespace Pulse.Core.Refresh;

/// <summary>Why a refresh was requested; used for diagnostics and merging.</summary>
public enum RefreshReason
{
    Scheduled,
    Manual,
    Hover,
    Startup,
    AccountChanged,
}

/// <summary>
/// Central refresh engine: one scheduler for all providers, per-account due times,
/// in-flight request merging, bounded concurrency, and failure isolation — one
/// provider's failure must never stall the whole pass (upstream refresh semantics).
/// </summary>
public sealed class RefreshEngine : IAsyncDisposable
{
    private readonly Func<MonitoredAccount, IUsageProvider?> _providerResolver;
    private readonly Func<MonitoredAccount, AdaptiveRefresh.Signals> _signalsResolver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _dueTimes = new();
    private readonly Dictionary<string, TimeSpan> _intervals = new();
    private readonly Dictionary<string, Task<ProviderReadResult>> _inFlight = new();
    private readonly UsageCache _cache = new();
    private readonly string? _cacheDirectory;
    private readonly SemaphoreSlim _workerGate;

    public RefreshEngine(
        Func<MonitoredAccount, IUsageProvider?> providerResolver,
        Func<MonitoredAccount, AdaptiveRefresh.Signals> signalsResolver,
        ILogger logger,
        TimeProvider? timeProvider = null,
        int maxConcurrentReads = 3,
        string? cacheDirectory = null)
    {
        _providerResolver = providerResolver;
        _signalsResolver = signalsResolver;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheDirectory = cacheDirectory;
        _workerGate = new SemaphoreSlim(maxConcurrentReads, maxConcurrentReads);
        LoadDurable();
    }

    /// <summary>Raised after an account's reading changed (fresh read, failure state, or cache restore).</summary>
    public event Action<MonitoredAccount, ProviderReadResult>? ReadingChanged;

    public void Schedule(MonitoredAccount account, TimeSpan? initialDelay = null)
    {
        var due = _timeProvider.GetUtcNow() + (initialDelay ?? TimeSpan.Zero);
        lock (_lock) _dueTimes[Key(account)] = due;
    }

    /// <summary>Request a refresh as soon as workers allow. Repeated requests for the same
    /// account while a refresh is in flight are merged into it.</summary>
    public void RequestRefresh(MonitoredAccount account, RefreshReason reason)
    {
        lock (_lock) _dueTimes[Key(account)] = _timeProvider.GetUtcNow();
        _logger.Log($"refresh requested provider={account.Provider} reason={reason}");
    }

    /// <summary>
    /// Run one scheduler pass: refresh every account whose due time has arrived.
    /// Returns the number of accounts actually read (merged duplicates and
    /// adapter-less accounts are not counted). One provider's failure never
    /// affects the others in the pass.
    /// </summary>
    public async Task<int> RunDueAsync(IReadOnlyList<MonitoredAccount> accounts, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        List<MonitoredAccount> due = new();

        lock (_lock)
        {
            foreach (var account in accounts)
            {
                if (!account.Enabled) continue;
                if (_dueTimes.TryGetValue(Key(account), out var dueTime) && dueTime <= now)
                    due.Add(account);
            }
        }

        if (due.Count == 0) return 0;

        var tasks = new Task<bool>[due.Count];
        for (var i = 0; i < due.Count; i++)
            tasks[i] = ReadWithIsolationAsync(due[i], cancellationToken);

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Count(started => started);
    }

    /// <summary>
    /// One account read with full isolation: exceptions are caught and logged, never
    /// allowed to break other providers in the pass. Returns true if a read was started.
    /// </summary>
    private async Task<bool> ReadWithIsolationAsync(MonitoredAccount account, CancellationToken cancellationToken)
    {
        Task<ProviderReadResult> readTask;
        lock (_lock)
        {
            var key = Key(account);
            if (_inFlight.TryGetValue(key, out var existing))
            {
                // Merge: an identical refresh is already running. Keep the due
                // time so a later pass still sees this account as pending.
                return false;
            }
            _dueTimes.Remove(key);

            var provider = _providerResolver(account);
            if (provider is null)
            {
                // No adapter: still reschedule so a later pass can pick it up.
                Schedule(account);
                return false;
            }

            readTask = ReadAsync(provider, account, cancellationToken);
            _inFlight[key] = readTask;
        }

        try
        {
            await readTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                var key = Key(account);
                if (ReferenceEquals(_inFlight.GetValueOrDefault(key), readTask))
                    _inFlight.Remove(key);
            }
            // A failed persist must not leave the account unscheduled. If
            // ReadAsync already set an adaptive due time, leave it alone.
            lock (_lock)
            {
                if (!_dueTimes.ContainsKey(Key(account)))
                {
                    try { Schedule(account); } catch (Exception) { }
                }
            }
        }
        return true;
    }

    private async Task<ProviderReadResult> ReadAsync(IUsageProvider provider, MonitoredAccount account, CancellationToken cancellationToken)
    {
        var result = new ProviderReadResult
        {
            Health = ProviderReadHealth.ProviderUnavailable,
            Detail = "not run",
        };

        try
        {
            await _workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var context = new ProviderReadContext { Now = _timeProvider.GetUtcNow(), Services = EmptyServices.Instance };
                result = await provider.ReadAsync(account, context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _workerGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw; // shutdown path: let cancellation propagate
        }
        catch (Exception ex)
        {
            // Failure isolation: log, downgrade, never crash the pass.
            _logger.Log($"provider={account.Provider} read failed: {ex.GetType().Name}");
            result = ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, ex.GetType().Name);
        }

        var now = _timeProvider.GetUtcNow();
        if (result.Health == ProviderReadHealth.Healthy && result.Usage is { } live)
        {
            _cache.Store(live, now);
            try { Persist(live); } catch (Exception) { /* disk errors must not kill the pass */ }
        }
        else if (_cache.Get(account.Provider, account.AccountId, now) is { } cached)
            result = result with { Usage = cached };

        // Schedule the next pass using the adaptive interval. The previous
        // decision is the input, so a quiet panel can lengthen and a hover
        // can shorten it on the following pass.
        var signals = _signalsResolver(account);
        TimeSpan previous;
        lock (_lock)
            previous = _intervals.GetValueOrDefault(Key(account), AdaptiveRefresh.Floor);
        var interval = AdaptiveRefresh.NextInterval(previous, signals);
        lock (_lock)
            _intervals[Key(account)] = interval;
        lock (_lock)
        {
            // Keep a due time a hover/refresh set while this read was running.
            if (!_dueTimes.ContainsKey(Key(account)))
                lock (_lock)
        {
            if (!_dueTimes.ContainsKey(Key(account)))
                Schedule(account, interval);
        }
        }

        try
        {
            ReadingChanged?.Invoke(account, result);
        }
        catch (Exception ex)
        {
            _logger.Log($"ReadingChanged handler failed: {ex.GetType().Name}");
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        Task<ProviderReadResult>[] pending;
        lock (_lock) pending = _inFlight.Values.ToArray();

        // Unified cancellation happens via the caller's token; here just observe
        // completion so shutdown does not race the in-flight reads.
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { /* shutdown: results no longer matter */ }

        // _workerGate is left undisposed: a late ReadAsync may still WaitAsync.
    }

    private void LoadDurable()
    {
        if (_cacheDirectory is null || !Directory.Exists(_cacheDirectory)) return;
        var now = _timeProvider.GetUtcNow();
        foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.json"))
        {
            var usage = DurableUsageCache.Load(file, now);
            if (usage is not null)
                _cache.Store(usage, now);
        }
    }

    private void Persist(ProviderUsage usage)
    {
        if (_cacheDirectory is null) return;
        Directory.CreateDirectory(_cacheDirectory);
        var name = $"{usage.Provider}-{usage.AccountId}.json";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        DurableUsageCache.Save(Path.Combine(_cacheDirectory, name), usage);
    }

    private static string Key(MonitoredAccount account) =>
        new AccountKey(account.Provider, account.AccountId).CacheKey;

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>Minimal redaction-friendly logger abstraction (see Pulse.Diagnostics).</summary>
public interface ILogger
{
    void Log(string message);
}

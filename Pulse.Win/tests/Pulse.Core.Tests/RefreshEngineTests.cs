using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Refresh;
using Pulse.Core.Usage;
using Xunit;

namespace Pulse.Core.Tests;

public class RefreshEngineTests
{
    private sealed class FakeLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public void Log(string message) => Messages.Add(message);
    }

    private sealed class ThrowingProvider : IUsageProvider
    {
        public ProviderId Id => ProviderId.DeepSeek;
        public ProviderCapabilities Capabilities => ProviderCapabilities.For(Id);
        public Task<ProviderReadResult> ReadAsync(MonitoredAccount account, ProviderReadContext context, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    private sealed class OkProvider : IUsageProvider
    {
        public ProviderId Id => ProviderId.KimiCode;
        public ProviderCapabilities Capabilities => ProviderCapabilities.For(Id);
        public Task<ProviderReadResult> ReadAsync(MonitoredAccount account, ProviderReadContext context, CancellationToken ct)
            => Task.FromResult(ProviderReadResult.Ok(new ProviderUsage(
                Id, account.AccountId, Array.Empty<UsageWindow>(), context.Now, UsageState.Live,
                null, null, null, UsageRoute.Endpoint)));
    }

    private static MonitoredAccount Account(ProviderId id, string name) =>
        new() { Provider = id, AccountId = name };

    [Fact]
    public async Task Provider_Exception_Is_Isolated_And_Engine_Survives()
    {
        var logger = new FakeLogger();
        var engine = new RefreshEngine(
            account => account.Provider == ProviderId.DeepSeek ? new ThrowingProvider() : new OkProvider(),
            _ => new AdaptiveRefresh.Signals(),
            logger);

        var results = new List<(MonitoredAccount, ProviderReadResult)>();
        engine.ReadingChanged += (a, r) => results.Add((a, r));

        engine.Schedule(Account(ProviderId.DeepSeek, "deepSeek"));
        engine.Schedule(Account(ProviderId.KimiCode, "kimiCode"));

        var read = await engine.RunDueAsync(
            new[] { Account(ProviderId.DeepSeek, "deepSeek"), Account(ProviderId.KimiCode, "kimiCode") },
            CancellationToken.None);

        Assert.Equal(2, read);
        Assert.Equal(2, results.Count);

        // The throwing provider reports failure; the other still succeeds.
        var deepSeek = results.Single(r => r.Item1.Provider == ProviderId.DeepSeek).Item2;
        var kimi = results.Single(r => r.Item1.Provider == ProviderId.KimiCode).Item2;
        Assert.Equal(ProviderReadHealth.ProviderUnavailable, deepSeek.Health);
        Assert.Equal(ProviderReadHealth.Healthy, kimi.Health);
    }

    [Fact]
    public async Task Disabled_Accounts_Are_Skipped()
    {
        var engine = new RefreshEngine(_ => new OkProvider(), _ => new AdaptiveRefresh.Signals(), new FakeLogger());
        var disabled = Account(ProviderId.DeepSeek, "deepSeek") with { Enabled = false };
        engine.Schedule(disabled);

        var read = await engine.RunDueAsync(new[] { disabled }, CancellationToken.None);

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Missing_Adapter_Is_Skipped_Silently()
    {
        // Feature-flagged-off providers have no adapter registered.
        var engine = new RefreshEngine(_ => null, _ => new AdaptiveRefresh.Signals(), new FakeLogger());
        var account = Account(ProviderId.Codex, "codex");
        engine.Schedule(account);

        var read = await engine.RunDueAsync(new[] { account }, CancellationToken.None);

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task RequestRefresh_Merges_InFlight_Duplicate()
    {
        var logger = new FakeLogger();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var engine = new RefreshEngine(
            _ => new BlockingProvider(started, release),
            _ => new AdaptiveRefresh.Signals(),
            logger);
        var account = Account(ProviderId.KimiCode, "kimiCode");
        engine.Schedule(account);

        var accounts = new[] { account };
        var first = engine.RunDueAsync(accounts, CancellationToken.None);
        // Wait until the first pass has actually started its read, then request again.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        engine.RequestRefresh(account, RefreshReason.Manual);
        var second = engine.RunDueAsync(accounts, CancellationToken.None);
        var secondReads = await second;

        // The duplicate merges into the in-flight read: nothing new was started.
        Assert.Equal(0, secondReads);
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task Failure_After_Success_Keeps_The_Last_Good_Reading()
    {
        var provider = new OnceThenFailProvider();
        var engine = new RefreshEngine(
            _ => provider,
            _ => new AdaptiveRefresh.Signals { PanelVisible = true },
            new FakeLogger());
        var results = new List<ProviderReadResult>();
        engine.ReadingChanged += (_, result) => results.Add(result);

        var account = Account(ProviderId.KimiCode, "kimiCode");
        var accounts = new[] { account };
        engine.Schedule(account);
        await engine.RunDueAsync(accounts, CancellationToken.None);

        engine.Schedule(account);
        await engine.RunDueAsync(accounts, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(ProviderReadHealth.Healthy, results[0].Health);
        Assert.Equal(UsageState.Live, results[0].Usage!.State);
        Assert.Equal(ProviderReadHealth.ProviderUnavailable, results[1].Health);
        var restored = results[1].Usage;
        Assert.NotNull(restored);
        Assert.Equal(UsageState.Stale, restored.State);
        Assert.Equal(0.4, restored.Windows[0].UsedFraction);
    }

    private sealed class OnceThenFailProvider : IUsageProvider
    {
        private bool _failed;
        public ProviderId Id => ProviderId.KimiCode;
        public ProviderCapabilities Capabilities => ProviderCapabilities.For(Id);

        public Task<ProviderReadResult> ReadAsync(MonitoredAccount account, ProviderReadContext context, CancellationToken ct)
        {
            if (_failed)
                return Task.FromResult(ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "down"));
            _failed = true;
            var window = new UsageWindow(
                "kimi", UsageWindowKind.Weekly, null, 0.4, 7 * 86400, context.Now.AddDays(3));
            return Task.FromResult(ProviderReadResult.Ok(new ProviderUsage(
                Id, account.AccountId, new[] { window }, context.Now, UsageState.Live,
                null, null, null, UsageRoute.Endpoint)));
        }
    }

    private sealed class BlockingProvider : IUsageProvider
    {
        private readonly TaskCompletionSource _startedSignal;
        private readonly TaskCompletionSource _release;
        public BlockingProvider(TaskCompletionSource startedSignal, TaskCompletionSource release) { _startedSignal = startedSignal; _release = release; }
        public ProviderId Id => ProviderId.KimiCode;
        public ProviderCapabilities Capabilities => ProviderCapabilities.For(Id);

        public async Task<ProviderReadResult> ReadAsync(MonitoredAccount account, ProviderReadContext context, CancellationToken ct)
        {
            _startedSignal.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return ProviderReadResult.Ok(new ProviderUsage(
                Id, account.AccountId, Array.Empty<UsageWindow>(), context.Now, UsageState.Live,
                null, null, null, UsageRoute.Endpoint));
        }
    }
}

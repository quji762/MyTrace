using System.Windows;
using Pulse.App.Bootstrap;
using Pulse.App.Tray;
using Pulse.App.Settings;
using Pulse.App.Spend;
using Pulse.Core.Accounts;
using Pulse.Core.Notifications;
using Pulse.Core.Platform;
using Pulse.Core.Providers;
using Pulse.Storage;
using Pulse.Providers;

namespace Pulse.App;

/// <summary>
/// Application entry point. ShutdownMode is OnExplicitShutdown because the rail is a
/// borderless always-available surface: closing windows must not kill the process —
/// exit goes through the tray menu. A second launch hands off to the running one
/// via the single-instance mutex and exits.
/// </summary>
public partial class App : System.Windows.Application
{
    private NotifyIconTray? _tray;
    private RailWindow? _rail;
    private UsageCoordinator? _coordinator;
    private IDisposable? _instanceLock;
    private ThresholdAlerts? _alerts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceLock = WindowsIntegration.AcquireSingleInstanceLock(out var isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show("Pulse is already running. Check the notification area.",
                "Pulse", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _rail = new RailWindow();
        _rail.Show();

        // Every registered provider gets a monitored account; the DPAPI vault holds
        // pasted credentials. Providers without a stored credential report
        // CredentialMissing — a visible, honest state, not a crash.
        ICredentialStore store = DpapiCredentialStore.IsWindowsSupported()
            ? new DpapiCredentialStore()
            : new InMemoryCredentialStore();
        var adapters = ProviderRegistry.CreateAll(store);
        var accounts = Enum.GetValues<ProviderId>()
            .Select(id => new MonitoredAccount { Provider = id, AccountId = id.ToString() })
            .ToArray();

        _alerts = new ThresholdAlerts();
        _alerts.ThresholdCrossed += (key, threshold) => Dispatcher.BeginInvoke(() =>
        {
            if (_tray is not null)
                _tray.ShowNotification("Pulse",
                    threshold >= 1.0
                        ? $"{key} limit reached"
                        : $"{key} passed {Math.Round(threshold * 100)}% used");
        });

        void OnReading(UsageCoordinator.Reading reading)
        {
            _rail.UpdateProvider(reading.Account, reading.Result);
            if (reading.Result.Usage is { Windows.Count: > 0 } usage)
            {
                var key = $"{usage.Provider}:{usage.AccountId}";
                foreach (var window in usage.Windows)
                    _alerts.Observe(key, window);
            }
        }

        _coordinator = new UsageCoordinator(adapters, accounts, OnReading);

        _tray = new NotifyIconTray();
        _tray.ShowRailRequested += () => _rail?.ShowAndRestore();
        _tray.ShowSettingsRequested += () => new SettingsWindow(store) { Topmost = true }.Show();
        _tray.ShowTokenSpendRequested += () => new TokenSpendWindow(new Pulse.Core.Ledger.TranscriptScanner()) { Topmost = true }.Show();
        _tray.ExitRequested += () =>
        {
            _coordinator?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            _tray?.Dispose();
            Shutdown();
        };
        _tray.Initialize();

        _coordinator.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}

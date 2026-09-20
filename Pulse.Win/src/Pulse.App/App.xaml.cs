using System.Windows;
using Pulse.App.Bootstrap;
using Pulse.App.Tray;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Providers;

namespace Pulse.App;

/// <summary>
/// Application entry point. ShutdownMode is OnExplicitShutdown because the rail is a
/// borderless always-available surface: closing windows must not kill the process —
/// exit goes through the tray menu.
/// </summary>
public partial class App : System.Windows.Application
{
    private NotifyIconTray? _tray;
    private RailWindow? _rail;
    private UsageCoordinator? _coordinator;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _rail = new RailWindow();
        _rail.Show();

        // Every registered provider gets a monitored account; the store (currently
        // in-memory until the vault milestone) holds pasted credentials. Providers
        // without a stored credential report CredentialMissing — a visible, honest
        // state, not a crash.
        var store = new InMemoryCredentialStore();
        var adapters = ProviderRegistry.CreateAll(store);
        var accounts = Enum.GetValues<ProviderId>()
            .Select(id => new MonitoredAccount { Provider = id, AccountId = id.ToString() })
            .ToArray();

        _coordinator = new UsageCoordinator(adapters, accounts, reading => _rail.UpdateProvider(reading.Account, reading.Result));

        _tray = new NotifyIconTray();
        _tray.ShowRailRequested += () => _rail?.ShowAndRestore();
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
        base.OnExit(e);
    }
}

using System.Windows;
using Pulse.App.Bootstrap;
using Pulse.App.Tray;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Providers.DeepSeek;
using Pulse.Providers.Kimi;
using Pulse.Providers.OpenCodeGo;

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

        // MVP wiring: the three key-based providers with user-pasted keys.
        _coordinator = new UsageCoordinator(
            new MonitoredAccount?[]
            {
                new MonitoredAccount { Provider = ProviderId.DeepSeek, AccountId = "deepSeek", Label = null },
                new MonitoredAccount { Provider = ProviderId.KimiCode, AccountId = "kimiCode", Label = null },
                new MonitoredAccount { Provider = ProviderId.OpenCodeGo, AccountId = "openCodeGo", Label = null },
            },
            key => new IUsageProvider?[]
            {
                new DeepSeekProvider(_ => key),
                new KimiCodeProvider(_ => key),
                new OpenCodeGoProvider(_ => key),
            },
            reading => _rail.UpdateProvider(reading.Account, reading.Result));

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

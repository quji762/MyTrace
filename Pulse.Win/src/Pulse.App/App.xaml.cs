using System.Windows;
using Pulse.App.Bootstrap;
using Pulse.App.Tray;
using Pulse.App.Settings;
using Pulse.App.Spend;
using Pulse.Auth;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Pulse.Core;
using Pulse.Core.ClaudeHook;
using Pulse.Core.Accounts;
using Pulse.Core.Notifications;
using Pulse.Core.Platform;
using Pulse.Core.Providers;
using Pulse.Core.Refresh;
using Pulse.Diagnostics;
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
    private SettingsWindow? _settings;
    private UsageCoordinator? _coordinator;
    private IDisposable? _instanceLock;
    private ThresholdAlerts? _alerts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Install crash logging first — anything after this that throws is captured.
        Bootstrap.CrashLogger.Install();

        // Apply the saved Light/Dark/System palette immediately — PulseStyles
        // ships dark, so without this a Light user stays dark until Settings.
        ThemeManager.Apply(ThemeManager.Current);
        ThemeManager.WatchSystem();

        // Before any window exists. WinForms strips dpiAwareness out of the
        // manifest in a mixed WPF project, so the mode is set here.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);

        // `--json` prints the last-good cache and exits — no UI, no fetch.
        if (e.Args.Contains("--json"))
        {
            Console.Out.Write(JsonReport.Emit());
            Shutdown();
            return;
        }

        // Status-line capture mode: Claude Code pipes a JSON blob to this
        // executable after every response. Bank the usage part, print the line,
        // and leave without ever building the UI. Anything unexpected is
        // swallowed on purpose — a status line that errors out or hangs is far
        // worse than one that says nothing.
        if (e.Args.Contains(Pulse.Core.ClaudeHook.StatusLinePaths.ModeArgument))
        {
            var payload = Console.In.ReadToEnd();
            var home = Environment.GetEnvironmentVariable("PULSE_HOME");
            var line = StatusLineCapture.RunAsStatusLine(payload, string.IsNullOrEmpty(home) ? null : home);
            if (!string.IsNullOrEmpty(line))
                Console.Out.Write(line);
            Shutdown();
            return;
        }

        _instanceLock = WindowsIntegration.AcquireSingleInstanceLock(out var isFirstInstance);
        var launchCommand = DeepLink.Parse(e.Args);
        if (!isFirstInstance)
        {
            // Second launch: hand a pulse:// command to the running rail, or say
            // that one is already up. Never start a second process tree.
            if (launchCommand is not null)
                DeepLink.WritePending(launchCommand);
            else
                System.Windows.MessageBox.Show(Ui.AlreadyRunning,
                    "Pulse", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DeepLink.Register();
        _rail = new RailWindow();
        _rail.Show();

        // The DPAPI vault holds pasted credentials and added-account tokens.
        // Providers see a store that unwraps OAuth bundles and renews them.
        // Settings keeps the raw store so a saved key is what the user typed.
        ICredentialStore vault = DpapiCredentialStore.IsWindowsSupported()
            ? new DpapiCredentialStore()
            : new InMemoryCredentialStore();

        // Every secret passing through the vault is registered with the shared
        // log scrubber: a credential must never surface in the crash or
        // diagnostic log, including tokens a background renewal writes back.
        ICredentialStore store = new SecretRegisteringStore(vault);

        var accounts = new MultiAccountStore(store);
        var adapters = ProviderRegistry.CreateAll(new RefreshingCredentialStore(store));

        // Rolling diagnostic log. RedactingLogger is the single choke point: every
        // message is scrubbed before the sink (5 MB per file, 7-day retention).
        var log = new RedactingLogger(
            new ILogSink[] { new FileLogSink(FileLogSink.DefaultDirectory()) },
            scrubber: CrashLogger.Scrubber);

        var alertLevel = AlertPreferences.Load(AlertPath());
        _alerts = new ThresholdAlerts(AlertPreferences.Thresholds(alertLevel));
        _alerts.ThresholdCrossed += (key, threshold) => Dispatcher.BeginInvoke(() =>
        {
            if (_tray is not null)
                _tray.ShowNotification("Pulse",
                    threshold >= 1.0
                        ? (UiLanguage.IsChinese ? $"{key} 已达上限" : $"{key} limit reached")
                        : (UiLanguage.IsChinese
                            ? $"{key} 已用 {Math.Round(threshold * 100)}%"
                            : $"{key} passed {Math.Round(threshold * 100)}% used"));
        });

        void OnReading(UsageCoordinator.Reading reading)
        {
            _rail.UpdateProvider(reading.Account, reading.Result);
            // Only alert on fresh readings — a cached fallback must not
            // re-trigger threshold alerts for data the user already saw.
            if (reading.Result.Health == ProviderReadHealth.Healthy &&
                reading.Result.Usage is { Windows.Count: > 0 } usage)
            {
                foreach (var window in usage.Windows)
                    _alerts.Observe(ThresholdAlerts.KeyFor(usage.Provider, usage.AccountId, window.Id), window);
            }
        }

        _coordinator = new UsageCoordinator(
            adapters,
            () => DueAccounts(accounts, store),
            OnReading,
            account => new AdaptiveRefresh.Signals
            {
                PanelVisible = _rail is { IsVisible: true, IsExpanded: true },
                RecentlyHovered = _rail?.HoveredRecently == true,
                PowerConstrained = IsOnBattery(),
                LocallyUnobservable = account.Provider is ProviderId.DeepSeek or ProviderId.CommandCode,
                // Only scan transcripts of providers that are actually enabled.
                LastLocalActivity = LocalActivity.LatestTranscriptWrite(EnabledTranscriptProviders(accounts, store)),
            },
            onAccountsResolved: activeAccounts => _rail?.SyncActiveProviders(activeAccounts),
            logger: log);
        _rail!.UserHovering += () => _coordinator?.RequestHover();

        _tray = new NotifyIconTray();
        _tray.ShowRailRequested += () => _rail?.ShowAndRestore();
        _tray.ShowSettingsRequested += () => ShowSettings(store);
        _rail.OpenSettingsRequested += () => ShowSettings(store);
        _rail.OpenTokenSpendRequested += () => OpenTokenSpend();
        _rail.ExitRequested += () =>
        {
            if (_coordinator is not null)
                Task.Run(async () => await _coordinator.DisposeAsync()).Wait(TimeSpan.FromSeconds(2));
            _tray?.Dispose();
            Shutdown();
        };
        _tray.ShowTokenSpendRequested += OpenTokenSpend;
        _tray.ExitRequested += () =>
        {
            if (_coordinator is not null)
                Task.Run(async () => await _coordinator.DisposeAsync()).Wait(TimeSpan.FromSeconds(2));
            _tray?.Dispose();
            Shutdown();
        };
        _tray.Initialize();
        _tray.OpenReleasesRequested += OpenReleasesPage;

        _hotkey = new Pulse.App.Platform.GlobalHotkey();
        _hotkey.Pressed += () => Dispatcher.BeginInvoke(() => _rail?.ToggleVisibility());
        ApplyHotkeyPreference();
        // Apply the hide-tray preference at startup (after hotkey is ready
        // so the entry-point invariant has the full picture).
        if (Pulse.Core.Platform.TrayPreferences.Load())
            _tray.SetVisible(false);

        _deeplinkTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _deeplinkTimer.Tick += (_, _) => DrainPendingDeepLink(store);
        _deeplinkTimer.Start();

        _updateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(2),
        };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync();
        _updateTimer.Start(); // periodic checks every 2h regardless of first result

        _coordinator.Start();
        _ = CheckForUpdateAsync();
        if (launchCommand is not null)
            ApplyDeepLink(launchCommand, store);
        else
            DrainPendingDeepLink(store);
    }

    private Pulse.App.Platform.GlobalHotkey? _hotkey;
    private System.Windows.Threading.DispatcherTimer? _deeplinkTimer;
    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    private string? _availableUpdateTag;

    internal void ApplyHotkeyPreference()
    {
        if (_hotkey is null || _rail is null) return;
        if (HotkeyPreferences.Load())
        {
            var (modifiers, key) = Pulse.App.Platform.GlobalHotkey.DefaultBinding;
            _hotkey.Register(_rail, modifiers, key);
        }
        else
        {
            _hotkey.Unregister();
        }

        // Changing the hotkey may affect the entry-point invariant.
        EnforceEntryPointInvariant();
    }

    /// <summary>Called when a provider is disabled in Settings: remove its rings from the rail.</summary>
    internal void NotifyProviderDisabled(ProviderId provider)
    {
        _rail?.RemoveProviderAll(provider);
    }

    /// <summary>Called when a provider is re-enabled in Settings: clear its denied keys so rings can return.</summary>
    internal void NotifyProviderEnabled(ProviderId provider)
    {
        _rail?.AllowProviderAll(provider);
    }

    /// <summary>
    /// Apply the hide-tray-icon preference. Refuses to hide when no other
    /// entry point (rail or global hotkey) remains — matching upstream's
    /// "hiding the menu bar icon cannot lock the user out" invariant.
    /// </summary>
    internal void ApplyTrayPreference(bool hide)
    {
        if (hide)
        {
            // At least one entry point must remain: the rail must be visible
            // or the hotkey must be registered. Otherwise refuse and keep tray.
            var railVisible = _rail is { IsVisible: true };
            var hotkeyRegistered = _hotkey?.IsRegistered == true;
            if (!railVisible && !hotkeyRegistered)
            {
                // Refuse: no other entry point. Keep the tray icon.
                Pulse.Core.Platform.TrayPreferences.Save(false);
                return;
            }
        }

        Pulse.Core.Platform.TrayPreferences.Save(hide);
        _tray?.SetVisible(!hide);
        EnforceEntryPointInvariant();
    }

    /// <summary>
    /// If the tray is hidden and neither the rail nor the hotkey provides an
    /// entry point, restore the tray icon. Called after any change that might
    /// remove the last entry point.
    /// </summary>
    private void EnforceEntryPointInvariant()
    {
        if (!Pulse.Core.Platform.TrayPreferences.Load()) return; // tray is visible
        var railVisible = _rail is { IsVisible: true };
        var hotkeyRegistered = _hotkey?.IsRegistered == true;
        if (!railVisible && !hotkeyRegistered)
        {
            // Last entry point gone: restore the tray icon.
            Pulse.Core.Platform.TrayPreferences.Save(false);
            _tray?.SetVisible(true);
        }
    }

    private void DrainPendingDeepLink(ICredentialStore store)
    {
        if (DeepLink.TakePending() is { } command)
            ApplyDeepLink(command, store);
    }

    private void ApplyDeepLink(DeepLink.Command command, ICredentialStore store)
    {
        switch (command.Action)
        {
            case DeepLink.Command.Show:
                _rail?.ShowAndRestore();
                break;
            case DeepLink.Command.Settings:
                ShowSettings(store);
                break;
            case DeepLink.Command.Account when command.AccountId is { } id:
                _rail?.FocusAccount(id);
                break;
        }
    }

    private static void OpenReleasesPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/quji762/MyTrace/releases",
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
        }
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var handler = new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
            NetworkProxy.Load().ApplyTo(handler);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PulseWin/" + CurrentVersion);
            var json = await client.GetStringAsync(
                "https://api.github.com/repos/quji762/MyTrace/releases?per_page=20").ConfigureAwait(true);
            var tag = UpdateSelection.ChooseFromReleaseJson(json);
            if (!UpdateSelection.IsNewer(CurrentVersion, tag))
            {
                _availableUpdateTag = null;
                _tray?.SetUpdateAvailable(null);
                return;
            }
            _availableUpdateTag = tag;
            _tray?.SetUpdateAvailable(tag);
            _tray?.ShowNotification("Pulse", Ui.UpdateReady(tag!));
            _updateTimer?.Start();
        }
        catch (Exception)
        {
            // An update check that cannot reach GitHub changes nothing on the rail.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _deeplinkTimer?.Stop();
        _updateTimer?.Stop();
        _hotkey?.Dispose();
        // Never block the dispatcher on DisposeAsync — hop to the thread pool.
        if (_coordinator is not null)
            Task.Run(async () => await _coordinator.DisposeAsync()).Wait(TimeSpan.FromSeconds(2));
        _tray?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }

    public static string CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? ProductVersion.Read(Path.Combine(AppContext.BaseDirectory, "VERSION"));

    private void ShowSettings(ICredentialStore store)
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(store) { Topmost = true };
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    private void OpenTokenSpend()
    {
        new TokenSpendWindow(new Pulse.Core.Ledger.TranscriptScanner()) { Topmost = true }.Show();
    }

    private static string AlertPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "alerts.json");

    private static string EnablementPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "enablement.json");

    /// <summary>Primary account for every provider, plus added slots where the provider allows them.</summary>
    private static IReadOnlyList<MonitoredAccount> DueAccounts(MultiAccountStore accounts, ICredentialStore store)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var overrides = ProviderEnablement.LoadOverrides(EnablementPath());
        bool Enabled(ProviderId id) => ProviderEnablement.IsEnabled(
            id,
            overrides,
            ProviderPresence.Found(id, home, roaming, FileOrDirectoryExists),
            store.GetSecret(id, id.ToString()) is { Length: > 0 }
                || accounts.List(id).Count > 0);

        return ProviderEnablement.DueAccounts(AccountsOf(accounts), Enabled);
    }

    /// <summary>
    /// Providers that keep local transcripts AND are currently enabled.
    /// Passed to LocalActivity so a disabled provider's transcript tree is not walked.
    /// </summary>
    private static IReadOnlySet<ProviderId> EnabledTranscriptProviders(MultiAccountStore accounts, ICredentialStore store)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var overrides = ProviderEnablement.LoadOverrides(EnablementPath());
        var set = new HashSet<ProviderId>();
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            if (!ProviderCapabilities.For(id).KeepsLocalTranscripts) continue;
            if (ProviderEnablement.IsEnabled(
                    id,
                    overrides,
                    ProviderPresence.Found(id, home, roaming, FileOrDirectoryExists),
                    store.GetSecret(id, id.ToString()) is { Length: > 0 }
                        || accounts.List(id).Count > 0))
                set.Add(id);
        }

        return set;
    }

    private static bool FileOrDirectoryExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Transparent ICredentialStore wrapper: every secret it sees is registered
    /// with the shared log scrubber, so no credential value can surface in the
    /// crash log or the diagnostic file log regardless of who stored or read it.
    /// </summary>
    private sealed class SecretRegisteringStore(ICredentialStore inner) : ICredentialStore
    {
        public string? GetSecret(ProviderId provider, string accountId)
        {
            var secret = inner.GetSecret(provider, accountId);
            if (!string.IsNullOrEmpty(secret))
                CrashLogger.RegisterSecret(secret);
            return secret;
        }

        public void SetSecret(ProviderId provider, string accountId, string secret)
        {
            CrashLogger.RegisterSecret(secret);
            inner.SetSecret(provider, accountId, secret);
        }

        public void RemoveSecret(ProviderId provider, string accountId) =>
            inner.RemoveSecret(provider, accountId);
    }

    /// <summary>Primary account for every provider, plus added slots where the provider allows them.</summary>
    private static IReadOnlyList<MonitoredAccount> AccountsOf(MultiAccountStore accounts)
    {
        var list = new List<MonitoredAccount>();
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            if (MultiAccountCapability.Supports(id))
                list.AddRange(accounts.MonitoredAccounts(id));
            else
                list.Add(new MonitoredAccount { Provider = id, AccountId = id.ToString() });
        }

        return list;
    }

    private static bool IsOnBattery()
    {
        try
        {
            return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
                == System.Windows.Forms.PowerLineStatus.Offline;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

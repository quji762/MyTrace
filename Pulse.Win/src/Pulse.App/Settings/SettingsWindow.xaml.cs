using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Pulse.Auth;
using Pulse.Core.Accounts;
using Pulse.Core.ClaudeHook;
using Pulse.Core.Platform;
using Pulse.Core.Ledger;
using Pulse.Core.Notifications;
using Pulse.Core.Providers;
using Pulse.Providers.Antigravity;

namespace Pulse.App.Settings;

/// <summary>
/// Settings window: one credential row per provider, additional OAuth accounts for
/// the four multi-account providers, and the launch-at-startup toggle. Save writes
/// through ICredentialStore (DPAPI vault) — secrets never touch settings files or
/// logs. A saved secret takes effect on the next refresh pass.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ICredentialStore _store;
    private readonly MultiAccountStore _multiAccount;
    private int _signInBusy;
    private bool _launchAtStartup = WindowsIntegration.IsLaunchAtStartupEnabled();
    private bool _loading = true;

    public sealed class CredentialRow
    {
        public ProviderId Provider { get; init; }
        public string DisplayName { get; init; } = "";
        public string Secret { get; set; } = "";
        public bool Enabled { get; set; }
        public bool HasStoredValue { get; init; }
    }

    public sealed class ExtraAccountRow
    {
        public ProviderId Provider { get; init; }
        public string DisplayName { get; init; } = "";
        public string AccountsSummary { get; set; } = "";
        public List<AddedAccountItem> Added { get; init; } = [];
    }

    public sealed class AddedAccountItem
    {
        public ProviderId Provider { get; init; }
        public string Slot { get; init; } = "";
        public string Label { get; set; } = "";
        public string DisplayName { get; init; } = "";
        public string RenameText { get; init; } = Ui.RenameAccount;
        public string SignInAgainText { get; init; } = Ui.SignInAgain;
        public string RemoveText { get; init; } = Ui.RemoveAccount;
    }

    public SettingsWindow(ICredentialStore store)
    {
        InitializeComponent();
        // Window-local PulseStyles merge ships dark; re-apply the live palette.
        ThemeManager.Apply(ThemeManager.Current);
        _store = store;
        _multiAccount = new MultiAccountStore(store);
        StartupToggle.IsChecked = _launchAtStartup;
        AlertChoice.SelectedIndex = AlertPreferences.Load(AlertPath()) switch
        {
            AlertLevel.Eighty => 1,
            AlertLevel.NinetyFive => 2,
            _ => 0,
        };
        SpendToggle.IsChecked = SpendOptIn.Load(SpendPath());
        _loading = false;

        Reload();
        ReloadStatusLine();
        ReloadNetworkAndVersion();
    }

    private void ReloadNetworkAndVersion()
    {
        // Setting SelectedIndex must not re-enter the change handlers.
        _loading = true;
        HotkeyToggle.IsChecked = Pulse.Core.Platform.HotkeyPreferences.Load();
        var proxy = Pulse.Core.Platform.NetworkProxy.Load();
        ProxyModeChoice.SelectedIndex = proxy.Mode == Pulse.Core.Platform.ProxyMode.Manual ? 1 : 0;
        ProxyHostBox.Text = proxy.Host ?? "";
        ProxyPortBox.Text = proxy.Port?.ToString() ?? "";
        VersionText.Text = App.CurrentVersion;
        LanguageChoice.SelectedIndex = UiLanguage.IsChinese ? 0 : 1;
        ThemeChoice.SelectedIndex = ThemeManager.Current switch
        {
            ThemeManager.Theme.Light => 0,
            ThemeManager.Theme.Dark => 1,
            _ => 2,
        };
        _loading = false;
        ApplyChrome();
    }

    private void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || LanguageChoice is null) return;
        UiLanguage.Apply(LanguageChoice.SelectedIndex == 1
            ? UiLanguage.Language.English
            : UiLanguage.Language.Chinese);
        ApplyChrome();
        Reload();
    }

    /// <summary>Re-label the settings chrome after a language switch.</summary>
    private void ApplyChrome()
    {
        Title = Ui.WindowTitle;
        BrandCaption.Text = Ui.BrandCaption;
        RailNavLabel.Text = Ui.RailNavLabel;
        RailNavHint.Text = Ui.RailNavHint;
        RailNavHint2.Text = Ui.RailNavHint2;
        ProvidersTitle.Text = Ui.ProvidersTitle;
        ProvidersHint.Text = Ui.ProvidersHint;
        MoreAccountsTitle.Text = Ui.SectionMoreAccounts;
        MoreAccountsHint.Text = Ui.MoreAccountsHint;
        GeneralTitle.Text = Ui.SectionGeneral;
        StartupToggle.Content = Ui.LaunchAtStartup;
        SpendToggle.Content = Ui.ReadSpend;
        HotkeyToggle.Content = Ui.GlobalHotkey;
        AlertsLabelBlock.Text = Ui.AlertsLabel;
        AlertOffItem.Content = Ui.AlertOff;
        Alert80Item.Content = Ui.Alert80;
        Alert95Item.Content = Ui.Alert95;
        SignInCopilotButton.Content = Ui.SignInCopilot;
        LanguageLabelBlock.Text = Ui.LanguageLabel;
        ThemeLabelBlock.Text = Ui.ThemeLabel;
        ((ComboBoxItem)ThemeChoice.Items[0]!).Content = Ui.ThemeLight;
        ((ComboBoxItem)ThemeChoice.Items[1]!).Content = Ui.ThemeDark;
        ((ComboBoxItem)ThemeChoice.Items[2]!).Content = Ui.ThemeSystem;
        NetworkLabelBlock.Text = Ui.NetworkLabel;
        ((ComboBoxItem)ProxyModeChoice.Items[0]!).Content = Ui.FollowSystem;
        ((ComboBoxItem)ProxyModeChoice.Items[1]!).Content = Ui.ManualProxy;
        SaveProxyButton.Content = Ui.SaveProxy;
        ProxyHint.Text = Ui.ProxyHint;
        VersionLabelBlock.Text = Ui.VersionLabel;
        VersionText.Text = App.CurrentVersion;
        CheckUpdatesButton.Content = Ui.CheckForUpdates;
        StatusLineTitle.Text = Ui.StatusLineTitle;
        VaultNote.Text = Ui.VaultNote;
        CloseButton.Content = Ui.Close;
    }

    private void OnThemeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || ThemeChoice is null) return;
        ThemeManager.Apply(ThemeChoice.SelectedIndex switch
        {
            0 => ThemeManager.Theme.Light,
            1 => ThemeManager.Theme.Dark,
            _ => ThemeManager.Theme.System,
        });
    }

    private void OnHotkeyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || HotkeyToggle is null) return;
        Pulse.Core.Platform.HotkeyPreferences.Save(HotkeyToggle.IsChecked == true);
        if (System.Windows.Application.Current is App app)
            app.ApplyHotkeyPreference();
    }

    private void OnProxyModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || ProxyModeChoice is null) return;
        // Mode alone does not replace a saved endpoint — host+port still commit together.
    }

    private void OnSaveProxy(object sender, RoutedEventArgs e)
    {
        var mode = ProxyModeChoice.SelectedIndex == 1
            ? Pulse.Core.Platform.ProxyMode.Manual
            : Pulse.Core.Platform.ProxyMode.System;
        var host = ProxyHostBox.Text.Trim();
        if (!int.TryParse(ProxyPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            if (mode == Pulse.Core.Platform.ProxyMode.Manual)
            {
                ProxyHint.Text = "Port must be a whole number from 1 to 65535. Previous endpoint kept.";
                return;
            }
            port = 0;
        }
        if (mode == Pulse.Core.Platform.ProxyMode.Manual && host.Length == 0)
        {
            ProxyHint.Text = "Host is required for a manual proxy. Previous endpoint kept.";
            return;
        }

        var proxy = mode == Pulse.Core.Platform.ProxyMode.Manual
            ? new Pulse.Core.Platform.NetworkProxy(mode, host, port)
            : Pulse.Core.Platform.NetworkProxy.Default;
        proxy.Save();
        ProxyHint.Text = proxy.HasEndpoint
            ? $"Saved: {proxy.Host}:{proxy.Port}"
            : "Saved: follow system.";
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        try
        {
            var handler = new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
            Pulse.Core.Platform.NetworkProxy.Load().ApplyTo(handler);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PulseWin/" + App.CurrentVersion);
            var json = await client.GetStringAsync(
                "https://api.github.com/repos/quji762/MyTrace/releases?per_page=20");
            var tag = Pulse.Core.Platform.UpdateSelection.ChooseFromReleaseJson(json);
            if (Pulse.Core.Platform.UpdateSelection.IsNewer(App.CurrentVersion, tag))
                System.Windows.MessageBox.Show(this, $"{tag} is available. Open Releases from the tray menu.", "Pulse",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            else
                System.Windows.MessageBox.Show(this, "You are up to date.", "Pulse",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Update check failed: {ex.Message}", "Pulse",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string AlertPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseWin", "alerts.json");

    private static string SpendPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseWin", "spend-opt-in.txt");

    private void OnAlertChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || AlertChoice is null) return;
        var level = AlertChoice.SelectedIndex switch
        {
            1 => AlertLevel.Eighty,
            2 => AlertLevel.NinetyFive,
            _ => AlertLevel.Off,
        };
        AlertPreferences.Save(AlertPath(), level);
    }

    private void OnSpendToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || SpendToggle is null) return;
        SpendOptIn.Save(SpendPath(), SpendToggle.IsChecked == true);
    }

    private async void OnCopilotSignIn(object sender, RoutedEventArgs e)
    {
        try
        {
            var prompt = await GitHubDeviceLogin.StartAsync();
            OpenBrowser(prompt.VerificationUrl);
            var token = await GitHubDeviceLogin.WaitForTokenAsync(prompt);
            if (string.IsNullOrEmpty(token)) return;
            _store.SetSecret(ProviderId.Copilot, ProviderId.Copilot.ToString(), token);
            Reload();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Pulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Reload()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var overrides = ProviderEnablement.LoadOverrides(EnablementPath());
        _loading = true;
        ProviderList.ItemsSource = Enum.GetValues<ProviderId>()
            .Select(id => new CredentialRow
            {
                Provider = id,
                DisplayName = DisplayName(id),
                Secret = _store.GetSecret(id, id.ToString()) ?? "",
                Enabled = ProviderEnablement.IsEnabled(
                    id, overrides,
                    ProviderPresence.Found(id, home, roaming, path => File.Exists(path) || Directory.Exists(path)),
                    _store.GetSecret(id, id.ToString()) is { Length: > 0 }),
                HasStoredValue = _store.GetSecret(id, id.ToString()) is not null,
            })
            .ToArray();
        _loading = false;

        ExtraAccountList.ItemsSource = Enum.GetValues<ProviderId>()
            .Where(MultiAccountCapability.Supports)
            .Select(id => new ExtraAccountRow
            {
                Provider = id,
                DisplayName = DisplayName(id),
                AccountsSummary = Summarize(_multiAccount.List(id)),
                Added = _multiAccount.List(id)
                    .Select(stored => new AddedAccountItem
                    {
                        Provider = id,
                        Slot = stored.Slot,
                        Label = stored.Label ?? stored.Slot,
                        DisplayName = DisplayName(id),
                    })
                    .ToList(),
            })
            .ToArray();
    }

    private static string Summarize(IReadOnlyList<MultiAccountStore.StoredAccount> accounts) =>
        accounts.Count == 0 ? "1 account (primary)" : $"primary + {accounts.Count} added";

    private static string EnablementPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "enablement.json");

    private void OnProviderToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not System.Windows.Controls.CheckBox { DataContext: CredentialRow row })
            return;
        var overrides = new Dictionary<ProviderId, bool>(ProviderEnablement.LoadOverrides(EnablementPath()))
        {
            [row.Provider] = row.Enabled,
        };
        ProviderEnablement.SaveOverrides(EnablementPath(), overrides);
    }

    private void OnSaveCredential(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: CredentialRow row }) return;

        if (string.IsNullOrWhiteSpace(row.Secret))
            _store.RemoveSecret(row.Provider, row.Provider.ToString());
        else
            _store.SetSecret(row.Provider, row.Provider.ToString(), row.Secret.Trim());

        System.Windows.MessageBox.Show(this, $"{row.DisplayName} credential saved.", "Pulse",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Add-account entry: drives the provider's own OAuth flow in the default
    /// browser, then stores the token under a fresh, never-reused slot. The CLI's
    /// own stored login is never read or written by this path.
    /// </summary>
    private async void OnAddAccount(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ExtraAccountRow row }) return;
        if (System.Threading.Interlocked.Exchange(ref _signInBusy, 1) == 1)
        {
            System.Windows.MessageBox.Show(this, "Another sign-in is already running.", "Pulse",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (row.Provider == ProviderId.Antigravity)
            {
                // No OAuth route reports real Antigravity quota; an added account
                // is another running language server's connection.
                var connection = PromptAntigravityConnection();
                if (connection is null) return;
                _multiAccount.Add(row.Provider, connection.Serialize(), label: $"added {DateTime.Now:MM-dd HH:mm}");
                Reload();
                return;
            }

            OAuthTokens? tokens = row.Provider switch
            {
                ProviderId.Codex => await RunOpenAIDeviceAsync(),
                ProviderId.Grok => await RunGrokDeviceAsync(),
                ProviderId.ClaudeCode => await RunClaudeLoopbackAsync(),
                ProviderId.GrokBot => await RunCursorWebAsync(),
                _ => null,
            };

            if (tokens is null)
            {
                System.Windows.MessageBox.Show(this, "Sign-in did not complete in time.", "Pulse",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _multiAccount.Add(row.Provider, tokens.Serialize(), label: $"added {DateTime.Now:MM-dd HH:mm}");
            Reload();
        }
        catch (OperationCanceledException)
        {
            // user closed the window mid-flow; nothing to report
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Sign-in failed: {ex.Message}", "Pulse",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _signInBusy, 0);
        }
    }

    /// <summary>Collect ports + CSRF of another Antigravity language server.
    /// Cancel or empty fields add nothing.</summary>
    private AntigravityConnection? PromptAntigravityConnection()
    {
        var portsBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 4, 0, 8) };
        var tokenBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 4, 0, 8) };
        var ok = new System.Windows.Controls.Button { Content = "Add", IsDefault = true, Width = 72, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Width = 72 };
        var dialog = new Window
        {
            Title = "Add Antigravity account",
            Owner = this,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new System.Windows.Controls.TextBlock
                    {
                        Text = "Antigravity quota is only available while that login's language server is running. " +
                               "Paste the port(s) and CSRF token from the other Antigravity instance " +
                               "(command line flag --csrf_token).",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.7,
                        Margin = new Thickness(0, 0, 0, 10),
                    },
                    new System.Windows.Controls.TextBlock { Text = "Port(s), comma-separated" },
                    portsBox,
                    new System.Windows.Controls.TextBlock { Text = "CSRF token" },
                    tokenBox,
                    new StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        Children = { ok, cancel },
                    },
                },
            },
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return null;

        var ports = portsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var port) ? port : 0)
            .Where(port => port is > 0 and < 65536)
            .ToArray();
        var token = tokenBox.Text.Trim();
        if (ports.Length == 0 || token.Length == 0) return null;
        return new AntigravityConnection(ports, token);
    }

    private void OnRenameAccount(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: AddedAccountItem item }) return;
        var label = PromptText("Rename account", "Label", item.Label);
        if (label is null) return;
        _multiAccount.Rename(item.Provider, item.Slot, label);
        Reload();
    }

    private void OnRemoveAccount(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: AddedAccountItem item }) return;
        var answer = System.Windows.MessageBox.Show(this,
            $"Remove “{item.Label}”? Its credential is deleted from the vault.",
            "Pulse", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        _multiAccount.Remove(item.Provider, item.Slot);
        Reload();
    }

    /// <summary>Re-run the provider's sign-in and replace this slot's secret,
    /// keeping its name. One extra-account sign-in at a time; a slot removed
    /// mid-flow is not written back.</summary>
    private async void OnSignInAgain(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: AddedAccountItem item }) return;
        if (System.Threading.Interlocked.Exchange(ref _signInBusy, 1) == 1)
        {
            System.Windows.MessageBox.Show(this, "Another sign-in is already running.", "Pulse",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            if (item.Provider == ProviderId.Antigravity)
            {
                var connection = PromptAntigravityConnection();
                if (connection is null) return;
                if (!_multiAccount.ReplaceSecret(item.Provider, item.Slot, connection.Serialize()))
                {
                    System.Windows.MessageBox.Show(this, "That account was removed; nothing was written.", "Pulse",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Reload();
                return;
            }

            OAuthTokens? tokens = item.Provider switch
            {
                ProviderId.Codex => await RunOpenAIDeviceAsync(),
                ProviderId.Grok => await RunGrokDeviceAsync(),
                ProviderId.ClaudeCode => await RunClaudeLoopbackAsync(),
                ProviderId.GrokBot => await RunCursorWebAsync(),
                _ => null,
            };
            if (tokens is null)
            {
                System.Windows.MessageBox.Show(this, "Sign-in did not complete in time.", "Pulse",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_multiAccount.ReplaceSecret(item.Provider, item.Slot, tokens.Serialize()))
            {
                System.Windows.MessageBox.Show(this, "That account was removed; nothing was written.", "Pulse",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Reload();
        }
        catch (OperationCanceledException)
        {
            // user closed the window mid-flow; the old secret stays
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Sign-in failed: {ex.Message}", "Pulse",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _signInBusy, 0);
        }
    }

    private string? PromptText(string title, string fieldLabel, string initial)
    {
        var box = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 8) };
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, Width = 72, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Width = 72 };
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new System.Windows.Controls.TextBlock { Text = fieldLabel },
                    box,
                    new StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        Children = { ok, cancel },
                    },
                },
            },
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }

    private static async Task<OAuthTokens?> RunOpenAIDeviceAsync()
    {
        var login = new OpenAIDeviceLogin();
        var prompt = await login.StartAsync();
        OpenBrowser(prompt.VerificationUrl);
        return await login.WaitForTokensAsync(prompt);
    }

    private static async Task<OAuthTokens?> RunGrokDeviceAsync()
    {
        var login = new GrokDeviceLogin();
        var prompt = await login.StartAsync();
        // xAI sends verification_uri_complete and it is used; a link Pulse asked
        // for itself is not the phishing vector the pre-fill attack needs.
        OpenBrowser(prompt.VerificationUrlComplete ?? prompt.VerificationUrl);
        return await login.WaitForTokensAsync(prompt);
    }

    private static async Task<OAuthTokens?> RunClaudeLoopbackAsync()
    {
        var login = new ClaudeLoopbackLogin();
        var state = Guid.NewGuid().ToString("N");
        var (verifier, challenge) = Pkce.Create();
        OpenBrowser(login.BuildAuthorizeUrl(state, challenge));
        var callback = await login.WaitForCallbackAsync(TimeSpan.FromMinutes(5));
        if (callback is not { } cb || cb.State != state)
            return null; // state mismatch: refuse, never exchange
        return await ClaudeLoopbackLogin.ExchangeAsync(cb.Code, cb.State, login.RedirectUri, verifier);
    }

    private static async Task<OAuthTokens?> RunCursorWebAsync()
    {
        var login = new CursorWebLogin();
        var attempt = login.Start();
        OpenBrowser(attempt.LoginUrl);
        return await login.WaitForTokenAsync(attempt);
    }

    private static void OpenBrowser(string url)
    {
        // Only http(s) may reach the shell — a crafted verification_uri must
        // not launch an arbitrary protocol handler.
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (StartupToggle is not null)
            _launchAtStartup = StartupToggle.IsChecked == true;
    }

    // --- Claude Code status line -------------------------------------------

    private void ReloadStatusLine()
    {
        var installed = StatusLineInstaller.IsInstalled();
        StatusLineButton.Content = installed ? Ui.Disable : Ui.Enable;
        StatusLineSummary.Text = installed
            ? "Enabled — Claude Code pipes each response's usage to Pulse."
            : "Not installed.";
    }

    private void OnStatusLineToggle(object sender, RoutedEventArgs e)
    {
        // An unreadable settings.json must never be replaced wholesale; both
        // paths refuse and say so rather than clobber the user's settings.
        var ok = StatusLineInstaller.IsInstalled()
            ? StatusLineInstaller.Uninstall()
            : StatusLineInstaller.Install(ExecutablePath());
        if (!ok)
        {
            System.Windows.MessageBox.Show(this,
                "Claude Code's settings.json exists but could not be parsed. It was left unchanged.",
                "Pulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ReloadStatusLine();
    }

    /// <summary>This executable, quoted for the hook command line.</summary>
    private static string ExecutablePath() =>
        Environment.ProcessPath
        ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // Persist the startup preference when settings close.
        WindowsIntegration.SetLaunchAtStartup(_launchAtStartup);
        base.OnClosed(e);
    }

    private static string DisplayName(ProviderId id) => ProviderCatalog.DisplayName(id);
}

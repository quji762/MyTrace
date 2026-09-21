using System.Windows;
using System.Windows.Controls;
using Pulse.Auth;
using Pulse.Core.Accounts;
using Pulse.Core.ClaudeHook;
using Pulse.Core.Platform;
using Pulse.Core.Providers;

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
    private bool _launchAtStartup = WindowsIntegration.IsLaunchAtStartupEnabled();

    public sealed class CredentialRow
    {
        public ProviderId Provider { get; init; }
        public string DisplayName { get; init; } = "";
        public string Secret { get; set; } = "";
        public bool HasStoredValue { get; init; }
    }

    public sealed class ExtraAccountRow
    {
        public ProviderId Provider { get; init; }
        public string DisplayName { get; init; } = "";
        public string AccountsSummary { get; set; } = "";
    }

    public SettingsWindow(ICredentialStore store)
    {
        InitializeComponent();
        _store = store;
        _multiAccount = new MultiAccountStore(store);
        StartupToggle.IsChecked = _launchAtStartup;

        Reload();
        ReloadStatusLine();
    }

    private void Reload()
    {
        ProviderList.ItemsSource = Enum.GetValues<ProviderId>()
            .Select(id => new CredentialRow
            {
                Provider = id,
                DisplayName = DisplayName(id),
                Secret = _store.GetSecret(id, id.ToString()) ?? "",
                HasStoredValue = _store.GetSecret(id, id.ToString()) is not null,
            })
            .ToArray();

        ExtraAccountList.ItemsSource = Enum.GetValues<ProviderId>()
            .Where(MultiAccountCapability.Supports)
            .Select(id => new ExtraAccountRow
            {
                Provider = id,
                DisplayName = DisplayName(id),
                AccountsSummary = Summarize(_multiAccount.List(id)),
            })
            .ToArray();
    }

    private static string Summarize(IReadOnlyList<MultiAccountStore.StoredAccount> accounts) =>
        accounts.Count == 0 ? "1 account (primary)" : $"primary + {accounts.Count} added";

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

        try
        {
            OAuthTokens? tokens = row.Provider switch
            {
                ProviderId.Codex => await RunOpenAIDeviceAsync(),
                ProviderId.Grok => await RunGrokDeviceAsync(),
                ProviderId.ClaudeCode => await RunClaudeLoopbackAsync(),
                _ => null, // GrokBot's Cursor web login needs an interactive window; paste is its fallback today
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
        var (verifier, _) = Pkce.Create();
        OpenBrowser(login.BuildAuthorizeUrl(state));
        var callback = await login.WaitForCallbackAsync(TimeSpan.FromMinutes(5));
        if (callback is not { } cb || cb.State != state)
            return null; // state mismatch: refuse, never exchange
        return await ClaudeLoopbackLogin.ExchangeAsync(cb.Code, cb.State, login.RedirectUri, verifier);
    }

    private static void OpenBrowser(string url)
    {
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
        StatusLineButton.Content = installed ? "Disable" : "Enable";
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

    private static string DisplayName(ProviderId id) => id switch
    {
        ProviderId.ClaudeCode => "Claude Code",
        ProviderId.OpenCodeGo => "OpenCode Go",
        ProviderId.KimiCode => "Kimi Code",
        ProviderId.OllamaCloud => "Ollama Cloud (session cookie)",
        ProviderId.Zai => "z.ai (API key)",
        ProviderId.GlmCoding => "GLM Coding Plan (API key)",
        ProviderId.MiniMax => "MiniMax (API key)",
        ProviderId.MiniMaxCN => "MiniMax CN (API key)",
        ProviderId.Copilot => "GitHub Copilot (OAuth token)",
        ProviderId.Grok => "Grok (CLI token)",
        ProviderId.GrokBot => "Grok Bot (Cursor session)",
        ProviderId.Volcengine => "Volcengine (AccessKeyID:SecretAccessKey)",
        ProviderId.CommandCode => "Command Code (API key)",
        ProviderId.DeepSeek => "DeepSeek (API key)",
        ProviderId.Devin => "Devin (token + org)",
        ProviderId.XiaomiMiMo => "Xiaomi MiMo (session cookie)",
        _ => id.ToString(),
    };
}

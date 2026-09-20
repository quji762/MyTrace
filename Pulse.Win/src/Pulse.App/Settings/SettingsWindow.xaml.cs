using System.Windows;
using System.Windows.Controls;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;

namespace Pulse.App.Settings;

/// <summary>
/// Settings window: one credential row per provider. Save writes through
/// ICredentialStore (DPAPI vault) — secrets never touch settings files or logs.
/// A saved secret takes effect on the next refresh pass; no restart needed.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ICredentialStore _store;

    public sealed class CredentialRow
    {
        public ProviderId Provider { get; init; }
        public string DisplayName { get; init; } = "";
        public string Secret { get; set; } = "";
        public bool HasStoredValue { get; init; }
    }

    public SettingsWindow(ICredentialStore store)
    {
        InitializeComponent();
        _store = store;

        ProviderList.ItemsSource = Enum.GetValues<ProviderId>()
            .Select(id => new CredentialRow
            {
                Provider = id,
                DisplayName = DisplayName(id),
                Secret = _store.GetSecret(id, id.ToString()) ?? "",
                HasStoredValue = _store.GetSecret(id, id.ToString()) is not null,
            })
            .ToArray();
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // Persist the startup preference whenever settings close.
        Pulse.Core.Platform.WindowsIntegration.SetLaunchAtStartup(_launchAtStartup);
        base.OnClosed(e);
    }

    private bool _launchAtStartup = Pulse.Core.Platform.WindowsIntegration.IsLaunchAtStartupEnabled();

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

using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Providers.Antigravity;
using Pulse.Providers.Claude;
using Pulse.Providers.Codex;
using Pulse.Providers.CommandCode;
using Pulse.Providers.Copilot;
using Pulse.Providers.Cursor;
using Pulse.Providers.DeepSeek;
using Pulse.Providers.Devin;
using Pulse.Providers.Grok;
using Pulse.Providers.GrokBot;
using Pulse.Providers.Kimi;
using Pulse.Providers.Kiro;
using Pulse.Providers.MiniMax;
using Pulse.Providers.Ollama;
using Pulse.Providers.OpenCodeGo;
using Pulse.Providers.Volcengine;
using Pulse.Providers.Xiaomi;
using Pulse.Providers.Zai;

namespace Pulse.Providers;

/// <summary>
/// The provider registry: one adapter per supported ProviderId, all fed by the
/// same credential store. A missing adapter = a provider the build does not yet
/// support; the refresh engine skips it silently rather than erroring.
/// All 20 upstream quota providers are registered; the browser-session routes
/// surface their credential through paste today and isolated WebView2 later.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ProviderRegistry
{
    /// <summary>Create adapters for every provider, reading secrets through the store.</summary>
    public static IReadOnlyDictionary<ProviderId, IUsageProvider> CreateAll(ICredentialStore store)
    {
        // The resolvers bind the provider at construction; the store is keyed by
        // provider + account id and the pasted value IS the secret for these.
        string? Resolve(ProviderId provider, string? accountId)
        {
            // Callers pass the account id. A missing one is the primary slot.
            var id = string.IsNullOrWhiteSpace(accountId) ? provider.ToString() : accountId;
            var secret = store.GetSecret(provider, id);
            return string.IsNullOrWhiteSpace(secret) ? null : secret;
        }

        return new Dictionary<ProviderId, IUsageProvider>
        {
            // CLI-borrowed routes: pasted token wins, then the Windows CLI files.
            [ProviderId.ClaudeCode] = new ClaudeCodeProvider(
                key => Resolve(ProviderId.ClaudeCode, key), () => ClaudeCodeProvider.LocateCliToken()),
            [ProviderId.Codex] = new CodexProvider(
                key => Resolve(ProviderId.Codex, key), () => CodexProvider.LocateCliCredentials()),
            [ProviderId.Grok] = new GrokProvider(key => Resolve(ProviderId.Grok, key)),

            // Key-based routes.
            [ProviderId.DeepSeek] = new DeepSeekProvider(key => Resolve(ProviderId.DeepSeek, key)),
            [ProviderId.KimiCode] = new KimiCodeProvider(key => Resolve(ProviderId.KimiCode, key)),
            [ProviderId.OpenCodeGo] = new OpenCodeGoProvider(key => Resolve(ProviderId.OpenCodeGo, key)),
            [ProviderId.Zai] = new ZaiProvider(ProviderId.Zai, key => Resolve(ProviderId.Zai, key)),
            [ProviderId.GlmCoding] = new ZaiProvider(ProviderId.GlmCoding, key => Resolve(ProviderId.GlmCoding, key)),
            [ProviderId.MiniMax] = new MiniMaxProvider(ProviderId.MiniMax, key => Resolve(ProviderId.MiniMax, key)),
            [ProviderId.MiniMaxCN] = new MiniMaxProvider(ProviderId.MiniMaxCN, key => Resolve(ProviderId.MiniMaxCN, key)),
            [ProviderId.CommandCode] = new CommandCodeProvider(key => Resolve(ProviderId.CommandCode, key)),
            [ProviderId.Volcengine] = new VolcengineProvider(key => Resolve(ProviderId.Volcengine, key)),

            // Session-cookie routes (paste; never browser-store decryption).
            [ProviderId.OllamaCloud] = new OllamaCloudProvider(key => Resolve(ProviderId.OllamaCloud, key)),
            [ProviderId.XiaomiMiMo] = new XiaomiMiMoProvider(key => Resolve(ProviderId.XiaomiMiMo, key)),

            // Editor-credential routes.
            [ProviderId.Cursor] = new CursorProvider(key => Resolve(ProviderId.Cursor, key)),
            [ProviderId.GrokBot] = new GrokBotProvider(key => Resolve(ProviderId.GrokBot, key)),
            [ProviderId.Copilot] = new CopilotProvider(key => Resolve(ProviderId.Copilot, key)),

            // Token+org route (paste).
            [ProviderId.Devin] = new DevinProvider(key => Resolve(ProviderId.Devin, key)),

            // Local language-server route: primary discovers the process; added
            // slots carry their own ports + CSRF and never borrow discovery.
            [ProviderId.Antigravity] = new AntigravityProvider(
                key => Resolve(ProviderId.Antigravity, key)),

            // CLI ACP route: short-lived `kiro-cli acp` with its saved login.
            [ProviderId.Kiro] = new KiroProvider(),

            // V2EX: documented API with Personal Access Token.
            [ProviderId.V2EX] = new V2EX.V2EXProvider(key => Resolve(ProviderId.V2EX, key)),

            // Sub2API: subscription + wallet.
            [ProviderId.Sub2API] = new Sub2API.Sub2APIProvider(key => Resolve(ProviderId.Sub2API, key)),

            // New API: token quota + balance.
            [ProviderId.NewAPI] = new NewAPI.NewAPIProvider(key => Resolve(ProviderId.NewAPI, key)),

            // Qoder: browser session cookie.
            [ProviderId.Qoder] = new Qoder.QoderProvider(key => Resolve(ProviderId.Qoder, key)),
        };
    }
}

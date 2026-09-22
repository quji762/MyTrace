namespace Pulse.Core.Providers;

/// <summary>Human names for the nineteen quota providers. The rail and settings read only this.</summary>
public static class ProviderCatalog
{
    public static string DisplayName(ProviderId id) => id switch
    {
        ProviderId.ClaudeCode => "Claude Code",
        ProviderId.Codex => "Codex",
        ProviderId.Antigravity => "Antigravity",
        ProviderId.Cursor => "Cursor",
        ProviderId.OpenCodeGo => "OpenCode Go",
        ProviderId.KimiCode => "Kimi Code",
        ProviderId.OllamaCloud => "Ollama Cloud",
        ProviderId.Zai => "z.ai",
        ProviderId.GlmCoding => "Zhipu",
        ProviderId.MiniMax => "MiniMax",
        ProviderId.MiniMaxCN => "MiniMax CN",
        ProviderId.Copilot => "GitHub Copilot",
        ProviderId.Grok => "Grok",
        ProviderId.GrokBot => "Grok Bot",
        ProviderId.Volcengine => "Volcengine",
        ProviderId.CommandCode => "Command Code",
        ProviderId.DeepSeek => "DeepSeek",
        ProviderId.Devin => "Devin",
        ProviderId.XiaomiMiMo => "Xiaomi Coding Plan",
        _ => id.ToString(),
    };

    public static IReadOnlyList<ProviderId> All { get; } = Enum.GetValues<ProviderId>();
}

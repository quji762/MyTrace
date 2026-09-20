namespace Pulse.Core.Providers;

/// <summary>
/// The 19 quota providers defined by upstream Pulse (qunqin24/Pulse).
/// Numeric values are stable serialization ids; never renumber.
/// </summary>
public enum ProviderId
{
    ClaudeCode = 0,
    Codex = 1,
    Antigravity = 2,
    Cursor = 3,
    OpenCodeGo = 4,
    KimiCode = 5,
    OllamaCloud = 6,
    Zai = 7,
    GlmCoding = 8,
    MiniMax = 9,
    MiniMaxCN = 10,
    Copilot = 11,
    Grok = 12,
    GrokBot = 13,
    Volcengine = 14,
    CommandCode = 15,
    DeepSeek = 16,
    Devin = 17,
    XiaomiMiMo = 18,
}

/// <summary>
/// Route by which usage data was obtained, mirroring upstream UsageRoute.
/// </summary>
public enum UsageRoute
{
    Endpoint,
    StatusLine,
    DesktopSession,
    AppServer,
    LanguageServer,
    WebSession,
    ArkCLI,
    AppCache,
}

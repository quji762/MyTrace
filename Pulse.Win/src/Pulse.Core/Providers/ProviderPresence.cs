namespace Pulse.Core.Providers;

/// <summary>
/// First-run evidence: named paths only. The probe asks whether a path exists.
/// It does not open the file or parse a secret.
/// </summary>
public static class ProviderPresence
{
    /// <summary>Ollama, Xiaomi, and Qoder have no local credential file. They stay off until a cookie is pasted.</summary>
    public static bool RequiresPastedCookie(ProviderId id) =>
        id is ProviderId.OllamaCloud or ProviderId.XiaomiMiMo or ProviderId.Qoder;

    public static IReadOnlyList<string> CandidatePaths(ProviderId id, string home, string roaming)
    {
        string Home(params string[] parts) => Path.Combine(new[] { home }.Concat(parts).ToArray());
        string Roaming(params string[] parts) => Path.Combine(new[] { roaming }.Concat(parts).ToArray());

        return id switch
        {
            ProviderId.ClaudeCode => [Home(".claude")],
            ProviderId.Codex => [Home(".codex")],
            ProviderId.Grok => [Home(".grok")],
            ProviderId.Antigravity => [Home(".gemini", "antigravity"), Home(".gemini", "antigravity-cli")],
            ProviderId.Cursor => [Roaming("Cursor", "User", "globalStorage", "state.vscdb")],
            ProviderId.OpenCodeGo => [Home(".local", "share", "opencode", "auth.json")],
            ProviderId.GlmCoding =>
            [
                Home(".coding-relay", "glm-api-key"),
                Home(".config", "bigmodel", "api_key"),
                Home(".config", "zhipu", "api_key"),
            ],
            ProviderId.CommandCode => [Home(".commandcode", "auth.json")],
            ProviderId.Devin =>
            [
                Roaming("Devin", "User", "globalStorage", "state.vscdb"),
                Roaming("Windsurf", "User", "globalStorage", "state.vscdb"),
            ],
            ProviderId.Kiro => [Home(".kiro")],
            ProviderId.Qoder => [Home(".qoder")],
            ProviderId.V2EX => [Roaming("V2EX"), Home(".config", "v2ex")],
            ProviderId.Sub2API => [],
            ProviderId.NewAPI => [],
            _ => [],
        };
    }

    public static bool Found(ProviderId id, string home, string roaming, Func<string, bool> exists)
    {
        foreach (var path in CandidatePaths(id, home, roaming))
        {
            if (exists(path)) return true;
        }

        return false;
    }

    /// <summary>A fresh profile enables a provider only when a path is present or a secret is already stored.
    /// Cookie-based providers require a pasted cookie (secret), not just a local directory.</summary>
    public static bool DefaultEnabled(bool pathPresent, bool secretStored) =>
        secretStored || (pathPresent && true);
}

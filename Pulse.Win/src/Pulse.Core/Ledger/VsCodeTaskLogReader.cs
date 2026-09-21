using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Roo Code, Kilo Code and Cline's VS Code task logs; port of upstream
/// VSCodeTaskLogReader. Each task is a directory named for the session holding
/// a `ui_messages.json` array and usually an `api_conversation_history.json`
/// beside it. Only the `api_req_started` entries count, and each is one real
/// request: the products write the four token kinds for it under a `text` field
/// that is itself JSON.
///
/// Windows roots: the desktop editors (Code, Code - Insiders, VSCodium) keep
/// their extension storage under %APPDATA%\&lt;editor&gt;\User\globalStorage; the
/// remote .vscode-server tree is probed too. `ts` is milliseconds as a string or
/// a number; an unparseable one skips the entry. The conversation history's last
/// `&lt;model&gt;` tag is the model fallback for a run whose entries predate the
/// per-entry modelInfo.
/// </summary>
public static class VsCodeTaskLogReader
{
    private static readonly Dictionary<string, string> ExtensionIDs = new()
    {
        ["roocode"] = "rooveterinaryinc.roo-cline",
        ["kilocode"] = "kilocode.kilo-code",
        ["cline"] = "saoudrizwan.claude-dev",
    };

    private static readonly string[] DesktopEditors = ["Code", "Code - Insiders", "VSCodium"];
    private static readonly string[] RemoteServers = [".vscode-server", ".vscode-server-insiders"];

    /// <summary>Every root the named client is read from; a root is returned
    /// whether or not it exists yet.</summary>
    public static IReadOnlyList<string> Inputs(string client, string? userProfile = null)
    {
        if (!ExtensionIDs.TryGetValue(client, out var extensionID)) return Array.Empty<string>();

        var appData = userProfile is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(userProfile, "AppData", "Roaming");
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var roots = new List<string>();
        foreach (var editor in DesktopEditors)
        {
            var storage = Path.Combine("User", "globalStorage", extensionID, "tasks");
            roots.Add(Path.Combine(appData, editor, storage));
            roots.Add(Path.Combine(home, ".config", editor, storage));
        }
        foreach (var server in RemoteServers)
        {
            roots.Add(Path.Combine(home, server, "data", "User", "globalStorage", extensionID, "tasks"));
        }
        return roots;
    }

    public static IReadOnlyList<AgentUsageRecord> Records(string client, string? userProfile = null)
    {
        var records = new List<AgentUsageRecord>();
        foreach (var root in Inputs(client, userProfile))
        {
            if (!Directory.Exists(root)) continue;
            records.AddRange(RecordsFromRoots(new[] { root }));
        }
        return records;
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var tasks = new Dictionary<string, (string? Messages, string? History)>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var name = Path.GetFileName(f);
                        return name is "ui_messages.json" or "api_conversation_history.json";
                    }).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                var directory = Path.GetDirectoryName(file)!;
                var (messages, history) = tasks.GetValueOrDefault(directory);
                if (Path.GetFileName(file) == "ui_messages.json") messages = file;
                else history = file;
                tasks[directory] = (messages, history);
            }
        }

        var records = new List<AgentUsageRecord>();
        foreach (var (directory, task) in tasks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (task.Messages is null) continue;
            var session = Path.GetFileName(directory);
            var details = task.History is { } historyFile ? EnvironmentDetails.FromFile(historyFile) : null;
            records.AddRange(Parse(task.Messages, session, details));
        }
        return records;
    }

    private static IEnumerable<AgentUsageRecord> Parse(string messagesFile, string session, EnvironmentDetails? details)
    {
        JsonElement entries;
        try
        {
            entries = JsonDocument.Parse(File.ReadAllText(messagesFile)).RootElement.Clone();
        }
        catch (JsonException) { yield break; }
        if (entries.ValueKind != JsonValueKind.Array) yield break;

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (Str(entry, "type") != "say" || Str(entry, "say") != "api_req_started") continue;
            // ts is milliseconds as a string or a number; unparseable skips.
            if (TimestampOf(entry, "ts") is not { } timestamp) continue;
            var text = Str(entry, "text");
            if (text is null) continue;

            JsonElement usage;
            try { usage = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { continue; }
            if (usage.ValueKind != JsonValueKind.Object) continue;

            var tally = new TokenTally(
                Input: IntOf(usage, "tokensIn"),
                CacheWrite: IntOf(usage, "cacheWrites"),
                CacheRead: IntOf(usage, "cacheReads"),
                Output: IntOf(usage, "tokensOut"));
            if (tally.Total <= 0) continue;

            // The entry's own model wins; the history's last <model> tag is the
            // fallback for a run that predates modelInfo.
            string? model = ObjectOf(entry, "modelInfo", out var modelInfo) && Str(modelInfo, "modelId") is { } entryModel
                ? entryModel
                : details?.Model;
            if (string.IsNullOrWhiteSpace(model)) continue;

            yield return new AgentUsageRecord
            {
                Timestamp = timestamp,
                Model = model,
                Tally = tally,
                SessionID = session,
                SessionName = details?.Agent,
            };
        }
    }

    // --- the conversation history's environment block ------------------------------------

    /// <summary>The &lt;model&gt;, &lt;slug&gt; and &lt;name&gt; tags written inside
    /// &lt;environment_details&gt; blocks — the only fallbacks used when an entry
    /// names no model.</summary>
    public sealed record EnvironmentDetails(string? Model, string? Agent)
    {
        public static EnvironmentDetails? FromFile(string path)
        {
            try
            {
                return Parse(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static EnvironmentDetails Parse(string text)
        {
            var details = new EnvironmentDetails(null, null);
            foreach (var block in Blocks(text))
            {
                if (LastTag("model", block) is { } model) details = details with { Model = model };
                // The slug is the product's own handle for the agent; the human
                // name is only a fallback for a run that wrote no slug.
                if (LastTag("slug", block) is { } slug) details = details with { Agent = slug };
                else if (LastTag("name", block) is { } name) details = details with { Agent = name };
            }
            return details;
        }

        private static List<string> Blocks(string text)
        {
            var found = new List<string>();
            var searchStart = 0;
            while (true)
            {
                var open = text.IndexOf("<environment_details>", searchStart, StringComparison.Ordinal);
                if (open < 0) break;
                var close = text.IndexOf("</environment_details>", open, StringComparison.Ordinal);
                if (close < 0) break;
                found.Add(text[(open + "<environment_details>".Length)..close]);
                searchStart = close + "</environment_details>".Length;
            }
            return found;
        }

        private static string? LastTag(string tag, string text)
        {
            var open = $"<{tag}>";
            var close = $"</{tag}>";
            string? value = null;
            var searchStart = 0;
            while (true)
            {
                var start = text.IndexOf(open, searchStart, StringComparison.Ordinal);
                if (start < 0) break;
                var end = text.IndexOf(close, start, StringComparison.Ordinal);
                if (end < 0) break;
                var content = text[(start + open.Length)..end].Trim();
                if (content.Length > 0) value = content;
                searchStart = end + close.Length;
            }
            return value;
        }
    }

    // --- helpers ---------------------------------------------------------------------------

    private static bool ObjectOf(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object) return false;
        value = property;
        return true;
    }

    private static DateTimeOffset? TimestampOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property))
            return null;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetInt64(out var millis) && millis > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            case JsonValueKind.String:
            {
                var text = property.GetString();
                if (long.TryParse(text, out var millis) && millis > 0)
                    return DateTimeOffset.FromUnixTimeMilliseconds(millis);
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                    return parsed;
                return null;
            }
            default:
                return null;
        }
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int IntOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)property.GetDouble(),
            _ => 0,
        };
    }
}

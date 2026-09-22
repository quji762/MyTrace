using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Codebuff's chat logs, under its `manicode*` product trees; port of upstream
/// CodebuffUsageReader. `&lt;root&gt;/projects/&lt;project&gt;/chats/&lt;chatId&gt;/
/// chat-messages.json` is a top-level array of messages.
///
/// Usage is MERGED, not summed: the same provider numbers are copied into
/// several places — metadata.usage, metadata.codebuff.usage, and the last
/// assistant row's providerOptions in the run-state history. Each field is taken
/// from the first source that reported a NON-ZERO value, so a zero in a
/// higher-priority copy cannot mask the real count below it, and a field written
/// twice is counted once.
///
/// Credits are not tokens: `credits` is a cost the product keeps beside the
/// counts and is left for the price table rather than read as usage. Freebuff
/// chats share this directory; a chat whose assistant rows carry authoritative
/// usage belongs here whatever its agent type, and a `base2-free` chat with none
/// contributes no record from either reader — the same batch is never counted
/// twice.
/// </summary>
public static class CodebuffUsageReader
{
    private static readonly string[] InputAliases = ["inputTokens", "input_tokens", "promptTokens", "prompt_tokens"];
    private static readonly string[] OutputAliases = ["outputTokens", "output_tokens", "completionTokens", "completion_tokens"];
    private static readonly string[] CacheReadAliases = ["cacheReadInputTokens", "cache_read_input_tokens"];
    private static readonly string[] CacheWriteAliases = ["cacheCreationInputTokens", "cache_creation_input_tokens", "cacheCreationTokens", "cache_creation_tokens", "cachedTokensCreated", "cached_tokens_created"];

    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // The manicode* product trees (manicode, manicode-beta, …).
        List<string> roots;
        try
        {
            var homeRoot = Path.Combine(home, ".config");
            roots = Directory.Exists(homeRoot)
                ? Directory.EnumerateDirectories(homeRoot, "manicode*").ToList()
                : new List<string>();
        }
        catch (Exception)
        {
            return Array.Empty<AgentUsageRecord>();
        }
        return RecordsFromRoots(roots);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var records = new List<AgentUsageRecord>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(root, "chat-messages.json", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                JsonElement? messagesMaybe = null;
                try
                {
                    var parsed = JsonDocument.Parse(File.ReadAllText(file));
                    if (parsed.RootElement.ValueKind == JsonValueKind.Array)
                        messagesMaybe = parsed.RootElement.Clone();
                    parsed.Dispose();
                }
                catch (JsonException) { }
                if (messagesMaybe is not { } messages) continue;

                var location = Location(file);
                var sessionID = $"{location.Channel}/{location.Project}/{location.ChatId}";

                var index = 0;
                foreach (var message in messages.EnumerateArray())
                {
                    var position = index++;
                    if (message.ValueKind != JsonValueKind.Object || !IsAssistant(message)) continue;
                    var sources = UsageSources(message);
                    if (sources.Count == 0) continue;

                    var tally = new TokenTally(
                        Input: Merged(sources, InputAliases),
                        CacheWrite: Merged(sources, CacheWriteAliases),
                        CacheRead: CacheRead(sources),
                        Output: Merged(sources, OutputAliases));
                    if (tally.Total <= 0) continue;

                    var model = Model(message, sources);
                    if (model is null) continue;
                    var timestamp = Timestamp(message, location.ChatId);
                    if (timestamp is null) continue;

                    var identity = Str(message, "id")
                        ?? $"{sessionID}:{position}:{timestamp.Value.ToUnixTimeSeconds()}:{model}:"
                        + $"{tally.Input}:{tally.CacheWrite}:{tally.CacheRead}:{tally.Output}";

                    records.Add(new AgentUsageRecord
                    {
                        Timestamp = timestamp.Value,
                        Model = model,
                        Tally = tally,
                        SessionID = sessionID,
                        SessionName = location.ChatId,
                        DeduplicationID = $"codebuff:{sessionID}:{identity}",
                    });
                }
            }
        }
        return records;
    }

    /// <summary>Whether a row is the assistant's own: `variant` is the newer
    /// marker and `role` the older one; either is enough.</summary>
    public static bool IsAssistant(JsonElement message)
    {
        foreach (var key in new[] { "variant", "role" })
        {
            if (Str(message, key)?.ToLowerInvariant() is { } marker &&
                marker is "ai" or "agent" or "assistant")
                return true;
        }
        return false;
    }

    /// <summary>Every usage object the message carries, highest priority first.</summary>
    public static List<JsonElement> UsageSources(JsonElement message)
    {
        var sources = new List<JsonElement>();
        if (!ObjectOf(message, "metadata", out var metadata)) return sources;

        if (ObjectOf(metadata, "usage", out var usage)) sources.Add(usage);
        if (ObjectOf(metadata, "codebuff", out var codebuff) &&
            ObjectOf(codebuff, "usage", out var codebuffUsage)) sources.Add(codebuffUsage);

        if (ObjectOf(metadata, "runState", out var runState) &&
            ObjectOf(runState, "sessionState", out var sessionState) &&
            ObjectOf(sessionState, "mainAgentState", out var mainAgentState) &&
            mainAgentState.TryGetProperty("messageHistory", out var history) &&
            history.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in history.EnumerateArray().Reverse())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (ObjectOf(row, "providerOptions", out var providers))
                {
                    if (ObjectOf(providers, "usage", out var providerUsage)) sources.Add(providerUsage);
                    if (ObjectOf(providers, "codebuff", out var providerCodebuff) &&
                        ObjectOf(providerCodebuff, "usage", out var providerCodebuffUsage)) sources.Add(providerCodebuffUsage);
                }
            }
        }
        return sources;
    }

    /// <summary>Cache-read tokens, including the two nested detail spellings.</summary>
    private static int CacheRead(List<JsonElement> sources)
    {
        var flat = Merged(sources, CacheReadAliases);
        if (flat > 0) return flat;

        foreach (var source in sources)
        {
            foreach (var key in new[] { "promptTokensDetails", "prompt_tokens_details" })
            {
                if (ObjectOf(source, key, out var details))
                {
                    if (IntOf(details, "cachedTokens") is { } camel && camel != 0) return camel;
                    if (IntOf(details, "cached_tokens") is { } snake && snake != 0) return snake;
                }
            }
        }
        return 0;
    }

    /// <summary>metadata.model → the last history row's
    /// providerOptions.codebuff.model → the usage object's own model.</summary>
    public static string? Model(JsonElement message, List<JsonElement> sources)
    {
        if (ObjectOf(message, "metadata", out var metadata))
        {
            if (Str(metadata, "model") is { } named) return named;
            if (ObjectOf(metadata, "runState", out var runState) &&
                ObjectOf(runState, "sessionState", out var sessionState) &&
                ObjectOf(sessionState, "mainAgentState", out var mainAgentState) &&
                mainAgentState.TryGetProperty("messageHistory", out var history) &&
                history.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in history.EnumerateArray().Reverse())
                {
                    if (row.ValueKind == JsonValueKind.Object &&
                        ObjectOf(row, "providerOptions", out var providers) &&
                        ObjectOf(providers, "codebuff", out var codebuff) &&
                        Str(codebuff, "model") is { } name)
                        return name;
                }
            }
        }
        foreach (var source in sources)
        {
            if (Str(source, "model") is { } sourceModel) return sourceModel;
        }
        return null;
    }

    /// <summary>The message's own timestamp, else its createdAt, else the
    /// metadata's, else the chat id restored to ISO 8601. Zero is "unset", not
    /// 1970, so it falls through to the next real field.</summary>
    public static DateTimeOffset? Timestamp(JsonElement message, string chatId)
    {
        if (EventTime(message, "timestamp") is { } a) return a;
        if (EventTime(message, "createdAt") is { } b) return b;
        if (ObjectOf(message, "metadata", out var metadata) &&
            EventTime(metadata, "timestamp") is { } c) return c;
        return IsoFromChatID(chatId);
    }

    // --- path ------------------------------------------------------------------------------

    /// <summary>Channel, project and chat id from `…/projects/&lt;project&gt;/chats/&lt;chatId&gt;/`.
    /// A tree that was renamed or moved: the directory above `chats` is the
    /// project, and the config directory above it names the channel.</summary>
    public static (string Channel, string Project, string ChatId) Location(string file)
    {
        var chatId = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
        var components = file.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var projectsIndex = Array.LastIndexOf(components, "projects");
        if (projectsIndex > 0 && projectsIndex + 1 < components.Length)
            return (components[projectsIndex - 1], components[projectsIndex + 1], chatId);

        var chatsDir = Path.GetDirectoryName(Path.GetDirectoryName(file));
        var project = Path.GetFileName(chatsDir) ?? "";
        var channel = Path.GetFileName(Path.GetDirectoryName(chatsDir)) ?? "";
        return (channel, project, chatId);
    }

    // --- shared helpers ----------------------------------------------------------------------

    private static bool ObjectOf(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object) return false;
        value = property;
        return true;
    }

    private static int Merged(List<JsonElement> sources, string[] aliases)
    {
        foreach (var source in sources)
        {
            foreach (var alias in aliases)
            {
                if (IntOf(source, alias) is { } value && value > 0) return value;
            }
        }
        return 0;
    }

    private static int? IntOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)property.GetDouble(),
            _ => null,
        };
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    /// <summary>A real event time is strictly after the epoch; a zero is a
    /// store's "not set" that slipped through as 1970.</summary>
    private static DateTimeOffset? EventTime(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetInt64(out var millis) && millis > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            case JsonValueKind.String when property.GetString() is { } text:
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
                    return parsed;
                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>An ISO 8601 chat id whose time separators were written as `-`,
    /// restored to a date. Only the HH-MM-SS after the T becomes :, so the
    /// date's own dashes are left alone.</summary>
    public static DateTimeOffset? IsoFromChatID(string chatID)
    {
        var marker = chatID.IndexOf('T');
        if (marker < 0) return null;
        var head = chatID[..marker];
        var tail = chatID[(marker + 1)..].Replace('-', ':');
        return DateTimeOffset.TryParse($"{head}T{tail}", CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}

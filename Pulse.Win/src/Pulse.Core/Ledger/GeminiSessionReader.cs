using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Gemini CLI's three on-disk shapes; port of upstream GeminiSessionReader.
/// - a legacy whole-session JSON (`session-*.json`),
/// - the current chat recording (`tmp/&lt;id&gt;/chats/&lt;file&gt;.json`),
/// - a headless JSONL stream whose `init` line names the model and session and
///   whose `tokens`/`stats` lines carry usage.
///
/// The token keys are aliases — `prompt`/`input_tokens`/`promptTokenCount` etc —
/// and the cache relation is SHAPE-SPECIFIC, so the two decoders stay apart.
/// Tool tokens are real counters: the session shape folds them into fresh input
/// while the headless usage object does not carry them at all. Reasoning is
/// additive on top of output, and cache writes are always zero here.
/// </summary>
public static class GeminiSessionReader
{
    public enum Shape { Session, Headless }

    private static string? _homeOverride;

    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? _homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".gemini");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoot(root);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoot(string root)
    {
        var records = new List<AgentUsageRecord>();
        var incomplete = false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.json*", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                (IReadOnlyList<AgentUsageRecord> Records, bool Incomplete) parsed;
                if (extension == ".jsonl")
                    parsed = Headless(file);
                else if (Path.GetFileName(file).StartsWith("session-", StringComparison.Ordinal) || IsChatPath(file))
                    parsed = Session(file);
                else
                    continue;
                records.AddRange(parsed.Records);
                incomplete |= parsed.Incomplete;
            }
        }
        catch (Exception)
        {
            return records; // an unreadable tree reads short, not fatal
        }
        return incomplete ? records.Select(MarkPartial).ToList() : records;
    }

    private static AgentUsageRecord MarkPartial(AgentUsageRecord record) => record with { IsPartial = true };

    /// <summary>The current chat recording's exact `…/tmp/&lt;id&gt;/chats/&lt;file&gt;.json`
    /// shape. A JSON file anywhere else is not accepted: a different shape read as
    /// this one would attribute the wrong session.</summary>
    public static bool IsChatPath(string path)
    {
        var components = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var chats = Array.LastIndexOf(components, "chats");
        return chats >= 2 && components[chats - 2] == "tmp" && components[chats - 1].Length > 0;
    }

    // --- session JSON -----------------------------------------------------------------

    public static (IReadOnlyList<AgentUsageRecord> Records, bool Incomplete) Session(string path)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        }
        catch (Exception)
        {
            return (Array.Empty<AgentUsageRecord>(), false);
        }
        if (root.ValueKind != JsonValueKind.Object)
            return (Array.Empty<AgentUsageRecord>(), false);

        var fallbackID = Path.GetFileNameWithoutExtension(path);
        var sessionID = Str(root, "sessionId") ?? Str(root, "session_id") ?? fallbackID;
        var records = new List<AgentUsageRecord>();
        var incomplete = false;

        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return (records, incomplete);

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object) continue;
            if (Str(message, "type") != "gemini") continue;
            if (!message.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object) continue;

            var usage = Decode(tokens, Shape.Session, tokenWrapper: false);
            if (usage.Tally.Total <= 0 && usage.Unclassified <= 0) continue;

            var model = Str(message, "model");
            var timestamp = TimestampOf(message, "timestamp") ?? TimestampOf(message, "created_at");
            if (model is null || timestamp is null)
            {
                // Real usage with no model or no locatable time.
                incomplete = true;
                continue;
            }

            var id = Str(message, "id");
            records.Add(new AgentUsageRecord
            {
                Timestamp = timestamp.Value,
                Model = model,
                Tally = usage.Tally,
                SessionID = sessionID,
                DeduplicationID = id is { } messageID ? $"gemini:session:{sessionID}:{messageID}" : null,
                UnclassifiedTokens = usage.Unclassified,
            });
        }
        return (records, incomplete);
    }

    // --- headless JSONL ------------------------------------------------------------------

    public static (IReadOnlyList<AgentUsageRecord> Records, bool Incomplete) Headless(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (IOException) { return (Array.Empty<AgentUsageRecord>(), false); }

        var fileStem = Path.GetFileNameWithoutExtension(path);
        string? currentModel = null;
        string? currentSession = null;
        var records = new List<AgentUsageRecord>();
        var indexByID = new Dictionary<string, int>();
        var incomplete = false;

        void Submit(AgentUsageRecord record, string? id)
        {
            if (id is not null && indexByID.TryGetValue(id, out var existing))
            {
                // A re-export of the same call REPLACES its original in place
                // rather than adding a second copy.
                records[existing] = record;
            }
            else
            {
                if (id is not null) indexByID[id] = records.Count;
                records.Add(record);
            }
        }

        var lineNumber = -1;
        foreach (var raw in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonElement? rowMaybe = null;
            try { rowMaybe = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch (JsonException) { }
            if (rowMaybe is not { } row || row.ValueKind != JsonValueKind.Object) continue;

            if (Str(row, "type") == "init")
            {
                currentModel = Str(row, "model") ?? currentModel;
                currentSession = Str(row, "session_id") ?? Str(row, "sessionId") ?? currentSession;
                continue;
            }

            var sessionID = Str(row, "session_id") ?? Str(row, "sessionId") ?? currentSession ?? fileStem;
            var lineID = Str(row, "id");
            var lineTime = TimestampOf(row, "timestamp") ?? TimestampOf(row, "created_at");

            if (row.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                var usage = Decode(tokens, Shape.Headless, tokenWrapper: true);
                if (usage.Tally.Total <= 0 && usage.Unclassified <= 0) continue;
                var model = Str(row, "model") ?? currentModel;
                if (model is null || lineTime is null) { incomplete = true; continue; }
                Submit(new AgentUsageRecord
                {
                    Timestamp = lineTime.Value,
                    Model = model,
                    Tally = usage.Tally,
                    SessionID = sessionID,
                    DeduplicationID = lineID is { } withID ? $"gemini:line:{withID}" : $"gemini:headless:{fileStem}:{lineNumber}",
                    UnclassifiedTokens = usage.Unclassified,
                }, id: lineID);
                continue;
            }

            JsonElement stats = default;
            var hasStats = false;
            if (row.TryGetProperty("stats", out var statsElement) && statsElement.ValueKind == JsonValueKind.Object)
            {
                stats = statsElement;
                hasStats = true;
            }
            else if (row.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
                     result.TryGetProperty("stats", out var resultStats) && resultStats.ValueKind == JsonValueKind.Object)
            {
                stats = resultStats;
                hasStats = true;
            }
            if (!hasStats) continue;

            if (stats.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
            {
                foreach (var modelEntry in models.EnumerateObject())
                {
                    if (modelEntry.Value.ValueKind != JsonValueKind.Object) continue;
                    var usage = Decode(modelEntry.Value, Shape.Headless, tokenWrapper: false);
                    if (usage.Tally.Total <= 0 && usage.Unclassified <= 0) continue;
                    var timestamp = TimestampOf(modelEntry.Value, "timestamp") ?? lineTime;
                    if (timestamp is null) { incomplete = true; continue; }
                    var id = lineID is { } lineWithID ? $"{lineWithID}:{modelEntry.Name}" : null;
                    Submit(new AgentUsageRecord
                    {
                        Timestamp = timestamp.Value,
                        Model = modelEntry.Name,
                        Tally = usage.Tally,
                        SessionID = sessionID,
                        DeduplicationID = id is { } withID ? $"gemini:line:{withID}" : $"gemini:headless:{fileStem}:{lineNumber}:{modelEntry.Name}",
                        UnclassifiedTokens = usage.Unclassified,
                    }, id: id);
                }
            }
            else
            {
                var usage = Decode(stats, Shape.Headless, tokenWrapper: false);
                if (usage.Tally.Total <= 0 && usage.Unclassified <= 0) continue;
                var model = Str(stats, "model") ?? currentModel;
                var timestamp = TimestampOf(stats, "timestamp") ?? lineTime;
                if (model is null || timestamp is null) { incomplete = true; continue; }
                Submit(new AgentUsageRecord
                {
                    Timestamp = timestamp.Value,
                    Model = model,
                    Tally = usage.Tally,
                    SessionID = sessionID,
                    DeduplicationID = lineID is { } plainID ? $"gemini:line:{plainID}" : $"gemini:headless:{fileStem}:{lineNumber}",
                    UnclassifiedTokens = usage.Unclassified,
                }, id: lineID);
            }
        }
        return (records, incomplete);
    }

    // --- tokens ---------------------------------------------------------------------------

    private static readonly string[] InputKeys = ["input", "prompt", "input_tokens", "prompt_tokens", "promptTokenCount"];
    private static readonly string[] OutputKeys = ["output", "candidates", "output_tokens", "completion_tokens", "candidatesTokenCount"];
    private static readonly string[] CachedKeys = ["cached", "cached_tokens", "cachedContentTokenCount"];
    private static readonly string[] ReasoningKeys = ["thoughts", "reasoning", "thoughts_tokens"];
    private static readonly string[] ToolKeys = ["tool", "tool_tokens"];
    private static readonly string[] TotalKeys = ["total", "totalTokenCount", "total_tokens"];

    /// <summary>Decodes a Gemini usage object into disjoint buckets. The session
    /// shape's cache overlap is proven only by a total equal to the non-cache
    /// sum; the headless shape treats an input arriving under a prompt-style key
    /// (or a tokens wrapper) as cache-inclusive and a bare `input` field as
    /// already net. A bare total with no named kind is unclassified rather than
    /// guessed into input.</summary>
    public static (TokenTally Tally, int Unclassified) Decode(JsonElement @object, Shape shape, bool tokenWrapper)
    {
        if (@object.ValueKind != JsonValueKind.Object) return (new TokenTally(), 0);

        var (inputValue, inputKey) = First(@object, InputKeys);
        var (outputValue, _) = First(@object, OutputKeys);
        var (cachedValue, _) = First(@object, CachedKeys);
        var (reasoningValue, _) = First(@object, ReasoningKeys);
        var (toolValue, _) = First(@object, ToolKeys);
        var (totalValue, _) = First(@object, TotalKeys);

        var inputCount = CountValue(inputValue);
        var outputCount = CountValue(outputValue);
        var cachedCount = CountValue(cachedValue);
        var reasoningCount = CountValue(reasoningValue);
        var toolCount = CountValue(toolValue);
        int? totalCount = totalValue is null ? null : CountValue(totalValue);

        var anyKind = inputValue is not null || outputValue is not null || cachedValue is not null ||
                      reasoningValue is not null || toolValue is not null;
        if (!anyKind)
            return (new TokenTally(), totalCount ?? 0);

        var outputBucket = outputCount + reasoningCount;
        switch (shape)
        {
            case Shape.Session:
            {
                var raw = inputCount + toolCount;
                // A reported total is the authority when present. Without one,
                // documented Gemini semantics still apply to a prompt-style
                // input key; a net `input` field is already fresh and stays.
                if (totalCount is { } sessionTotal)
                {
                    var cacheInclusive = sessionTotal == raw + outputCount + reasoningCount &&
                                         sessionTotal != raw + outputCount + reasoningCount + cachedCount;
                    var fresh = cacheInclusive ? Math.Max(0, raw - cachedCount) : raw;
                    return (new TokenTally(Input: fresh, CacheWrite: 0, CacheRead: cachedCount, Output: outputBucket), 0);
                }
                var promptStyle = inputKey is { } key && key != "input";
                var freshSession = promptStyle ? Math.Max(0, raw - cachedCount) : raw;
                return (new TokenTally(Input: freshSession, CacheWrite: 0, CacheRead: cachedCount, Output: outputBucket), 0);
            }
            case Shape.Headless:
            {
                var cacheInclusive = tokenWrapper || (inputKey is { } headlessKey && headlessKey != "input");
                var freshHeadless = cacheInclusive ? Math.Max(0, inputCount - cachedCount) : inputCount;
                return (new TokenTally(Input: freshHeadless, CacheWrite: 0, CacheRead: cachedCount, Output: outputBucket), 0);
            }
            default:
                return (new TokenTally(), 0);
        }
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static (JsonElement? Value, string? Key) First(JsonElement @object, string[] keys)
    {
        foreach (var key in keys)
        {
            if (@object.ValueKind == JsonValueKind.Object && @object.TryGetProperty(key, out var property))
                return (property.Clone(), key);
        }
        return (null, null);
    }

    private static int CountValue(JsonElement? value)
    {
        if (value is not { } element) return 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)element.GetDouble(),
            JsonValueKind.String when int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => 0,
        };
    }

    private static DateTimeOffset? TimestampOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property))
            return null;
        switch (property.ValueKind)
        {
            case JsonValueKind.String when DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed):
                return parsed;
            case JsonValueKind.Number when property.TryGetInt64(out var millis) && millis > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            default:
                return null;
        }
    }
}

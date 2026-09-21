using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// CommandCode's transcripts; port of upstream CommandCodeReader.
/// One JSONL per session under `~/.commandcode/projects/&lt;slug&gt;/`. The modern
/// format is a TREE: every entry names a `parentId`, and /rewind moves the leaf
/// while abandoned replies keep their usage on disk. Replies on every branch
/// carry real work: rewinding context does not refund tokens already consumed.
/// Message identities fold replays; ancestry only resolves the model a reply
/// used, so a model change on another branch cannot reprice it.
///
/// The modern `usage` object names all four buckets and is cache-exclusive. The
/// legacy flat format may carry no usage at all; where it does not there is NO
/// estimate — token counts from string lengths are not reported tokens.
/// </summary>
public static class CommandCodeSpendReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projects = Path.Combine(home, ".commandcode", "projects");
        var config = Path.Combine(home, ".commandcode", "config.json");
        var roots = new List<string> { projects, config };
        return RecordsFromRoots(roots);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IReadOnlyList<string> roots)
    {
        var configModel = ConfigModel(roots);
        var records = new List<AgentUsageRecord>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".checkpoints.jsonl", StringComparison.Ordinal))
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var file in files)
                records.AddRange(Parse(file, configModel));
        }
        return records;
    }

    private sealed record Entry(int Index, string? Id, string? Parent, string? Type, string? Model, string? Role, DateTimeOffset? Timestamp, string? Session, TokenTally? Tally);

    private static IEnumerable<AgentUsageRecord> Parse(string file, string? configModel)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        string? headerSession = null;
        var entries = new List<Entry>();

        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (IOException) { yield break; }

        var index = 0;
        foreach (var raw in lines)
        {
            var position = index++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(raw); }
            catch (JsonException) { continue; }

            using (document)
            {
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object) continue;
                var type = Str(row, "type");
                if (type == "session")
                {
                    headerSession = NonBlank(Str(row, "id")) ?? headerSession;
                    continue;
                }
                var message = ObjectOf(row, "message");
                var role = NonBlank(Str(message, "role")) ?? NonBlank(Str(row, "role"));
                TokenTally? tally = null;
                if (ObjectOf(row, "usage") is { } usageElement)
                    tally = new TokenTally(
                        Input: IntOf(usageElement, "inputTokens"),
                        CacheWrite: IntOf(usageElement, "cacheWriteTokens"),
                        CacheRead: IntOf(usageElement, "cacheReadTokens"),
                        Output: IntOf(usageElement, "outputTokens"));

                entries.Add(new Entry(
                    Index: position,
                    Id: NonBlank(Str(row, "id")),
                    Parent: NonBlank(Str(row, "parentId")),
                    Type: type,
                    Model: EditorLog.ModelID(Str(row, "model")),
                    Role: role,
                    // Only assistant rows carry a real timestamp.
                    Timestamp: role == "assistant" ? TimestampOf(row, "timestamp") : null,
                    Session: NonBlank(Str(row, "sessionId")),
                    Tally: tally));
            }
        }

        var hasTree = entries.Any(entry => entry.Parent is not null);
        var byID = new Dictionary<string, Entry>();
        foreach (var entry in entries)
        {
            if (entry.Id is { } id) byID[id] = entry;
        }
        var inheritedModels = new Dictionary<string, string?>();

        var currentModel = (string?)null;
        foreach (var entry in entries)
        {
            if (entry.Type == "model_change")
            {
                if (entry.Model is { } changed) currentModel = changed;
                continue;
            }

            if (entry.Role != "assistant" || entry.Timestamp is not { } timestamp || entry.Tally is not { } tally) continue;
            if (tally.Total <= 0) continue;

            var model = entry.Model
                ?? (hasTree ? AncestorModel(entry, byID, inheritedModels) : null)
                ?? currentModel
                ?? configModel;
            if (model is null) continue;

            var session = headerSession ?? entry.Session ?? stem;
            // An explicit message id names the call even if an export changes
            // its timestamp. The builder folds replays across files once.
            var identity = entry.Id is { } entryId
                ? $"commandcode:{session}:{entryId}"
                : $"commandcode:{session}:line{entry.Index}:{timestamp.ToUnixTimeSeconds()}";

            yield return new AgentUsageRecord
            {
                Timestamp = timestamp,
                Model = model,
                Tally = tally,
                SessionID = session,
                DeduplicationID = identity,
            };
        }
    }

    /// <summary>The nearest stated model on this reply's own ancestry, not the
    /// last model change in file order. Memoized so long branches stay linear;
    /// missing parents and cycles stop without borrowing a sibling's
    /// model.</summary>
    private static string? AncestorModel(Entry entry, Dictionary<string, Entry> entries, Dictionary<string, string?> cache)
    {
        var visited = new HashSet<string>();
        string? cursor = entry.Parent;
        string? model = null;

        while (cursor is { } id && visited.Add(id))
        {
            if (cache.TryGetValue(id, out var cached))
            {
                model = cached;
                break;
            }
            if (!entries.TryGetValue(id, out var ancestor)) break;
            if (ancestor.Model is { } stated)
            {
                model = stated;
                break;
            }
            cursor = ancestor.Parent;
        }
        if (model is { } resolved)
        {
            foreach (var id in visited) cache[id] = resolved;
        }
        return model;
    }

    private static string? ConfigModel(IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (!File.Exists(root) || Path.GetFileName(root) != "config.json") continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(root));
                return EditorLog.ModelID(Str(document.RootElement, "model"));
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return null;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static JsonElement ObjectOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object) return default;
        return property;
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

    private static DateTimeOffset? TimestampOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property))
            return null;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetInt64(out var millis) && millis > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            case JsonValueKind.String when DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed):
                return parsed;
            default:
                return null;
        }
    }
}

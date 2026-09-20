using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Kiro's CLI session tree, `%USERPROFILE%\.kiro\sessions\cli\`; port of upstream
/// KiroReader. Each `*.json` header is a session and the same stem's `.jsonl` is
/// its conversation.
///
/// Only REAL counters are read: the header's per-turn `input_token_count` /
/// `output_token_count` are the one measured source in Kiro's formats. Every
/// estimate a compatibility target might reach for — a context-window
/// calculation, a character count divided by four, a whole IDE tree, a SQLite
/// store — is deliberately not read here. A turn whose explicit counters are
/// both zero produces NO record: a zero is not a measurement, and estimating one
/// from text would be inventing it. No cache or reasoning counter exists
/// anywhere in Kiro.
/// </summary>
public static class KiroReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".kiro", "sessions", "cli");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoot(root);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoot(string root)
    {
        var records = new List<AgentUsageRecord>();
        try
        {
            // Every .json EXCEPT the .jsonl sidecars (glob *.json alone matches
            // nothing else here; jsonl has its own extension).
            foreach (var headerFile in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (headerFile.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                records.AddRange(ReadHeader(headerFile));
            }
        }
        catch (Exception)
        {
            return records; // an unreadable tree reads short, not fatal
        }
        return records;
    }

    private static IEnumerable<AgentUsageRecord> ReadHeader(string headerFile)
    {
        JsonElement header;
        try
        {
            header = JsonDocument.Parse(File.ReadAllText(headerFile)).RootElement.Clone();
        }
        catch (Exception)
        {
            yield break;
        }
        if (header.ValueKind != JsonValueKind.Object) yield break;

        var session = Str(header, "session_id") ?? Path.GetFileNameWithoutExtension(headerFile);
        var model = ModelID(header) ?? "auto"; // the routing fallback, applied by the caller
        var project = Str(header, "cwd") is { } cwd
            ? Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : null;
        var prompts = PromptTimestamps(beside: headerFile);

        // session_state.conversation_metadata.user_turn_metadatas.
        if (!header.TryGetProperty("session_state", out var state) ||
            state.ValueKind != JsonValueKind.Object ||
            !state.TryGetProperty("conversation_metadata", out var conversation) ||
            conversation.ValueKind != JsonValueKind.Object ||
            !conversation.TryGetProperty("user_turn_metadatas", out var turns) ||
            turns.ValueKind != JsonValueKind.Array)
            yield break;

        var index = 0;
        foreach (var turn in turns.EnumerateArray())
        {
            var position = index++;
            if (turn.ValueKind != JsonValueKind.Object) continue;

            var input = ClampedCount(turn, "input_token_count");
            var output = ClampedCount(turn, "output_token_count");
            // Both zero: the only real counters Kiro has say nothing was
            // measured here, so nothing is emitted.
            if (input <= 0 && output <= 0) continue;

            var prompt = default(DateTimeOffset?);
            if (turn.TryGetProperty("message_ids", out var messageIDs) && messageIDs.ValueKind == JsonValueKind.Array)
            {
                foreach (var messageID in messageIDs.EnumerateArray())
                {
                    if (messageID.ValueKind == JsonValueKind.String &&
                        prompts.TryGetValue(messageID.GetString()!, out var stamp))
                    {
                        prompt = stamp;
                        break; // the earliest prompt for the turn (list order)
                    }
                }
            }

            var end = turn.TryGetProperty("end_timestamp", out var endElement) &&
                      endElement.TryGetDouble(out var seconds) && seconds > 0
                ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))
                : null;
            if (prompt is null && end is null) continue;

            yield return new AgentUsageRecord
            {
                Timestamp = prompt ?? end!.Value,
                Model = model,
                Tally = new TokenTally(Input: input, Output: output),
                SessionID = session,
                Project = project,
                DeduplicationID = $"{session}:{position}",
            };
        }
    }

    /// <summary>Which model the header names, if any; the caller applies "auto".</summary>
    private static string? ModelID(JsonElement header)
    {
        if (header.TryGetProperty("session_state", out var state) &&
            state.ValueKind == JsonValueKind.Object &&
            state.TryGetProperty("rts_model_state", out var rts) &&
            rts.ValueKind == JsonValueKind.Object &&
            rts.TryGetProperty("model_info", out var info) &&
            info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("model_id", out var modelID) &&
            modelID.ValueKind == JsonValueKind.String)
            return modelID.GetString();
        return null;
    }

    /// <summary>The conversation sidecar's prompt timestamps by message id. A
    /// prompt's own .jsonl time is the accurate one; end_timestamp is the
    /// fallback for a turn whose prompt line is missing.</summary>
    private static Dictionary<string, DateTimeOffset> PromptTimestamps(string beside)
    {
        var times = new Dictionary<string, DateTimeOffset>();
        var sidecar = Path.ChangeExtension(beside, ".jsonl");
        if (!File.Exists(sidecar)) return times;

        string[] lines;
        try { lines = File.ReadAllLines(sidecar); }
        catch (IOException) { return times; }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (document)
            {
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (Str(row, "kind") != "Prompt") continue;
                if (!row.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                var message = Str(data, "message_id");
                if (message is null ||
                    !data.TryGetProperty("meta", out var meta) ||
                    meta.ValueKind != JsonValueKind.Object ||
                    !meta.TryGetProperty("timestamp", out var timestampElement) ||
                    !timestampElement.TryGetDouble(out var seconds) ||
                    seconds <= 0)
                    continue;
                times[message] = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
            }
        }
        return times;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int ClampedCount(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => Math.Max(i, 0),
            JsonValueKind.Number => Math.Max((int)property.GetDouble(), 0),
            _ => 0,
        };
    }
}

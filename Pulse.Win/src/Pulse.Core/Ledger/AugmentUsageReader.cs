using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Augment's per-session chat history; port of upstream AugmentUsageReader.
///
/// `~/.augment/sessions/&lt;sessionId&gt;.json` holds `sessionId`,
/// `agentState.modelId` and `chatHistory[]`. Each turn is
/// `{ "finishedAt", "completed", "sequenceId", "exchange": { "model_id",
/// "request_id", "response_nodes": [{ "token_usage": {...} }] } }`.
///
/// **Only completed turns count.** An aborted or in-progress turn can carry a
/// partial streamed total; emitting it would put a snapshot of work that never
/// finished into the ledger.
///
/// **The last non-empty `token_usage` wins, never the sum.** A turn's response
/// is streamed as several nodes whose usage is cumulative; adding them would
/// multiply the turn. The final node that reported anything is the turn's own
/// total.
///
/// Input and cache are independent here — the format does not fold one into
/// the other — so they are read straight across. Augment credits are a cost,
/// not a kind, and are left for the price table. `finishedAt` is the only time
/// the turn has, so timing is end-anchored and there is no duration.
/// </summary>
public static class AugmentUsageReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".augment", "sessions");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoots(new[] { root });
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
                files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                JsonDocument document;
                try { document = JsonDocument.Parse(File.ReadAllText(file)); }
                catch (Exception) { continue; }
                using (document)
                {
                    var session = document.RootElement;
                    if (session.ValueKind != JsonValueKind.Object) continue;
                    ReadSession(session, Path.GetFileNameWithoutExtension(file), records);
                }
            }
        }
        return records;
    }

    private static void ReadSession(JsonElement session, string fallbackSessionID, List<AgentUsageRecord> records)
    {
        var sessionID = Str(session, "sessionId") ?? fallbackSessionID;
        var agentModel = session.TryGetProperty("agentState", out var agentState) &&
                         agentState.ValueKind == JsonValueKind.Object
            ? Str(agentState, "modelId")
            : null;

        if (!session.TryGetProperty("chatHistory", out var history) ||
            history.ValueKind != JsonValueKind.Array)
            return;

        var index = 0;
        foreach (var turn in history.EnumerateArray())
        {
            var position = index++;
            if (turn.ValueKind != JsonValueKind.Object) continue;

            // `completed` must be exactly true; absent or false is not a
            // finished turn.
            if (turn.TryGetProperty("completed", out var completed) &&
                completed.ValueKind != JsonValueKind.True) continue;
            if (!turn.TryGetProperty("exchange", out var exchange) ||
                exchange.ValueKind != JsonValueKind.Object)
                continue;
            if (LastUsage(exchange) is not { } usage) continue;
            if (EventTime(turn, "finishedAt") is not { } timestamp) continue;

            var tally = new TokenTally(
                Input: CountOf(usage, "input_tokens"),
                CacheWrite: CountOf(usage, "cache_creation_input_tokens"),
                CacheRead: CountOf(usage, "cache_read_input_tokens"),
                Output: CountOf(usage, "output_tokens"));

            var model = Str(exchange, "model_id") ?? agentModel;
            var identity = Str(exchange, "request_id")
                ?? Str(turn, "sequenceId")
                ?? position.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (tally.Total <= 0) continue;
            records.Add(new AgentUsageRecord
            {
                Timestamp = timestamp,
                Model = model!,
                Tally = tally,
                SessionID = sessionID,
                SessionName = sessionID,
                DeduplicationID = $"augment:{sessionID}:{identity}",
            });
        }
    }

    /// <summary>The last `response_nodes` entry whose `token_usage` reports
    /// anything: streamed nodes are cumulative, so the last is the total.</summary>
    private static JsonElement? LastUsage(JsonElement exchange)
    {
        if (!exchange.TryGetProperty("response_nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var node in nodes.EnumerateArray().Reverse())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("token_usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
                continue;
            var total = CountOf(usage, "input_tokens")
                + CountOf(usage, "cache_creation_input_tokens")
                + CountOf(usage, "cache_read_input_tokens")
                + CountOf(usage, "output_tokens");
            if (total > 0) return usage;
        }
        return null;
    }

    private static int CountOf(JsonElement element, string name) =>
        EditorLog.FirstCount(element, name) ?? 0;

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String && property.GetString() is { } value && value.Length > 0
            ? value
            : null;
    }

    /// <summary>A real event time is strictly after the epoch; a zero is a
    /// store's "not set" that slipped through as 1970.</summary>
    private static DateTimeOffset? EventTime(JsonElement turn, string name)
    {
        if (!turn.TryGetProperty(name, out var property)) return null;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
            case JsonValueKind.String when property.GetString() is { } text:
            {
                if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
                if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) &&
                    parsed.ToUnixTimeMilliseconds() > 0)
                    return parsed;
                return null;
            }
            default:
                return null;
        }
    }

}

using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// JetBrains Junie's event stream; port of upstream JunieUsageReader.
/// `~/.junie/sessions/&lt;session-id&gt;/events.jsonl`; a usage event is one whose
/// `event.agentEvent.kind` is `LlmResponseMetadataEvent` and which holds a
/// `modelUsage[]` — every entry is one provider call.
///
/// The timestamp is anchored to the RESPONSE'S START: timestampMs is the end and
/// `usage.time` is the latency, so a row with a positive latency is dated at
/// timestampMs − time. A zero millisecond time is "unset", not 1970 — the record
/// falls back to the session id's own `session-YYMMDD-HHMMSS` date. Neither the
/// clock nor the file's date is used.
///
/// Reasoning is LEFT OUT: reasoningTokens / reasoningOutputTokens /
/// thinkingTokens are listed beside output with no statement of containment, so
/// adding one would double it and carrying it as unclassified would inflate the
/// grand total the same way. The reported output is kept and the ambiguous
/// reasoning marks the record partial. Cost is the product's own dollars and is
/// never read as tokens.
/// </summary>
public static class JunieUsageReader
{
    private static readonly string[] InputAliases = ["inputTokens", "input"];
    private static readonly string[] OutputAliases = ["outputTokens", "output"];
    private static readonly string[] CacheReadAliases = ["cacheInputTokens", "cacheReadInputTokens", "cacheRead"];
    private static readonly string[] CacheWriteAliases = ["cacheCreateTokens", "cacheCreationInputTokens", "cacheWrite"];
    private static readonly string[] ReasoningAliases = ["reasoningTokens", "reasoningOutputTokens", "thinkingTokens"];

    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".junie", "sessions");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoot(root);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoot(string root)
    {
        var records = new List<AgentUsageRecord>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "events.jsonl", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var sessionId = Path.GetFileName(Path.GetDirectoryName(file));
                records.AddRange(ParseFile(file, sessionId));
            }
        }
        catch (Exception)
        {
            return records; // an unreadable tree reads short, not fatal
        }
        return records;
    }

    private static IEnumerable<AgentUsageRecord> ParseFile(string file, string? sessionId)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (IOException) { yield break; }

        // The session id encodes its own start: the real fallback when the
        // event's timestampMs is unset (0).
        var sessionStart = SessionIDTime(sessionId);

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(raw); }
            catch (JsonException) { continue; }

            using (document)
            {
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!row.TryGetProperty("event", out var @event) || @event.ValueKind != JsonValueKind.Object) continue;
                if (!@event.TryGetProperty("agentEvent", out var agentEvent) || agentEvent.ValueKind != JsonValueKind.Object) continue;
                if (Str(agentEvent, "kind") != "LlmResponseMetadataEvent") continue;
                if (!agentEvent.TryGetProperty("modelUsage", out var usages) || usages.ValueKind != JsonValueKind.Array) continue;

                // A zero millisecond time is "unset", not 1970.
                long? endMillis = row.TryGetProperty("timestampMs", out var tsElement) &&
                                  tsElement.ValueKind == JsonValueKind.Number &&
                                  tsElement.TryGetInt64(out var tsValue) && tsValue > 0
                    ? tsValue
                    : null;
                var end = endMillis is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;

                var agent = ObjectOf(agentEvent, "agent");
                var agentName = Str(agent, "name") ?? Str(agent, "id");

                var entryIndex = 0;
                foreach (var entry in usages.EnumerateArray())
                {
                    var position = entryIndex++;
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    var model = Str(entry, "model");
                    if (model is null) continue;

                    var latency = IntOf(entry, "time");
                    if (EventTime(end, latency, sessionStart) is not { } timestamp) continue;

                    var reasoning = Merged(entry, ReasoningAliases);
                    var tally = new TokenTally(
                        Input: Merged(entry, InputAliases),
                        CacheWrite: Merged(entry, CacheWriteAliases),
                        CacheRead: Merged(entry, CacheReadAliases),
                        // Reasoning is left out: containment undeclared.
                        Output: Merged(entry, OutputAliases));

                    var identity = $"{sessionId}:{timestamp.ToUnixTimeSeconds()}:{model}:"
                        + $"{tally.Input}:{tally.CacheWrite}:{tally.CacheRead}:{tally.Output}:"
                        + $"{IntOf(entry, "cost")}:{position}";

                    yield return new AgentUsageRecord
                    {
                        Timestamp = timestamp,
                        Model = model,
                        Tally = tally,
                        IsPartial = reasoning > 0,
                        SessionID = sessionId ?? "",
                        SessionName = agentName,
                        DeduplicationID = $"junie:{identity}",
                    };
                }
            }
        }
    }

    /// <summary>The call's start when the response end is known, else the
    /// session's own start. Always a real, positive time: a latency that would
    /// place the start at or before the epoch is not used.</summary>
    public static DateTimeOffset? EventTime(DateTimeOffset? end, int latency, DateTimeOffset? fallback)
    {
        if (end is null) return fallback;
        if (latency <= 0) return end;
        var start = end.Value.AddMilliseconds(-latency);
        return start.ToUnixTimeMilliseconds() > 0 ? start : end;
    }

    /// <summary>`session-YYMMDD-HHMMSS` in local time → its start, or nil.</summary>
    public static DateTimeOffset? SessionIDTime(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var marker = id.IndexOf("session-", StringComparison.Ordinal);
        if (marker < 0) return null;
        var stamp = id[(marker + "session-".Length)..];
        if (stamp.Length < 13) return null;
        stamp = stamp[..13];
        if (!DateTime.TryParseExact(stamp, "yyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return null;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static JsonElement ObjectOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
            return default;
        return property;
    }

    private static int Merged(JsonElement entry, string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (entry.ValueKind == JsonValueKind.Object &&
                entry.TryGetProperty(alias, out var property) &&
                property.ValueKind == JsonValueKind.Number)
            {
                if (property.TryGetInt32(out var value)) return Math.Max(value, 0);
            }
        }
        return 0;
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

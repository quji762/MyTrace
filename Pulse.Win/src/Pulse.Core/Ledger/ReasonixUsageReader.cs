using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Reasonix's daily stats files; port of upstream ReasonixUsageReader.
///
/// `~/.reasonix/stats/YYYY-MM-DD.jsonl` (`$REASONIX_STATE_HOME`, or
/// `$REASONIX_HOME\stats`) holds one aggregate per request group: `{ "ts",
/// "model", "prompt", "completion", "reasoning", "cache_hit", "cache_miss",
/// "total", "requests", "turn" }`. The session transcript is deliberately not
/// scanned: it has no authoritative counters and would overlap these.
///
/// **Only real, non-empty rows count.** A `turn == true` marker, an empty
/// model, and a row whose `total` and `requests` are both non-positive are all
/// skipped — the first is not a call, the second names nothing, and the third
/// is a row that reported nothing.
///
/// **Cache hit is the read, cache miss the fresh input.** When `cache_miss` is
/// present and positive it is the fresh input; otherwise it is `prompt −
/// cache_hit`. **Reasoning is a subset of completion** — the format reports it
/// as a count within the completion — so the completion is kept whole as
/// output; subtracting reasoning and counting it a second time would drop real
/// output or double it. There is no cache-write figure in this format, so it
/// is zero.
///
/// `model` is written as `provider/model` and is kept exactly as reported; the
/// provider prefix is not stripped, because guessing which spelling a price
/// table wants is not this reader's job.
/// </summary>
public static class ReasonixUsageReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(
        string? userProfile = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? EnvironmentValue(string name) =>
            environment is null ? Environment.GetEnvironmentVariable(name) : environment.GetValueOrDefault(name);

        string root;
        if (EnvironmentValue("REASONIX_STATE_HOME") is { } state && state.Length > 0)
            root = state;
        else if (EnvironmentValue("REASONIX_HOME") is { } baseDir && baseDir.Length > 0)
            root = Path.Combine(baseDir, "stats");
        else
        {
            var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = Path.Combine(home, ".reasonix", "stats");
        }
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
                files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                var path = Path.GetFileName(file);
                var lineIndex = 0;
                foreach (var raw in File.ReadLines(file))
                {
                    lineIndex++;
                    JsonElement row;
                    try { row = JsonDocument.Parse(raw).RootElement; }
                    catch (JsonException) { continue; }
                    if (row.ValueKind != JsonValueKind.Object) continue;

                    // `turn` is a marker, not a call.
                    if (row.TryGetProperty("turn", out var turn) &&
                        turn.ValueKind == JsonValueKind.True) continue;
                    if (Str(row, "model") is not { } model) continue;

                    var total = EditorLog.FirstCount(row, "total");
                    var requests = EditorLog.FirstCount(row, "requests");
                    if ((total ?? 0) <= 0 && (requests ?? 0) <= 0) continue;
                    if (EventTime(row, "ts") is not { } timestamp) continue;

                    var prompt = EditorLog.FirstCount(row, "prompt");
                    var completion = EditorLog.FirstCount(row, "completion");
                    var cacheRead = EditorLog.FirstCount(row, "cache_hit") ?? 0;

                    TokenTally tally;
                    int unclassified;
                    if (prompt is null && completion is null)
                    {
                        // A bare total: real, but with no split to place it in.
                        tally = new TokenTally();
                        unclassified = total ?? 0;
                    }
                    else
                    {
                        var miss = EditorLog.FirstCount(row, "cache_miss") ?? 0;
                        var input = miss > 0 ? miss : Math.Max(0, (prompt ?? 0) - cacheRead);
                        // Reasoning is reported as a count within the
                        // completion: the completion is kept whole.
                        tally = new TokenTally(
                            Input: input,
                            CacheWrite: 0,
                            CacheRead: cacheRead,
                            Output: Math.Max(0, completion ?? 0));
                        unclassified = 0;
                    }

                    if (tally.Total <= 0 && unclassified <= 0) continue;
                    records.Add(new AgentUsageRecord
                    {
                        Timestamp = timestamp,
                        Model = model,
                        Tally = tally,
                        SessionID = $"reasonix-stats:{path}",
                        SessionName = "reasonix",
                        DeduplicationID = $"reasonix:{path}:{lineIndex}:{requests ?? 0}:{total ?? 0}",
                        UnclassifiedTokens = unclassified,
                    });
                }
            }
        }
        return records;
    }

    /// <summary>A real event time is strictly after the epoch; a zero is a
    /// store's "not set" that slipped through as 1970.</summary>
    private static DateTimeOffset? EventTime(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var property)) return null;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
            case JsonValueKind.String when property.GetString() is { } text:
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) &&
                    parsed.ToUnixTimeMilliseconds() > 0)
                    return parsed;
                return null;
            }
            default:
                return null;
        }
    }

    private static string? Str(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String && property.GetString() is { } value && value.Length > 0
            ? value
            : null;
    }
}

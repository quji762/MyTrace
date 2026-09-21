using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// OpenCodeReview's session logs; port of upstream OpenCodeReviewReader.
/// One JSONL per session under `~/.opencodereview/sessions/&lt;encoded-repo&gt;/`. A
/// `session_start` line names the working directory and default model; each
/// `llm_response` carries the response's own usage.
///
/// The cache relation is NOT assumed: the persisted `llm_response.usage` writes
/// only prompt/completion/cache-read/cache-write — no total and no record of
/// which provider path the cache came from, and a bare sum could bill the same
/// tokens twice. The store's own total, where a variant carries one, settles it:
/// - total == disjoint sum → the four kinds are disjoint, counted as reported;
/// - total == prompt + completion → prompt already contains the cache (subtract);
/// - any other total → unclassifiedTokens with an empty tally, not a guessed split;
/// - NO total and a positive cache → no record at all (unprovable); remaining
///   readable records are marked isPartial;
/// - no total and no cache → counted as reported.
///
/// A llm_response line carries a real uuid; a replayed uuid folds once while two
/// equal requests in one second stay two. A line with no uuid falls back to the
/// FILE'S CONTENT DIGEST plus its line position, so a whole-file mirror folds
/// but two different files sharing a session id are both kept.
/// </summary>
public static class OpenCodeReviewReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".opencodereview", "sessions");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoots(new[] { root });
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var seen = new HashSet<string>();
        var records = new List<AgentUsageRecord>();
        var skippedUnprovable = false;

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
                var parsed = Parse(file);
                skippedUnprovable |= parsed.SkippedUnprovable;
                foreach (var record in parsed.Records)
                {
                    if (record.DeduplicationID is { } id && !seen.Add(id)) continue;
                    records.Add(record);
                }
            }
        }

        // A recognized usage was dropped because its cache relation could not
        // be proven; what remains is a confirmed subset, not the whole.
        return skippedUnprovable ? records.Select(MarkPartial).ToList() : records;
    }

    private static (IReadOnlyList<AgentUsageRecord> Records, bool SkippedUnprovable) Parse(string file)
    {
        var fragment = Digest(file);
        if (fragment is null) return (Array.Empty<AgentUsageRecord>(), false);

        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (IOException) { return (Array.Empty<AgentUsageRecord>(), false); }

        // The file's own digest is a deterministic fragment identity: a
        // byte-identical mirror shares it, two different files do not.
        var stem = Path.GetFileNameWithoutExtension(file);

        string? sessionFromStart = null;
        string? cwd = null;
        string? startModel = null;
        foreach (var line in lines)
        {
            if (TryObject(line) is not { } scan || Str(scan, "type") != "session_start") continue;
            sessionFromStart = NonBlank(Str(scan, "sessionId")) ?? sessionFromStart;
            cwd = NonBlank(Str(scan, "cwd")) ?? cwd;
            startModel = EditorLog.ModelID(Str(scan, "model")) ?? startModel;
        }

        var records = new List<AgentUsageRecord>();
        var skippedUnprovable = false;

        var index = -1;
        foreach (var raw in lines)
        {
            index++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (TryObject(raw) is not { } row) continue;
            if (Str(row, "type") != "llm_response") continue;
            if (TimestampOf(row, "timestamp") is not { } end) continue;
            if (!row.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;

            var reduction = Parts(usage);
            if (reduction.Unprovable) { skippedUnprovable = true; continue; }
            var parts = reduction.Parts;
            if (parts.Tally.Total + parts.Unclassified <= 0) continue;

            // A real end timestamp plus a positive duration puts the request's
            // START at the end minus the duration.
            var duration = OptInt(row, "duration_ms") ?? 0;
            var start = duration > 0 ? end.AddMilliseconds(-duration) : end;

            var session = NonBlank(Str(row, "sessionId")) ?? sessionFromStart ?? stem;
            var model = EditorLog.ModelID(Str(row, "model")) ?? startModel;
            if (model is null) continue;

            var identity = NonBlank(Str(row, "uuid")) is { } uuid
                ? $"opencodereview:{session}:uuid:{uuid}"
                : $"opencodereview:{session}:fragment:{fragment}:line:{index}";

            records.Add(new AgentUsageRecord
            {
                Timestamp = start,
                Model = model,
                Tally = parts.Tally,
                SessionID = session,
                Project = cwd is null ? null : Path.GetFileName(cwd.TrimEnd('/', '\\')),
                DeduplicationID = identity,
                UnclassifiedTokens = parts.Unclassified,
            });
        }
        return (records, skippedUnprovable);
    }

    /// <summary>One usage object reduced, or a flag that it cannot be split.</summary>
    public static (EditorLog.UsageParts Parts, bool Unprovable) Parts(JsonElement usage)
    {
        var prompt = EditorLog.FirstCount(usage, "prompt_tokens", "promptTokens");
        var completion = EditorLog.FirstCount(usage, "completion_tokens", "completionTokens");
        var cacheRead = EditorLog.FirstCount(usage, "cache_read_tokens", "cacheReadTokens");
        var cacheWrite = EditorLog.FirstCount(usage, "cache_write_tokens", "cacheWriteTokens");
        var total = EditorLog.FirstCount(usage, "total_tokens", "totalTokens", "total");

        var namesAKind = prompt is not null || completion is not null || cacheRead is not null || cacheWrite is not null;
        if (!namesAKind && total is { } bare && bare > 0)
            return (new EditorLog.UsageParts(new TokenTally(), bare), false);

        var promptValue = prompt ?? 0;
        var completionValue = completion ?? 0;
        var cacheReadValue = cacheRead ?? 0;
        var cacheWriteValue = cacheWrite ?? 0;
        var disjoint = promptValue + completionValue + cacheReadValue + cacheWriteValue;

        // A total of zero is not a usable total; it is treated as absent.
        if (total is { } totalCount && totalCount > 0)
        {
            if (totalCount == disjoint)
            {
                // The total names every kind: the four are disjoint.
                return (new EditorLog.UsageParts(new TokenTally(
                    Input: promptValue, CacheWrite: cacheWriteValue,
                    CacheRead: cacheReadValue, Output: completionValue)), false);
            }
            if (totalCount == promptValue + completionValue && totalCount != disjoint)
            {
                // The total is only prompt + completion: prompt already contains
                // the cache counts.
                return (new EditorLog.UsageParts(new TokenTally(
                    Input: Math.Max(0, promptValue - cacheReadValue - cacheWriteValue),
                    CacheWrite: cacheWriteValue, CacheRead: cacheReadValue,
                    Output: completionValue)), false);
            }
            // The total fits neither shape: keep it whole rather than price a
            // split that could double count.
            return (new EditorLog.UsageParts(new TokenTally(), totalCount), false);
        }

        // No total to settle the overlap: with a positive cache the split is
        // unproven and no complete-looking number may be produced.
        if (cacheReadValue != 0 || cacheWriteValue != 0)
            return (new EditorLog.UsageParts(new TokenTally()), true);
        return (new EditorLog.UsageParts(new TokenTally(Input: promptValue, Output: completionValue)), false);
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

    private static JsonElement? TryObject(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var element = JsonDocument.Parse(line).RootElement;
            return element.ValueKind == JsonValueKind.Object ? element.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AgentUsageRecord MarkPartial(AgentUsageRecord record) => record with { IsPartial = true };

    private static string? Digest(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return null;
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..32];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? OptInt(JsonElement element, string name)
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

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

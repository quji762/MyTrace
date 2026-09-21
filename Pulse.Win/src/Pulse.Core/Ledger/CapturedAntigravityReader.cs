using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// The Antigravity IDE cache: one JSON object per line, written by pulling
/// usage from a running Antigravity language server; port of upstream
/// CapturedAntigravityReader. Not a native local usage log — the cache comes
/// from an authenticated sync against the language server; the cache is read
/// rather than the server. Not to be confused with `antigravity-cli`, a
/// different product with its own conversation databases.
///
/// `session_meta` supplies a fallback model for the `usage` rows that follow;
/// a row with no model and no fallback is skipped, because a token count with
/// no model cannot be priced and must not be filed under an invented name.
/// Placeholder ids (`model_placeholder_`) name nothing resolvable without the
/// sync's own alias table and are skipped rather than passed on as a price key
/// that could collide.
///
/// Reasoning is NOT silently folded into output: this schema does not state
/// whether `reasoning` is already part of `output`, so output stays exactly as
/// reported, reasoning is not placed in any bucket, and the record is marked
/// `isPartial` so the ambiguity is visible. Negative counts clamp to zero —
/// this format states every bucket, so an absent one is a real zero rather
/// than an unknown. The response id is the cache's own identity for the call; a
/// re-sync of the same response folds here.
/// </summary>
public static class CapturedAntigravityReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".antigravity", "cache");
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
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (IOException) { continue; }
                records.AddRange(ParseLines(lines));
            }
        }
        return records;
    }

    public static IReadOnlyList<AgentUsageRecord> ParseLines(string[] lines)
    {
        var records = new List<AgentUsageRecord>();
        string? fallbackModel = null;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(raw); }
            catch (JsonException) { continue; }

            using (document)
            {
                var line = document.RootElement;
                if (line.ValueKind != JsonValueKind.Object) continue;
                var type = Str(line, "type");
                if (type is null) continue;

                if (type == "session_meta")
                {
                    fallbackModel = Str(line, "modelId") ?? fallbackModel;
                    continue;
                }
                if (type != "usage") continue;

                if (Str(line, "sessionId") is not { } session) continue;
                if (TimestampOf(line, "timestamp") is not { } at) continue;

                var model = NonBlank(Str(line, "modelId")) ?? NonBlank(fallbackModel);
                if (model is null || IsPlaceholder(model)) continue;

                // Negative counts clamp to zero; an all-zero row asserts no
                // usage and is dropped.
                var tally = new TokenTally(
                    Input: Clamp(line, "input"),
                    CacheWrite: Clamp(line, "cacheWrite"),
                    CacheRead: Clamp(line, "cacheRead"),
                    Output: Clamp(line, "output"));
                if (tally.Total <= 0) continue;

                var reasoning = Clamp(line, "reasoning");
                var record = new AgentUsageRecord
                {
                    Timestamp = at,
                    Model = model,
                    Tally = tally,
                    SessionID = session,
                    // A separate reasoning figure may or may not already be
                    // inside output: neither added nor counted.
                    IsPartial = reasoning > 0,
                    DeduplicationID = Str(line, "responseId") is { } response
                        ? $"antigravity:{response}"
                        : null,
                };
                records.Add(record);
            }
        }
        return records;
    }

    private static int Clamp(JsonElement element, string name)
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

    /// <summary>Placeholder ids name no model resolvable without the sync's own
    /// alias table, so they are skipped rather than passed on as a price key
    /// that could collide.</summary>
    public static bool IsPlaceholder(string model) =>
        model.ToLowerInvariant().StartsWith("model_placeholder_", StringComparison.Ordinal);

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTimeOffset? TimestampOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property))
            return null;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetInt64(out var millis) && millis > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            case JsonValueKind.String when long.TryParse(property.GetString(), out var millis) && millis > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            default:
                return null;
        }
    }
}

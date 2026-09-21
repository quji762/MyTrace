using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Trae's usage cache: a raw JSON ARRAY dumped from the Trae usage API; port of
/// upstream CapturedTraeReader. There is no native local file — the array is an
/// export needing an authenticated sync; Pulse reads the dumped array whatever
/// its file name, as soon as the root parses as an array.
///
/// `model_name` is empty when the system picked a model per turn (Auto mode),
/// in which case the row is placed under `trae-&lt;mode&gt;`; the true per-turn
/// model is not recoverable. `extra_info` carries the four real counters;
/// anything else there is left alone — a counter with no stated meaning is not
/// a cache-read count.
///
/// Equal rows are NOT merged, but overlapping pages are: the dump has no
/// per-row id, so two rows in one second inside one page are left as two (they
/// can be two requests). Across pages the same row is reconciled by content
/// multiset — {A} and {A,B} give A and B, never two As. An unconfirmed
/// increment inside a shared region marks the scope partial; a byte-identical
/// page folds outright.
/// </summary>
public static class CapturedTraeReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".trae", "captures");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoots(new[] { root });
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal));
            }
            catch (Exception) { }
        }

        // A byte-identical re-dump is one page; two rows inside one page still are two.
        var distinct = new List<string>();
        var seen = new HashSet<string>();
        foreach (var file in files)
        {
            if (CapturedSupport.Digest(file) is not { } digest) continue;
            if (seen.Add(digest)) distinct.Add(file);
        }

        var perFile = new List<IReadOnlyList<AgentUsageRecord>>();
        foreach (var file in distinct)
        {
            IReadOnlyList<AgentUsageRecord> page;
            try
            {
                page = ParsePage(file);
            }
            catch (Exception) { continue; } // a non-array root yields nothing
            perFile.Add(page);
        }

        var reconciled = CapturedSupport.Reconcile(perFile, Signature);
        // An unconfirmed increment inside a shared region is reported as partial
        // rather than added.
        return reconciled.Overlapped
            ? reconciled.Records.Select(r => r with { IsPartial = true }).ToList()
            : reconciled.Records.ToList();
    }

    /// <summary>A row's content identity, used only to reconcile overlapping
    /// pages — never to fold two rows of one page together.</summary>
    private static string Signature(AgentUsageRecord record) =>
        $"{record.SessionID ?? ""}:{record.Timestamp.ToUnixTimeSeconds()}:{record.Model}:"
        + $"{record.Tally.Input}:{record.Tally.CacheWrite}:{record.Tally.CacheRead}:{record.Tally.Output}";

    private static IReadOnlyList<AgentUsageRecord> ParsePage(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        // A non-array root is not this format and yields nothing.
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<AgentUsageRecord>();

        var records = new List<AgentUsageRecord>();
        foreach (var session in document.RootElement.EnumerateArray())
        {
            if (session.ValueKind != JsonValueKind.Object) continue;
            var sessionID = Str(session, "session_id");
            if (sessionID is null) continue;

            // usage_time is epoch seconds by this schema; non-positive is unusable.
            if (!session.TryGetProperty("usage_time", out var usageTime) ||
                !usageTime.TryGetInt64(out var seconds) || seconds <= 0) continue;
            var at = DateTimeOffset.FromUnixTimeSeconds(seconds);

            if (!session.TryGetProperty("extra_info", out var extra) || extra.ValueKind != JsonValueKind.Object) continue;
            var tally = new TokenTally(
                Input: IntOf(extra, "input_token"),
                CacheWrite: IntOf(extra, "cache_write_token"),
                CacheRead: IntOf(extra, "cache_read_token"),
                Output: IntOf(extra, "output_token"));
            // All-zero totals assert no usage.
            if (tally.Total <= 0) continue;

            records.Add(new AgentUsageRecord
            {
                Timestamp = at,
                Model = Normalized(ModelName(session)),
                Tally = tally,
                SessionID = sessionID,
                // The session id names the session; it is NOT a row identity and
                // never becomes a deduplication id.
            });
        }
        return records;
    }

    private static string ModelName(JsonElement session)
    {
        if (Str(session, "model_name") is { } name) return name;
        if (Str(session, "mode") is { } mode) return $"trae-{mode}";
        return "trae-unknown";
    }

    /// <summary>A small, fixed display-name table. Ids are the provider's own
    /// where a known name maps to one; an unrecognised name passes through
    /// unchanged so it can be counted and reported as unpriced rather than
    /// disguised.</summary>
    public static string Normalized(string name)
    {
        var trimmed = name.Trim();
        var lower = trimmed.ToLowerInvariant();

        if (lower.StartsWith("gpt-5")) return Spaced(lower);
        if (lower.StartsWith("gemini 3.1") || lower.StartsWith("gemini-3.1")) return Spaced(lower);
        if (lower is "glm 5.1" or "glm-5.1") return "glm-5.1";
        // Anthropic spells the version with dashes: Claude Sonnet 4.5 -> claude-sonnet-4-5.
        if (lower.StartsWith("claude sonnet 4.5")) return Spaced(lower.Replace("4.5", "4-5"));
        if (lower.StartsWith("claude sonnet 4.6")) return Spaced(lower.Replace("4.6", "4-6"));
        return trimmed;
    }

    private static string Spaced(string value) =>
        string.Join("-", value.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries));

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

using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Shared conversions for the Captured* readers; port of the parts of upstream
/// CapturedSupport those readers use. Kept in one place so two readers cannot
/// disagree about what a slug is or when two files are byte-identical.
/// </summary>
public static class CapturedSupport
{
    /// <summary>An ASCII slug for a product's own account or workspace label:
    /// only ASCII letters and digits survive, other runs collapse to one `-`,
    /// lowercased, dashes trimmed. Nil when nothing survives.</summary>
    public static string? Slug(string value)
    {
        var output = new System.Text.StringBuilder();
        var pendingDash = false;
        foreach (var c in value)
        {
            var ascii = (int)c;
            var keep = ascii is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
            if (keep)
            {
                if (pendingDash && output.Length > 0) output.Append('-');
                pendingDash = false;
                output.Append(c);
            }
            else if (output.Length > 0)
            {
                pendingDash = true;
            }
        }
        return output.Length == 0 ? null : output.ToString().ToLowerInvariant();
    }

    /// <summary>A SHA-256 content digest: file replay (not row identity) — it
    /// only collapses files whose bytes are identical.</summary>
    public static string? Digest(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return null;
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The multiset union of records read from several files of ONE scope; port
    /// of upstream CapturedSupport.reconcile. Two exports {A} and {A,B} yield A
    /// and B, never two As: a row found in more than one file is kept once per
    /// the most any single file holds. Two equal rows INSIDE one file are kept
    /// (that file itself says there were two). `overlapped` is true when a row
    /// appeared in more than one file — the increment inside the shared region
    /// cannot be confirmed, so the caller marks the scope partial.
    /// </summary>
    public static (IReadOnlyList<AgentUsageRecord> Records, bool Overlapped) Reconcile(
        IEnumerable<IReadOnlyList<AgentUsageRecord>> files, Func<AgentUsageRecord, string> signature)
    {
        var order = new List<string>();
        var representative = new Dictionary<string, AgentUsageRecord>();
        var maximum = new Dictionary<string, int>();
        var total = new Dictionary<string, int>();

        foreach (var records in files)
        {
            var perFile = new Dictionary<string, int>();
            foreach (var record in records)
            {
                var key = signature(record);
                perFile[key] = perFile.GetValueOrDefault(key) + 1;
                if (!representative.ContainsKey(key))
                {
                    representative[key] = record;
                    order.Add(key);
                }
            }
            foreach (var (key, count) in perFile)
            {
                maximum[key] = Math.Max(maximum.GetValueOrDefault(key), count);
                total[key] = total.GetValueOrDefault(key) + count;
            }
        }

        var outRecords = new List<AgentUsageRecord>();
        var overlapped = false;
        foreach (var key in order)
        {
            var emit = maximum.GetValueOrDefault(key);
            if (total.GetValueOrDefault(key) > emit) overlapped = true;
            if (representative.TryGetValue(key, out var record))
            {
                for (var i = 0; i < emit; i++) outRecords.Add(record);
            }
        }
        return (outRecords, overlapped);
    }
}

/// <summary>
/// The Hindsight ledger: a JSONL mirror of a self-hosted memory service's
/// `llm-requests`; port of upstream CapturedHindsightReader. The service's own
/// table is a rolling window, so the durable copy is this JSONL; the ledger is
/// read, never the service.
///
/// A user-edited file is untrusted: a line that will not decode, a row with no
/// id/started_at, a stated total asserting no usage, or a row with neither
/// input nor output is skipped rather than guessed at. `cached_tokens` is the
/// cache-read bucket (null in practice on the Ollama path); cache write is not
/// reported; reasoning is never split out on this path so output stays as
/// stated.
/// </summary>
public static class CapturedHindsightReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".hindsight", "ledger");
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

                        // The id is the ledger's dedup identity; without it a
                        // re-synced row cannot be folded, so it is required.
                        var id = Str(line, "id");
                        var model = Str(line, "model");
                        var at = HindsightTimestamp(line, "started_at");
                        if (id is null || model is null || at is null) continue;

                        // A stated total of zero or less asserts no usage.
                        if (OptInt(line, "total_tokens") is { } total && total <= 0) continue;

                        var input = OptInt(line, "input_tokens") ?? 0;
                        var output = OptInt(line, "output_tokens") ?? 0;
                        if (input <= 0 && output <= 0) continue;

                        records.Add(new AgentUsageRecord
                        {
                            Timestamp = at.Value,
                            Model = model,
                            Tally = new TokenTally(
                                Input: input,
                                CacheWrite: 0, // not reported on this path
                                CacheRead: OptInt(line, "cached_tokens") ?? 0,
                                Output: output), // completion already includes reasoning
                            SessionID = Str(line, "trace_id") ?? id,
                            Project = Str(line, "bank"),
                            Title = Title(Str(line, "operation"), Str(line, "scope")),
                            DeduplicationID = $"hindsight:{id}",
                        });
                    }
                }
            }
        }
        return records;
    }

    /// <summary>`operation / scope` where both are present and differ, otherwise
    /// whichever one the ledger stated.</summary>
    public static string? Title(string? operation, string? scope)
    {
        return (operation, scope) switch
        {
            ({ } op, { } sc) => op == sc ? op : $"{op} / {sc}",
            ({ } op, null) => op,
            (null, { } sc) => sc,
            _ => null,
        };
    }

    private static DateTimeOffset? HindsightTimestamp(JsonElement element, string name)
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

    private static int? OptInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => i,
            _ => null,
        };
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }
}

/// <summary>
/// A captured MiniMax Code headless stream (`mcode exec --output-format
/// stream-json`); port of upstream CapturedMcodeReader. There is no native
/// local file — a user or wrapper captures the output; Pulse only reads it.
///
/// Usage is buffered per `turnId` and emitted only when a matching
/// `exec.result` supplies the model; a stream that never names a model is
/// unusable, and none is guessed. A message is merged only by its own identity
/// (id/messageId/responseId), never by its counts: no id means every line is
/// its own message, even with identical counts. A leading BOM or one bad byte
/// does not lose the rest of the capture.
/// </summary>
public static class CapturedMcodeReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".mcode", "captures");
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

    private sealed record Usage(int Input, int Output, int CacheRead, int CacheWrite, int Unclassified, DateTimeOffset? Timestamp, string? Identity);

    private sealed record TurnResult(string Session, string Model);

    public static IReadOnlyList<AgentUsageRecord> ParseLines(string[] lines)
    {
        var buffered = new Dictionary<string, List<Usage>>();
        var positions = new Dictionary<string, Dictionary<string, int>>();
        var results = new Dictionary<string, TurnResult>();
        var order = new List<string>();

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            // A leading BOM does not lose the line.
            var clean = raw.StartsWith('\uFEFF') ? raw[1..] : raw;
            JsonDocument document;
            try { document = JsonDocument.Parse(clean); }
            catch (JsonException) { continue; }

            using (document)
            {
                var @object = document.RootElement;
                if (@object.ValueKind != JsonValueKind.Object) continue;
                var type = Str(@object, "type");
                if (type is null) continue;

                if (type == "message")
                {
                    if (!@object.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                    if (Str(message, "role") != "assistant") continue;
                    if (Str(message, "turnId") is not { } turn) continue;
                    if (!message.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object) continue;
                    if (UsageOf(usageElement, TimestampOf(message), IdentityOf(message)) is not { } entryValue) continue;

                    if (!buffered.TryGetValue(turn, out var list))
                    {
                        buffered[turn] = list = new List<Usage>();
                        order.Add(turn);
                    }
                    Merge(entryValue, turn, buffered, positions);
                }
                else if (type == "exec.result")
                {
                    if (Str(@object, "sessionId") is not { } session) continue;
                    if (Str(@object, "turnId") is not { } turn) continue;
                    if (!@object.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) continue;
                    if (Str(model, "providerId") is null || Str(model, "modelId") is not { } modelID) continue;
                    results[turn] = new TurnResult(session, modelID);
                }
            }
        }

        var records = new List<AgentUsageRecord>();
        foreach (var turn in order)
        {
            if (!results.TryGetValue(turn, out var result) || !buffered.TryGetValue(turn, out var entries)) continue;
            for (var position = 0; position < entries.Count; position++)
            {
                var entry = entries[position];
                if (entry.Timestamp is not { } at) continue;
                records.Add(new AgentUsageRecord
                {
                    Timestamp = at,
                    Model = result.Model,
                    Tally = new TokenTally(entry.Input, entry.CacheWrite, entry.CacheRead, entry.Output),
                    SessionID = result.Session,
                    UnclassifiedTokens = entry.Unclassified,
                    DeduplicationID = $"mcode:{result.Session}:{turn}:{position}:"
                        + $"{entry.Input}:{entry.Output}:{entry.CacheRead}:{entry.CacheWrite}",
                });
            }
        }
        return records;
    }

    /// <summary>Appends a message, or replaces the earlier restatement of the
    /// SAME id. Two different messages — with or without equal counts — are
    /// always both kept.</summary>
    private static void Merge(Usage entry, string turn,
        Dictionary<string, List<Usage>> buffered, Dictionary<string, Dictionary<string, int>> positions)
    {
        if (entry.Identity is { } identity &&
            positions.TryGetValue(turn, out var map) &&
            map.TryGetValue(identity, out var index) &&
            index < (buffered[turn]?.Count ?? 0))
        {
            buffered[turn][index] = entry; // streaming restatement replaces
            return;
        }
        if (!buffered.TryGetValue(turn, out var list))
            buffered[turn] = list = new List<Usage>();
        var position = list.Count;
        list.Add(entry);
        if (entry.Identity is { } id)
        {
            if (!positions.TryGetValue(turn, out var positionsMap))
                positions[turn] = positionsMap = new Dictionary<string, int>();
            positionsMap[id] = position;
        }
    }

    /// <summary>The message's usage, or nil when it asserts nothing usable.</summary>
    private static Usage? UsageOf(JsonElement usage, DateTimeOffset? timestamp, string? identity)
    {
        var hasBuckets = usage.ValueKind == JsonValueKind.Object &&
            (usage.TryGetProperty("inputTokens", out _) || usage.TryGetProperty("outputTokens", out _) ||
             usage.TryGetProperty("cacheReadTokens", out _) || usage.TryGetProperty("cacheWriteTokens", out _));
        var total = IntOf(usage, "totalTokens");

        if (hasBuckets)
        {
            var entry = new Usage(
                IntOf(usage, "inputTokens"),
                IntOf(usage, "outputTokens"),
                IntOf(usage, "cacheReadTokens"),
                IntOf(usage, "cacheWriteTokens"),
                0, timestamp, identity);
            if (entry.Input + entry.Output + entry.CacheRead + entry.CacheWrite > 0) return entry;
            // Buckets present but empty, with a stated total: the split is not
            // reported, so the tokens are unclassified rather than fake input.
            return total > 0
                ? new Usage(0, 0, 0, 0, total, timestamp, identity)
                : null;
        }

        // No per-kind counter at all: only a stated total exists.
        return total > 0
            ? new Usage(0, 0, 0, 0, total, timestamp, identity)
            : null;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static string? IdentityOf(JsonElement message)
    {
        return Str(message, "id") ?? Str(message, "messageId") ?? Str(message, "responseId");
    }

    private static DateTimeOffset? TimestampOf(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("timestamp", out var property))
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var s) && s > 0 && s < 10_000_000_000 =>
                DateTimeOffset.FromUnixTimeSeconds(s),
            JsonValueKind.Number when property.TryGetInt64(out var ms) && ms > 0 =>
                DateTimeOffset.FromUnixTimeMilliseconds(ms),
            _ => null,
        };
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

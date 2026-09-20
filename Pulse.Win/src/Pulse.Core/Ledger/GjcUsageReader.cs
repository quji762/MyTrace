using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Gajae Code's (`gjc`) JSONL session files; port of upstream GJCUsageReader.
/// A transcript opens with `{"type":"session","id","timestamp","cwd"}` then
/// carries `{"type":"message","id","message":{"role","model","timestamp",
/// "usage":{"input","output","cacheRead","cacheWrite","totalTokens"}}}`.
/// Service-tier and unknown event types are ignored.
///
/// Only assistant rows with a model and usage emit. Identity is the entry id,
/// and ONLY that: a row with an id folds against the same id, so a replay counts
/// once. A row WITHOUT an id is its own request and is always counted — even
/// when another row carries the same second, model and token counts. Identical
/// figures are not evidence two calls are one, and the store wrote no id to say
/// so; folding them by a value hash would silently drop a real request. A
/// byte-for-byte mirror file (same session id and same file name at two depths)
/// is read once, while genuinely separate parts all count.
///
/// `cost.total` is the product's own dollars and is not read as tokens.
/// Reasoning is not exposed by this usage shape, so output is taken as reported.
/// </summary>
public static class GjcUsageReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".gjc");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoots(new[] { root });
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var records = new List<AgentUsageRecord>();
        // A session id and file name map to the digests already read; only a
        // byte-for-byte mirror of one of them is skipped.
        var seenFiles = new Dictionary<string, HashSet<string>>();

        foreach (var file in JsonlFiles(roots))
        {
            var digest = Digest(file);
            if (digest is null) continue;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { continue; }

            // The header is read first so a mirror is recognised before any of
            // its lines has been counted.
            string? headerID = null;
            string? workspace = null;
            foreach (var line in lines)
            {
                if (TryObject(line) is not { } scanEvent) continue;
                if (Str(scanEvent, "type") != "session") continue;
                headerID ??= Str(scanEvent, "id");
                workspace ??= Str(scanEvent, "cwd");
            }

            // One session written twice at two depths shares its id and file
            // name; read once ONLY when the bytes are identical. A same-named
            // file with extra requests is a fuller record, not a mirror.
            if (headerID is not null)
            {
                var mirror = $"{headerID}|{Path.GetFileName(file)}";
                if (!seenFiles.TryGetValue(mirror, out var digests))
                    seenFiles[mirror] = digests = new HashSet<string>();
                if (!digests.Add(digest)) continue;
            }

            var session = headerID ?? Path.GetFileNameWithoutExtension(file);

            foreach (var line in lines)
            {
                if (TryObject(line) is not { } @event) continue;
                if (Str(@event, "type") != "message") continue;
                if (!@event.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                if (Str(message, "role")?.ToLowerInvariant() != "assistant") continue;
                if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
                var model = Str(message, "model");
                if (model is null) continue;
                if (TimestampOf(message, @event) is not { } timestamp) continue;

                var tally = new TokenTally(
                    Input: IntOf(usage, "input"),
                    CacheWrite: IntOf(usage, "cacheWrite"),
                    CacheRead: IntOf(usage, "cacheRead"),
                    Output: IntOf(usage, "output"));

                // Only a real entry id is an identity. Without one the record
                // carries none, so the builder keeps every occurrence.
                var identity = Str(@event, "id") is { } entryID
                    ? $"gjc:{session}:{entryID}"
                    : null;

                records.Add(new AgentUsageRecord
                {
                    Timestamp = timestamp,
                    Model = model,
                    Tally = tally,
                    SessionID = session,
                    SessionName = session,
                    Project = Project(workspace),
                    DeduplicationID = identity,
                });
            }
        }
        return records;
    }

    private static IReadOnlyList<string> JsonlFiles(IEnumerable<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal));
            }
            catch (Exception) { }
        }
        return files;
    }

    /// <summary>The message's own unix-millisecond time, else the envelope's RFC
    /// 3339 time. A zero message time is unset; absent is skipped.</summary>
    private static DateTimeOffset? TimestampOf(JsonElement message, JsonElement @event)
    {
        // The message's own unix-millisecond time. A zero is unset, so the
        // envelope's RFC 3339 time is tried.
        if (message.TryGetProperty("timestamp", out var messageTimestamp))
        {
            if (messageTimestamp.ValueKind == JsonValueKind.Number &&
                messageTimestamp.TryGetInt64(out var millis) && millis > 0)
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            if (messageTimestamp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(messageTimestamp.GetString(), CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var messageParsed))
                return messageParsed; // an ISO message stamp is real too
        }
        if (@event.TryGetProperty("timestamp", out var envelopeTimestamp) &&
            envelopeTimestamp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(envelopeTimestamp.GetString(), CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;
        return null;
    }

    /// <summary>SHA-256 hex of the file's bytes: the mirror-detection digest.</summary>
    private static string? Digest(string path)
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

    private static string? Project(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return null;
        var trimmed = workspace.TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? null : name;
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

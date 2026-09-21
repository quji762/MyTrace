using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// Devin Desktop's ACP event captures, `*.ndjson` under the app's
/// `User/acp-events` directory, with the CLI's session database read only as a
/// **lookup** and, when its usage is counted separately, a source-precedence
/// check; port of upstream DevinDesktopReader.
///
/// **Two shapes, told apart by evidence, not a version marker.** The canonical
/// ACP `usage_update` carries its figures under `notification._meta` with the
/// `cognition.ai/` prefix: `inputTokens` is the complete prompt **including**
/// `cachedReadTokens`, output accumulates per step, and the cached buckets
/// overwrite. One aggregate record is emitted per file: fresh input is
/// `inputTokens − cachedReadTokens`, output is the summed output, cache read
/// and write are the last reported values. A capture with no `_meta` fields —
/// an older or mislabelled event — falls back to a usage object under the
/// metadata locations, and emits **one record per metric-bearing event**.
///
/// **The database supplies identity, not additional tokens on this route.**
/// Its `sessions.title` maps to `{id, model, working_directory}` so a Desktop
/// file whose name is an unrelated UUID recovers a stable session id, model
/// and workspace. A title two database sessions share is ambiguous and is
/// ignored. Most Desktop captures carry no usage at all; the CLI database is
/// usually where the authoritative figures are, and this reader never invents
/// the difference. When the catalogue counts that database's messages
/// separately, a capture matched to one of its counted sessions is excluded
/// as a mirror.
///
/// `reasoning` is never reported here, so it stays zero. A missing timestamp
/// is skipped rather than filled from the file's modification date, and
/// `adaptive` — a routing mode, not a model — is never a model name.
/// </summary>
public static class DevinDesktopReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appDataRoaming = string.IsNullOrEmpty(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(userProfile, "AppData", "Roaming");
        var appDataLocal = string.IsNullOrEmpty(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(userProfile, "AppData", "Local");

        var roots = new List<string>
        {
            // The mac path and its Windows equivalents, plus the lookup
            // database locations (read, never written).
            Path.Combine(appDataLocal, "Devin", "User", "acp-events"),
            Path.Combine(home, ".config", "devin", "User", "acp-events"),
            Path.Combine(home, ".local", "share", "devin", "cli", "sessions.db"),
            Path.Combine(appDataRoaming, "devin", "cli", "sessions.db"),
        };
        return RecordsFromRoots(roots);
    }

    /// <summary>`authoritativeDatabases` are the lookup databases the spend
    /// catalogue also reads as Devin's native ledger; their counted sessions
    /// suppress a mirrored capture. Other lookup databases are metadata only
    /// and cannot suppress a record.</summary>
    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(
        IEnumerable<string> roots, IReadOnlyCollection<string>? authoritativeDatabases = null)
    {
        var rootList = roots.ToList();
        var authoritative = (authoritativeDatabases ?? Array.Empty<string>())
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lookup = Sessions(rootList, authoritative);

        var records = new List<AgentUsageRecord>();
        foreach (var root in rootList)
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.ndjson", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }
            foreach (var file in files)
                records.AddRange(ReadCapture(file, lookup));
        }
        return records;
    }

    // --- the CLI lookup ------------------------------------------------------

    private sealed record CliSession(string Id, string? Model, string? Directory, bool HasCountedUsage);

    /// <summary>`sessions.title` to the sessions that carry it. A title held by
    /// more than one session is ambiguous and resolved to nil by throwing it
    /// away.</summary>
    private static IReadOnlyDictionary<string, List<CliSession>> Sessions(
        IReadOnlyList<string> roots, HashSet<string> authoritative)
    {
        var table = new Dictionary<string, List<CliSession>>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!File.Exists(root) && !Directory.Exists(root)) continue;
            foreach (var databasePath in DatabaseCandidates(root)) ReadLookup(databasePath, table, authoritative);
        }
        return table;
    }

    /// <summary>A root is either the database itself or a directory that may
    /// contain databases deeper down.</summary>
    private static IEnumerable<string> DatabaseCandidates(string root)
    {
        if (File.Exists(root)) { yield return root; yield break; }
        if (!Directory.Exists(root)) yield break;
        string[] databases;
        try
        {
            databases = Directory.EnumerateFiles(root, "sessions.db", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        }
        catch (Exception) { yield break; }
        foreach (var database in databases) yield return database;
    }

    private static void ReadLookup(
        string databasePath, Dictionary<string, List<CliSession>> table, HashSet<string> authoritative)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var (hasModel, hasDirectory) = Columns(connection);

            var counted = new HashSet<string>(StringComparer.Ordinal);
            if (authoritative.Contains(Path.GetFullPath(databasePath)))
                counted = CountedSessionIDs(connection);

            var modelColumn = hasModel ? "model" : "NULL";
            var directoryColumn = hasDirectory ? "working_directory" : "NULL";
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id, title, {modelColumn}, {directoryColumn} FROM sessions";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var id = reader.GetString(0);
                var title = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrEmpty(title)) continue;
                var model = reader.IsDBNull(2) ? null : reader.GetString(2);
                var directory = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (!table.TryGetValue(title!, out var list))
                    table[title!] = list = new List<CliSession>();
                list.Add(new CliSession(id, model, directory, counted.Contains(id)));
            }
        }
        catch (Exception) { }
    }

    private static (bool HasModel, bool HasDirectory) Columns(SqliteConnection connection)
    {
        var hasModel = false;
        var hasDirectory = false;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(sessions)";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1)) continue;
                var name = reader.GetString(1);
                if (name == "model") hasModel = true;
                if (name == "working_directory") hasDirectory = true;
            }
        }
        catch (Exception) { }
        return (hasModel, hasDirectory);
    }

    /// <summary>Only sessions this reader can actually count suppress their
    /// Desktop mirror; the exact parser of the CLI route decides.</summary>
    private static HashSet<string> CountedSessionIDs(SqliteConnection connection)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT session_id, chat_message, created_at FROM message_nodes";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(reader.GetString(1)); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("metadata", out var metadata) ||
                        metadata.ValueKind != JsonValueKind.Object ||
                        !metadata.TryGetProperty("metrics", out var metrics) ||
                        metrics.ValueKind != JsonValueKind.Object)
                        continue;
                    var total = EditorLog.FirstCount(metrics, "input_tokens")
                        + EditorLog.FirstCount(metrics, "output_tokens")
                        + EditorLog.FirstCount(metrics, "cache_read_tokens")
                        + EditorLog.FirstCount(metrics, "cache_creation_tokens");
                    if (total > 0) ids.Add(reader.GetString(0));
                }
            }
        }
        catch (Exception) { }
        return ids;
    }

    // --- one capture ---------------------------------------------------------

    private static IReadOnlyList<AgentUsageRecord> ReadCapture(
        string file, IReadOnlyDictionary<string, List<CliSession>> lookup)
    {
        var records = new List<AgentUsageRecord>();
        var title = default(string?);

        // The canonical aggregate across the file.
        int? latestInput = null;
        var latestRead = 0;
        var latestWrite = 0;
        var summedOutput = 0;
        var model = default(string?);
        DateTimeOffset? timestamp = null;

        var index = 0;
        foreach (var raw in File.ReadLines(file))
        {
            var lineIndex = index++;
            JsonDocument document;
            try { document = JsonDocument.Parse(raw); }
            catch (JsonException) { continue; }
            using (document)
            {
                var line = document.RootElement;
                if (line.ValueKind != JsonValueKind.Object ||
                    !line.TryGetProperty("notification", out var notification) ||
                    notification.ValueKind != JsonValueKind.Object)
                    continue;

                if (Str(notification, "sessionUpdate") == "session_info_update" &&
                    Str(notification, "title") is { } stated)
                    title = stated;

                if (Canonical(notification) is { } canonical)
                {
                    // `inputTokens` is the complete prompt, so it overwrites;
                    // the cached buckets overwrite too; output is summed per
                    // step.
                    if (canonical.Input is { } input) latestInput = input;
                    if (canonical.CacheRead is { } read) latestRead = read;
                    if (canonical.CacheWrite is { } write) latestWrite = write;
                    summedOutput += canonical.Output;
                    model = ModelHint(notification, canonical.Meta) ?? model;
                    timestamp = canonical.Timestamp ?? timestamp;
                    continue;
                }

                // A non-canonical event: an older capture that kept its usage
                // under the metadata locations. One record per event.
                if (Legacy(notification, lineIndex, file, title, lookup) is { } legacy)
                    records.Add(legacy);
            }
        }

        if (latestInput is { } totalInput)
        {
            var fresh = Math.Max(0, totalInput - latestRead);
            var tally = new TokenTally(fresh, latestWrite, latestRead, summedOutput);
            if (timestamp is { } at && tally.Total > 0)
            {
                var resolved = Resolve(title, lookup);
                if (resolved?.HasCountedUsage == true) return records;
                records.Add(new AgentUsageRecord
                {
                    Timestamp = at,
                    Model = ModelName(model ?? resolved?.Model),
                    Tally = tally,
                    SessionID = resolved?.Id ?? Path.GetFileNameWithoutExtension(file),
                    Project = EditorLog.Project(resolved?.Directory),
                    DeduplicationID = $"devin-desktop:{file}:usage",
                    IsAggregate = true,
                });
            }
        }
        return records;
    }

    /// <summary>The canonical ACP usage under `notification._meta`, or null
    /// when the event does not carry it.</summary>
    private static (int? Input, int? CacheRead, int? CacheWrite, int Output, DateTimeOffset? Timestamp, JsonElement Meta)? Canonical(
        JsonElement notification)
    {
        if (Str(notification, "sessionUpdate") != "usage_update" ||
            !notification.TryGetProperty("_meta", out var meta) ||
            meta.ValueKind != JsonValueKind.Object)
            return null;

        const string prefix = "cognition.ai/";
        var input = EditorLog.FirstCount(meta, prefix + "inputTokens");
        var read = EditorLog.FirstCount(meta, prefix + "cachedReadTokens");
        var write = EditorLog.FirstCount(meta, prefix + "cachedWriteTokens");
        var output = EditorLog.FirstCount(meta, prefix + "outputTokens");
        if (input is null && read is null && write is null && output is null) return null;

        return (input, read, write, output ?? 0, Flexible(Property(notification, "created_at")), meta);
    }

    /// <summary>The legacy shape: a usage object under one of the metadata
    /// locations, with fields named with underscores. One record per event,
    /// keyed by the event's line index because no other identity exists.</summary>
    private static AgentUsageRecord? Legacy(
        JsonElement notification, int index, string file,
        string? title, IReadOnlyDictionary<string, List<CliSession>> lookup)
    {
        var content = Property(notification, "content");
        var contentMetadata = content is { } c ? Property(c, "metadata") : null;
        var metadata = Property(notification, "metadata");

        foreach (var usage in Usages(contentMetadata, metadata, notification))
        {
            if (usage is not { } candidate || candidate.ValueKind != JsonValueKind.Object) continue;
            var hasTokens =
                EditorLog.FirstCount(candidate, "input_tokens") is not null ||
                EditorLog.FirstCount(candidate, "output_tokens") is not null ||
                EditorLog.FirstCount(candidate, "cache_read_tokens") is not null ||
                EditorLog.FirstCount(candidate, "cache_creation_tokens") is not null;
            if (!hasTokens) continue;

            var tally = new TokenTally(
                Input: EditorLog.FirstCount(candidate, "input_tokens") ?? 0,
                CacheWrite: EditorLog.FirstCount(candidate, "cache_creation_tokens") ?? 0,
                CacheRead: EditorLog.FirstCount(candidate, "cache_read_tokens") ?? 0,
                Output: EditorLog.FirstCount(candidate, "output_tokens") ?? 0);
            if (tally.Total <= 0) continue;

            var timestamp =
                Flexible(contentMetadata is { } cm ? Property(cm, "created_at") : null) ??
                Flexible(metadata is { } m ? Property(m, "created_at") : null) ??
                Flexible(Property(notification, "created_at")) ??
                Flexible(Property(notification, "timestamp"));
            if (timestamp is null) continue;

            var hinted =
                (contentMetadata is { } cm2 ? Str(cm2, "generation_model") : null) ??
                (metadata is { } m2 ? Str(m2, "generation_model") : null) ??
                (Property(notification, "_meta") is { } meta ? Str(meta, "cognition.ai/model") : null);
            var resolved = Resolve(title, lookup);
            if (resolved?.HasCountedUsage == true) return null;

            return new AgentUsageRecord
            {
                Timestamp = timestamp.Value,
                Model = ModelName(hinted ?? resolved?.Model),
                Tally = tally,
                SessionID = resolved?.Id ?? Path.GetFileNameWithoutExtension(file),
                Project = EditorLog.Project(resolved?.Directory),
                DeduplicationID = $"devin-desktop:{file}:{index}",
            };
        }
        return null;

        static IEnumerable<JsonElement?> Usages(JsonElement? contentMetadata, JsonElement? metadata, JsonElement notification)
        {
            if (contentMetadata is { } cm && Property(cm, "metrics") is { } m1) yield return m1;
            if (metadata is { } m && Property(m, "metrics") is { } m2) yield return m2;
            if (Property(notification, "metrics") is { } m3) yield return m3;
            yield return contentMetadata;
            yield return metadata;
        }
    }

    /// <summary>The model hint priority for the canonical shape.</summary>
    private static string? ModelHint(JsonElement notification, JsonElement meta) =>
        Str(notification, "notification_model") ?? Str(meta, "cognition.ai/model");

    /// <summary>`adaptive` is a routing mode, not a model, and an unresolved
    /// file gets `unknown` — an unpriced name — rather than a concrete model
    /// invented for the product, which would be priced at the wrong rate.</summary>
    private static string ModelName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == "adaptive") return "unknown";
        return value;
    }

    /// <summary>The CLI session whose title is this file's, when exactly one
    /// matches.</summary>
    private static CliSession? Resolve(
        string? title, IReadOnlyDictionary<string, List<CliSession>> lookup)
    {
        if (title is null) return null;
        return lookup.TryGetValue(title, out var matches) && matches.Count == 1 ? matches[0] : null;
    }

    // --- small helpers ---------------------------------------------------------

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static string? Str(JsonElement element, string name)
    {
        var property = Property(element, name);
        return property is { } p && p.ValueKind == JsonValueKind.String && p.GetString() is { } value && value.Length > 0
            ? value
            : null;
    }

    /// <summary>Seconds, milliseconds or an ISO string — whatever the event
    /// wrote — and strictly after the epoch.</summary>
    private static DateTimeOffset? Flexible(JsonElement? element)
    {
        if (element is not { } e) return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                if (!e.TryGetDouble(out var raw) || !double.IsFinite(raw) || raw <= 0) return null;
                var seconds = raw < 1e12 ? raw : raw / 1000;
                return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
            case JsonValueKind.String when e.GetString() is { } text:
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0)
                {
                    seconds = number < 1e12 ? number : number / 1000;
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
                }
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) &&
                    parsed.ToUnixTimeMilliseconds() > 0)
                    return parsed;
                return null;
            }
            default:
                return null;
        }
    }
}

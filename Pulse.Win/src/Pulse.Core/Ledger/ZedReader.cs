using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// Zed's thread database, `threads.db`; port of upstream ZedReader (the
/// non-zstd paths — .NET ships no zstd codec, so a compressed row is reported
/// as unreadable rather than silently zeroed).
///
/// Only threads whose `model.provider` is `zed.dev` count; an external ACP
/// agent's thread is skipped so the provider behind it is not counted twice.
/// A thread is cumulative over its life: ONE aggregate record per thread keyed
/// `zed:{id}`. `request_token_usage` is summed entry by entry;
/// `cumulative_token_usage` is used only when the request entries are empty.
/// No reasoning field exists, so none is derived. Imported threads are another
/// product's, already counted where they came from.
/// </summary>
public static class ZedReader
{
    public const int MaximumPayload = 32 * 1024 * 1024;

    public static IReadOnlyList<string> CandidateDatabases(
        string? userProfile = null, string? appDataLocal = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = appDataLocal ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        if (home.Length > 0) candidates.Add(Path.Combine(home, ".local", "share", "zed", "threads", "threads.db"));
        if (local.Length > 0) candidates.Add(Path.Combine(local, "Zed", "threads", "threads.db"));
        return candidates;
    }

    public static IReadOnlyList<AgentUsageRecord> Records(
        string? userProfile = null, string? appDataLocal = null)
    {
        var database = CandidateDatabases(userProfile, appDataLocal).FirstOrDefault(File.Exists);
        return database is null ? Array.Empty<AgentUsageRecord>() : Read(database);
    }

    public static IReadOnlyList<AgentUsageRecord> Read(string databasePath)
    {
        var records = new List<AgentUsageRecord>();
        if (!File.Exists(databasePath)) return records;

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

            var present = ColumnsOf(connection, "threads");
            if (present.Count == 0) return records;

            string[] columns =
            [
                "id", "updated_at", "data_type", "data", "created_at",
                "folder_paths", "folder_paths_order",
            ];
            var list = string.Join(", ", columns.Select(c => present.Contains(c) ? c : "NULL"));
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {list} FROM threads";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var id = reader.GetString(0);
                var updatedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
                var dataType = reader.IsDBNull(2) ? "" : reader.GetString(2);
                if (reader.IsDBNull(3)) continue;
                var blob = (byte[])reader.GetValue(3);
                var createdAt = reader.IsDBNull(4) ? null : reader.GetString(4);
                var folderPaths = reader.IsDBNull(5) ? null : reader.GetString(5);
                var folderOrder = reader.IsDBNull(6) ? null : reader.GetString(6);

                // A zstd-labelled or zstd-magic row is not decodable in .NET:
                // reported nowhere rather than silently zeroed (the payload
                // contract upstream carries notes for).
                if (dataType == "zstd" || IsZstd(blob)) continue;
                if (dataType != "json" && dataType.Length > 0) continue;
                if (blob.Length > MaximumPayload) continue;

                JsonElement thread;
                try { thread = JsonDocument.Parse(blob).RootElement.Clone(); }
                catch (JsonException) { continue; }
                if (thread.ValueKind != JsonValueKind.Object) continue;

                // An imported thread is another product's, already counted.
                if (thread.TryGetProperty("imported", out var imported) &&
                    imported.ValueKind == JsonValueKind.True) continue;

                if (!thread.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) continue;
                if (JsonStr(model, "provider") != "zed.dev") continue;
                var name = JsonStr(model, "model");
                if (string.IsNullOrEmpty(name)) continue;

                var request = UsageOf(thread, "request_token_usage", multiple: true);
                var cumulative = UsageOf(thread, "cumulative_token_usage", multiple: false);
                var tally = request.Total > 0 ? request : cumulative;
                if (tally.Total <= 0) continue;

                if (TimestampOf(reader, 1, updatedAt, thread) is not { } timestamp) continue;

                records.Add(new AgentUsageRecord
                {
                    Timestamp = timestamp,
                    Model = name,
                    Tally = tally,
                    SessionID = id,
                    Project = Project(folderPaths, folderOrder),
                    DeduplicationID = $"zed:{id}",
                    IsAggregate = true,
                });
            }
        }
        catch (SqliteException)
        {
            return records; // locked or foreign: nothing to read
        }
        return records;
    }

    /// <summary>`created_at` first, else `updated_at`, else the payload's own
    /// updated_at. An ISO 8601 string or a numeric epoch.</summary>
    private static DateTimeOffset? TimestampOf(SqliteDataReader reader, int updatedOrdinal, string updatedAt, JsonElement thread)
    {
        if (!reader.IsDBNull(4))
        {
            var created = reader.GetString(4);
            if (Flexible(created) is { } c) return c;
        }
        if (!string.IsNullOrEmpty(updatedAt) && Flexible(updatedAt) is { } u) return u;
        if (thread.TryGetProperty("updated_at", out var payloadUpdated) &&
            payloadUpdated.ValueKind == JsonValueKind.String &&
            Flexible(payloadUpdated.GetString()) is { } p) return p;
        return null;
    }

    private static DateTimeOffset? Flexible(string text)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;
        if (long.TryParse(text, out var seconds) && seconds > 0)
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return null;
    }

    /// <summary>A `request_token_usage` (an object of usages, or an array of
    /// them) summed over the entries that carry work; or a single
    /// `cumulative_token_usage` object.</summary>
    private static TokenTally UsageOf(JsonElement thread, string propertyName, bool multiple)
    {
        if (!thread.TryGetProperty(propertyName, out var value)) return new TokenTally();
        if (multiple)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                var sum = new TokenTally();
                foreach (var entry in value.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object) sum += Entry(entry);
                return sum;
            }
            if (value.ValueKind == JsonValueKind.Object)
            {
                var sum = new TokenTally();
                foreach (var part in value.EnumerateObject())
                    if (part.Value.ValueKind == JsonValueKind.Object) sum += Entry(part.Value);
                return sum;
            }
            return new TokenTally();
        }
        return value.ValueKind == JsonValueKind.Object ? Entry(value) : new TokenTally();
    }

    /// <summary>One TokenUsage. Values may be numbers or numeric strings; a
    /// negative clamps to zero. A zero-total entry contributes nothing.</summary>
    private static TokenTally Entry(JsonElement @object)
    {
        var tally = new TokenTally(
            Input: Clamped(@object, "input_tokens"),
            CacheWrite: Clamped(@object, "cache_creation_input_tokens"),
            CacheRead: Clamped(@object, "cache_read_input_tokens"),
            Output: Clamped(@object, "output_tokens"));
        return tally.Total > 0 ? tally : new TokenTally();
    }

    private static int Clamped(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => Math.Max(i, 0),
            JsonValueKind.Number => Math.Max((int)property.GetDouble(), 0),
            JsonValueKind.String when int.TryParse(property.GetString(), out var i) => Math.Max(i, 0),
            _ => 0,
        };
    }

    /// <summary>The workspace label: folder_paths is newline-separated and
    /// folder_paths_order names the original index of the first one. The final
    /// path component is the label, never the whole path.</summary>
    public static string? Project(string? paths, string? order)
    {
        if (string.IsNullOrEmpty(paths)) return null;
        var list = paths.Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (list.Count == 0) return null;

        var chosen = list[0];
        if (order is not null)
        {
            var first = order.Split(',').FirstOrDefault()?.Trim();
            if (int.TryParse(first, out var index) && index >= 0 && index < list.Count)
                chosen = list[index];
        }
        var name = Path.GetFileName(chosen.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string? JsonStr(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static HashSet<string> ColumnsOf(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1)) columns.Add(reader.GetString(1));
            }
        }
        catch (SqliteException) { }
        return columns;
    }

    /// <summary>The zstd frame magic: 28 B5 2F FD.</summary>
    public static bool IsZstd(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x28 && bytes[1] == 0xB5 && bytes[2] == 0x2F && bytes[3] == 0xFD;
}

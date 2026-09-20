using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// ZCode's two on-disk shapes; port of upstream ZCodeReader.
/// - JSONL transcripts under `~/.zcode/projects`,
/// - the v2 SQLite store `~/.zcode/cli/db/db.sqlite` (a `model_usage` table,
///   optionally joined to `session`).
///
/// JSONL: the `usage` object wins when it yields a split; the alternate
/// `token_usage` spelling is tried otherwise. When the store reports only a
/// bare total, the record carries `unclassifiedTokens` — never a fabricated
/// kind.
///
/// Database: this schema DOCUMENTS `input_tokens` as cache-inclusive and
/// `output_tokens` as reasoning-inclusive, so the cache overlap is removed from
/// input and `reasoning_tokens` is NOT added to output a second time; the
/// reported total can only add an unclassified remainder beyond input+output.
/// </summary>
public static class ZCodeReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".zcode");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoot(root);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoot(string root)
    {
        var records = new List<AgentUsageRecord>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
                records.AddRange(Jsonl(file));

            var database = Path.Combine(root, "cli", "db", "db.sqlite");
            if (File.Exists(database))
                records.AddRange(Database(database));
        }
        catch (Exception)
        {
            return records; // an unreadable tree reads short, not fatal
        }
        return records;
    }

    // --- JSONL transcripts -----------------------------------------------------------------

    private static IEnumerable<AgentUsageRecord> Jsonl(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (IOException) { yield break; }

        var index = 0;
        foreach (var line in lines)
        {
            var position = index++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (document)
            {
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object) continue;
                var timestamp = TimestampOf(row, "timestamp");
                var model = Str(row, "model");
                if (timestamp is null || model is null) continue;

                // `usage` wins when it yields a split; `token_usage` is the
                // alternate spelling tried otherwise.
                var parts = row.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                    ? UsageParts(usage)
                    : (Tally: new TokenTally(), Unclassified: 0);
                if (parts.Tally.Total + parts.Unclassified == 0 &&
                    row.TryGetProperty("token_usage", out var tokenUsage) &&
                    tokenUsage.ValueKind == JsonValueKind.Object)
                    parts = UsageParts(tokenUsage);
                if (parts.Tally.Total + parts.Unclassified <= 0) continue;

                var session = NonBlank(Str(row, "sessionId")) ?? stem;
                yield return new AgentUsageRecord
                {
                    Timestamp = timestamp.Value,
                    Model = model,
                    Tally = parts.Tally,
                    SessionID = session,
                    DeduplicationID = $"zcode:{file}:{position}:{session}:{timestamp.Value.ToUnixTimeSeconds()}",
                    UnclassifiedTokens = parts.Unclassified,
                };
            }
        }
    }

    /// <summary>Aliased keys, input treated as MAY-include-cache; a bare total
    /// with no named kind carries as unclassified.</summary>
    private static (TokenTally Tally, int Unclassified) UsageParts(JsonElement @object)
    {
        var input = FirstCount(@object, "input_tokens", "prompt_tokens", "inputTokens");
        var output = FirstCount(@object, "output_tokens", "completion_tokens", "outputTokens");
        var cacheRead = FirstCount(@object, "input_cache_read", "cache_read_tokens", "cacheReadTokens");
        var cacheWrite = FirstCount(@object, "input_cache_creation", "cache_write_tokens", "cacheCreationTokens");
        var reasoning = FirstCount(@object, "reasoningTokens", "reasoning_tokens");
        var total = FirstCount(@object, "totalTokens", "total_tokens");

        var any = input is not null || output is not null || cacheRead is not null ||
                  cacheWrite is not null || reasoning is not null;
        if (!any)
            return (new TokenTally(), Math.Max(total ?? 0, 0));

        var inputCount = input ?? 0;
        var outputCount = output ?? 0;
        var cacheReadCount = cacheRead ?? 0;
        var cacheWriteCount = cacheWrite ?? 0;
        var reasoningCount = reasoning ?? 0;

        // Reasoning is a subset of output in this convention: only stands in
        // when output was not reported.
        var freshInput = Math.Max(inputCount - cacheReadCount - cacheWriteCount, 0);
        var outputBucket = outputCount > 0 ? outputCount : reasoningCount;
        return (new TokenTally(freshInput, cacheWriteCount, cacheReadCount, outputBucket), 0);
    }

    // --- the v2 database ---------------------------------------------------------------------

    public static IReadOnlyList<AgentUsageRecord> Database(string databasePath)
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

            var columns = ColumnNames(connection, "model_usage");
            if (columns.Count == 0) return records;
            var hasSession = TableExists(connection, "session");

            // Only the columns the schema actually has are selected, so a legacy
            // table without computed_total_tokens still reads.
            var wanted = new[]
            {
                "id", "session_id", "model_id", "started_at", "completed_at",
                "input_tokens", "output_tokens", "reasoning_tokens",
                "cache_read_input_tokens", "cache_creation_input_tokens",
                "computed_total_tokens",
            };
            var prefix = hasSession ? "mu." : "";
            var labels = new List<string>();
            var expressions = new List<string>();
            foreach (var name in wanted.Where(columns.Contains))
            {
                labels.Add(name);
                expressions.Add(prefix + name);
            }
            if (hasSession)
            {
                labels.Add("directory");
                labels.Add("path");
                expressions.Add("s.directory");
                expressions.Add("s.path");
            }

            var indices = new Dictionary<string, int>();
            for (var position = 0; position < labels.Count; position++)
                indices[labels[position]] = position;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT " + string.Join(", ", expressions)
                + " FROM model_usage" + (hasSession ? " mu LEFT JOIN session s ON s.id = mu.session_id" : "");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string? Text(string label) =>
                    indices.TryGetValue(label, out var ordinal) && !reader.IsDBNull(ordinal)
                        ? reader.GetString(ordinal)
                        : null;
                int? Count(string label) =>
                    indices.TryGetValue(label, out var ordinal) && !reader.IsDBNull(ordinal)
                        ? reader.GetInt64(ordinal) is { } value && value is >= int.MinValue and <= int.MaxValue
                            ? (int)value
                            : null
                        : null;

                var started = Millis(Text("started_at"));
                var completed = Millis(Text("completed_at"));
                var timestamp = started ?? completed;
                var sessionID = Text("session_id");
                if (timestamp is null || sessionID is null) continue;

                var rawInput = Count("input_tokens") ?? 0;
                var rawOutput = Count("output_tokens") ?? 0;
                var cacheRead = Count("cache_read_input_tokens") ?? 0;
                var cacheWrite = Count("cache_creation_input_tokens") ?? 0;
                var computed = Count("computed_total_tokens");

                // The schema documents input as cache-inclusive and output as
                // reasoning-inclusive: cache overlap removed from input,
                // reasoning NOT folded into output a second time. The union is
                // input + output; the reported total can only add an
                // unclassified remainder beyond it.
                var freshInput = Math.Max(0, rawInput - cacheRead - cacheWrite);
                var unclassified = computed is { } computedValue && computedValue > rawInput + rawOutput
                    ? computedValue - (rawInput + rawOutput)
                    : 0;

                var identifier = Text("id") ?? $"{sessionID}#{(started ?? completed ?? DateTimeOffset.MinValue).ToUnixTimeSeconds()}";
                var directory = Text("directory") ?? Text("path");
                records.Add(new AgentUsageRecord
                {
                    Timestamp = timestamp.Value,
                    Model = Text("model_id") ?? "auto",
                    Tally = new TokenTally(
                        Input: freshInput,
                        CacheWrite: cacheWrite,
                        CacheRead: cacheRead,
                        Output: rawOutput),
                    SessionID = sessionID,
                    Project = directory is null
                        ? null
                        : Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    DeduplicationID = $"zcode:{databasePath}:{identifier}",
                    UnclassifiedTokens = unclassified,
                });
            }
        }
        catch (SqliteException)
        {
            return records; // locked or foreign: nothing to read
        }
        return records;
    }

    private static HashSet<string> ColumnNames(SqliteConnection connection, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1)) names.Add(reader.GetString(1));
            }
        }
        catch (SqliteException) { }
        return names;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $table";
        command.Parameters.AddWithValue("$table", table);
        return command.ExecuteScalar() is not null;
    }

    // --- helpers ---------------------------------------------------------------------------

    private static DateTimeOffset? Millis(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis) && millis > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(millis);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        return null;
    }

    private static int? FirstCount(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (!element.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.Number)
                continue;
            if (property.TryGetInt32(out var value)) return value;
            if (property.TryGetDouble(out var dbl)) return (int)dbl;
        }
        return null;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static DateTimeOffset? TimestampOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        switch (property.ValueKind)
        {
            case JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
            case JsonValueKind.String when DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed):
                return parsed;
            case JsonValueKind.Number when property.TryGetInt64(out var millis) && millis > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            default:
                return null;
        }
    }

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

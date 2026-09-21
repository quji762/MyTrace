using System.Globalization;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Goose's session database, `sessions.db`; port of upstream GooseReader.
/// The first candidate root that exists wins, in priority order.
///
/// ```sql
/// sessions(id, model_config_json, provider_name, created_at, total_tokens,
///          input_tokens, output_tokens, accumulated_total_tokens,
///          accumulated_input_tokens, accumulated_output_tokens)
/// ```
///
/// The ACCUMULATED columns are cumulative and preferred; a session that grows
/// between reads changes its figures under the same id, so this is one record
/// per session — an aggregate, not a turn.
///
/// The difference between the total and the named kinds is UNCLASSIFIED, never
/// reasoning: Goose carries no reasoning counter and no cache columns, and
/// deriving one from `max(0, total − input − output)` would be a claim about a
/// schema that does not state it. Counted, never priced, never shown as a kind.
///
/// A session whose `created_at` cannot be read is skipped rather than dated
/// 1970: a record must not be bucketed on a date nobody wrote.
/// </summary>
public static class GooseReader
{
    /// <summary>Every candidate, in priority order.</summary>
    public static IReadOnlyList<string> CandidateDatabases(
        string? userProfile = null,
        string? appDataRoaming = null,
        string? appDataLocal = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? FromEnv(string name) =>
            environment is null ? Environment.GetEnvironmentVariable(name) : environment.GetValueOrDefault(name);

        var candidates = new List<string>();
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = appDataRoaming ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = appDataLocal ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (FromEnv("GOOSE_PATH_ROOT") is { } root && root.Length > 0)
            candidates.Add(Path.Combine(root, "data", "sessions", "sessions.db"));
        if (roaming.Length > 0)
            candidates.Add(Path.Combine(roaming, "goose", "sessions", "sessions.db"));
        if (local.Length > 0)
            candidates.Add(Path.Combine(local, "goose", "sessions", "sessions.db"));
        if (roaming.Length > 0)
            candidates.Add(Path.Combine(roaming, "Block", "goose", "sessions", "sessions.db"));
        if (home.Length > 0)
            candidates.Add(Path.Combine(home, ".local", "share", "Block", "goose", "sessions", "sessions.db"));
        return candidates;
    }

    public static IReadOnlyList<AgentUsageRecord> Records(
        string? userProfile = null,
        string? appDataRoaming = null,
        string? appDataLocal = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var database = CandidateDatabases(userProfile, appDataRoaming, appDataLocal, environment)
            .FirstOrDefault(File.Exists);
        return database is null ? Array.Empty<AgentUsageRecord>() : Read(database);
    }

    public static IReadOnlyList<AgentUsageRecord> Read(string databasePath)
    {
        var records = new List<AgentUsageRecord>();
        if (!File.Exists(databasePath)) return records;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly, // read-only and in place
            Pooling = false,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var present = ColumnsOf(connection, "sessions");
            if (present.Count == 0) return records;

            // Declared columns in a fixed order, with NULL for any an older
            // schema lacks; model_config_json is parsed rather than read plain.
            string[] columns =
            [
                "id", "model_config_json", "created_at", "input_tokens", "output_tokens",
                "total_tokens", "accumulated_input_tokens", "accumulated_output_tokens",
                "accumulated_total_tokens",
            ];
            var list = string.Join(", ", columns.Select(c => present.Contains(c) ? c : "NULL"));
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {list} FROM sessions";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                var config = reader.IsDBNull(1) ? null : reader.GetString(1);
                var createdAt = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (id is null || config is null || createdAt is null) continue;
                var model = ModelIn(config);
                if (model is null) continue;
                if (Utc(createdAt) is not { } timestamp) continue;

                // Accumulated preferred; plain ones are the fallback.
                var input = IntColumn(reader, 6) ?? IntColumn(reader, 3) ?? 0;
                var output = IntColumn(reader, 7) ?? IntColumn(reader, 4) ?? 0;
                var total = IntColumn(reader, 8) ?? IntColumn(reader, 5) ?? 0;

                if (input <= 0 && output <= 0 && total <= 0) continue;

                var known = input + output;
                records.Add(new AgentUsageRecord
                {
                    Timestamp = timestamp,
                    Model = model,
                    Tally = new TokenTally(Input: input, Output: output),
                    // A total larger than the kinds it names: the part that
                    // cannot be attributed is kept as such.
                    UnclassifiedTokens = Math.Max(0, total - known),
                    SessionID = id,
                    DeduplicationID = id,
                    IsAggregate = true,
                });
            }
        }
        catch (SqliteException)
        {
            return records; // a locked or foreign store is "nothing to read"
        }
        return records;
    }

    /// <summary>NULL for an absent column reads as absent, not zero.</summary>
    private static int? IntColumn(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        try { return reader.GetInt32(ordinal); }
        catch (Exception)
        {
            try
            {
                var raw = reader.GetValue(ordinal);
                return Convert.ToInt32(raw);
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>The table's columns in declared (cid) order. A List, not a
    /// HashSet: the SELECT maps columns to fixed ordinals.</summary>
    private static List<string> ColumnsOf(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            var rows = new List<(long Cid, string Name)>();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                    rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
            foreach (var (_, name) in rows.OrderBy(r => r.Cid))
                columns.Add(name);
        }
        catch (SqliteException) { }
        return columns;
    }

    /// <summary>`model_config_json.model_name`, or nil for a NULL/blank config, a
    /// config that is not an object, or a blank model name. Other keys ignored.</summary>
    public static string? ModelIn(string config)
    {
        try
        {
            using var document = JsonDocument.Parse(config);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("model_name", out var name) ||
                name.ValueKind != JsonValueKind.String)
                return null;
            var value = name.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The UTC stamps the store writes, in the formats it has shipped.</summary>
    public static DateTimeOffset? Utc(string text) =>
        text switch
        {
            _ when DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
            _ when DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dateOnly) => dateOnly,
            _ when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var loose) => loose,
            _ => null,
        };
}

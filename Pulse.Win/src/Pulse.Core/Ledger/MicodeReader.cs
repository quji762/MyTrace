using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// MiMo Code's OpenCode-shaped database, `mimocode*.db` under the XDG data
/// directory, unioned with the Orca hook's shared copy; port of upstream
/// MicodeReader.
///
/// The assistant message's JSON is the same shape OpenCode and Kilo write:
/// modelID, providerID, tokens.{input,output,reasoning,cache{read,write}} and
/// time.{created,completed}. Both epochs are tolerated (milliseconds on current
/// builds, seconds on older ones). Reasoning is a real field and is billed as
/// output, folded in once. The store's own cost is ignored — a cost the store
/// wrote under its own route would put a second, differently-sourced figure in
/// the total.
///
/// An embedded `id` is the product's own identity and is NOT namespaced by
/// database, so a message written to both the XDG and Orca copies folds to one;
/// a row id has no such identity and is namespaced by the database path.
/// </summary>
public static class MicodeReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string>();
        if (home.Length > 0)
            roots.Add(Path.Combine(home, ".local", "share", "mimocode"));
        if (local.Length > 0)
            roots.Add(Path.Combine(local, "orca", "mimocode-hooks", "shared", "data"));
        return RecordsFromRoots(roots.Where(Directory.Exists));
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var records = new List<AgentUsageRecord>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] databases;
            try
            {
                databases = Directory.EnumerateFiles(root, "mimocode*.db", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var database in databases)
                records.AddRange(Read(database));
        }
        return records;
    }

    private static IReadOnlyList<AgentUsageRecord> Read(string databasePath)
    {
        var records = new List<AgentUsageRecord>();
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

            var directories = Directories(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, session_id, data FROM message";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2)) continue;
                var rowID = reader.GetString(0);
                var sessionColumn = reader.GetString(1);
                var data = reader.GetString(2);

                JsonElement payload;
                string? model;
                try
                {
                    var parsed = JsonDocument.Parse(data);
                    var root = parsed.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        Str(root, "role") != "assistant" ||
                        Str(root, "modelID") is not { } modelValue)
                    {
                        parsed.Dispose();
                        continue;
                    }
                    payload = root.Clone();
                    model = modelValue;
                    parsed.Dispose();
                }
                catch (JsonException) { continue; }

                using (var payloadDoc = JsonDocument.Parse(payload.GetRawText()))
                {
                    var payloadElement = payloadDoc.RootElement;
                    model = Str(payloadElement, "modelID");
                    if (!payloadElement.TryGetProperty("time", out var time) ||
                        time.ValueKind != JsonValueKind.Object ||
                        !time.TryGetProperty("created", out var createdElement) ||
                        !createdElement.TryGetInt64(out var created) || created <= 0) continue;
                    // Both epochs are tolerated: created is milliseconds on
                    // current builds, seconds on older ones.
                    var timestamp = created > 10_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(created)
                        : DateTimeOffset.FromUnixTimeSeconds(created);

                    var counts = payloadElement.TryGetProperty("tokens", out var tk) && tk.ValueKind == JsonValueKind.Object
                        ? tk
                        : default;
                    var cache = counts.ValueKind == JsonValueKind.Object &&
                                counts.TryGetProperty("cache", out var ch) && ch.ValueKind == JsonValueKind.Object
                        ? ch
                        : default;

                    // Reasoning is a real field and is billed as output.
                    var tally = new TokenTally(
                        Input: Clamped(counts, "input"),
                        CacheWrite: Clamped(cache, "write"),
                        CacheRead: Clamped(cache, "read"),
                        Output: Clamped(counts, "output") + Clamped(counts, "reasoning"));
                    if (tally.Total <= 0) continue;

                    var session = Str(payloadElement, "sessionID")
                        ?? Str(payloadElement, "session_id")
                        ?? sessionColumn;
                    var directory = directories.GetValueOrDefault(sessionColumn);
                    var workspace = Str(payloadElement, "path")
                        ?? directory;

                    // An embedded id is the product's own identity and is not
                    // namespaced; a row id has no such identity and is
                    // namespaced by the database path.
                    var embedded = Str(payloadElement, "id");
                    records.Add(new AgentUsageRecord
                    {
                        Timestamp = timestamp,
                        Model = model,
                        Tally = tally,
                        SessionID = session,
                        Project = workspace is { } ws ? Path.GetFileName(ws.TrimEnd('/', '\\')) : null,
                        DeduplicationID = embedded ?? $"micode:{databasePath}:{rowID}",
                    });
                }
            }
        }
        catch (SqliteException)
        {
            return records; // locked or foreign: nothing to read
        }
        return records;
    }

    /// <summary>`session.id` to its `directory`, where the older schema keeps it.</summary>
    private static Dictionary<string, string> Directories(SqliteConnection connection)
    {
        var rows = new Dictionary<string, string>();
        if (ColumnsOf(connection, "session").Contains("directory"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, directory FROM session";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                    rows[reader.GetString(0)] = reader.GetString(1);
            }
        }
        return rows;
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

    private static int Clamped(JsonElement element, string name)
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

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }
}

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// Hermes Agent's SQLite state at $HERMES_HOME/state.db and one state.db per
/// named profile; port of upstream HermesReader.
///
/// A session is a CUMULATIVE DEPLOYMENT, not a request: the per-model SUM and
/// the session row both run over the session's whole life. Two passes keep that
/// from double counting — a session the optional per-model table explains is
/// emitted per model and never re-emitted from the session row; every other
/// session is emitted once from its own totals.
///
/// Input is only split when there is no cache to collide with: the schema does
/// not establish whether input_tokens already contains the cache read. With no
/// cache reported the input is fresh; with a non-zero cache the reported input
/// is carried as unclassified real work and the cache columns are not added.
/// Reasoning is not added to output either. A positive cache or reasoning marks
/// the record partial; a session with neither reports all four kinds.
///
/// Nothing is invented: billing_provider, message_count, titles and workspaces
/// are read by no field of the record; the raw provider string survives only in
/// the dedup identity so two provider groups of one session stay distinct.
/// </summary>
public static class HermesReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(
        string? userProfile = null, string? hermesHome = null)
    {
        var root = hermesHome
            ?? Environment.GetEnvironmentVariable("HERMES_HOME")
            ?? Path.Combine(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes");

        var roots = new List<string> { Path.Combine(root, "state.db") };

        var profilesDir = Path.Combine(root, "profiles");
        if (Directory.Exists(profilesDir))
        {
            var databases = new List<string>();
            try
            {
                foreach (var profilePath in Directory.EnumerateDirectories(profilesDir))
                {
                    var db = Path.Combine(profilePath, "state.db");
                    if (File.Exists(db)) databases.Add(db);
                }
            }
            catch (Exception) { }
            if (databases.Count > 0) roots.AddRange(databases);
            else roots.Add(profilesDir); // watched while it holds no profiles
        }

        var localAppData = string.IsNullOrEmpty(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(userProfile, "AppData", "Local");
        if (localAppData.Length > 0)
            roots.Add(Path.Combine(localAppData, "hermes", "state.db"));
        if (!string.IsNullOrEmpty(userProfile))
            roots.Add(Path.Combine(userProfile, "AppData", "Local", "hermes", "state.db"));

        var records = new List<AgentUsageRecord>();
        foreach (var rootCandidate in roots.Where(File.Exists).OrderBy(f => f, StringComparer.Ordinal))
            records.AddRange(Read(rootCandidate));
        return records.OrderBy(r => r.Timestamp)
            .ThenBy(r => r.DeduplicationID ?? "", StringComparer.Ordinal).ToList();
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

            if (ColumnsOf(connection, "sessions").Count == 0) return records;

            var sessions = ReadSessions(connection);
            var covered = new HashSet<string>();
            records.AddRange(PerModel(connection, sessions, covered));
            records.AddRange(SessionTotals(connection, sessions, covered));
        }
        catch (SqliteException)
        {
            return records; // locked or foreign: nothing to read
        }
        return records;
    }

    // --- pass one: the per-model table ------------------------------------------------

    private static IReadOnlyList<AgentUsageRecord> PerModel(
        SqliteConnection connection, Dictionary<string, SessionRow> sessions, HashSet<string> covered)
    {
        var records = new List<AgentUsageRecord>();
        if (ColumnsOf(connection, "session_model_usage").Count == 0) return records;

        var groups = new Dictionary<string, (int Input, int CacheWrite, int CacheRead, int Output, int Reasoning)>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, model, billing_provider, input_tokens,
                   output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens
            FROM session_model_usage
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            var session = reader.GetString(0);
            var model = reader.GetString(1);
            var provider = reader.IsDBNull(2) ? null : reader.GetString(2);

            var key = $"{session}|{model}|{provider ?? "<null>"}";
            var sums = groups.GetValueOrDefault(key);
            sums.Input += reader.IsDBNull(3) ? 0 : reader.GetInt64(3) is { } i3 ? (int)Math.Min(i3, int.MaxValue) : 0;
            sums.Output += reader.IsDBNull(4) ? 0 : reader.GetInt64(4) is { } o4 ? (int)Math.Min(o4, int.MaxValue) : 0;
            sums.CacheRead += reader.IsDBNull(5) ? 0 : reader.GetInt64(5) is { } cr5 ? (int)Math.Min(cr5, int.MaxValue) : 0;
            sums.CacheWrite += reader.IsDBNull(6) ? 0 : reader.GetInt64(6) is { } cw6 ? (int)Math.Min(cw6, int.MaxValue) : 0;
            sums.Reasoning += reader.IsDBNull(7) ? 0 : reader.GetInt64(7) is { } r7 ? (int)Math.Min(r7, int.MaxValue) : 0;
            groups[key] = sums;
        }

        foreach (var (key, sums) in groups)
        {
            var total = sums.Input + sums.Output + sums.CacheRead + sums.CacheWrite + sums.Reasoning;
            if (total <= 0) continue;
            var session = key.Split('|')[0];
            // A session covered here is never read from its own row.
            covered.Add(session);
            if (!sessions.TryGetValue(session, out var meta) || meta.StartedAt is not { } startedAt) continue;

            // The literal <null> keeps a NULL provider a distinct key from an
            // empty string.
            var provider = key.Split('|')[2];
            var model = key.Split('|')[1];
            records.Add(Record(startedAt, model,
                sums.Input, sums.Output, sums.Reasoning, sums.CacheRead, sums.CacheWrite,
                session, $"hermes:{session}:{model}:{provider}"));
        }
        return records;
    }

    // --- pass two: session rows -----------------------------------------------------------

    private static IReadOnlyList<AgentUsageRecord> SessionTotals(
        SqliteConnection connection, Dictionary<string, SessionRow> sessions, HashSet<string> covered)
    {
        var records = new List<AgentUsageRecord>();
        var columns = ColumnsOf(connection, "sessions");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, model, started_at FROM sessions";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var id = reader.GetString(0);
            if (covered.Contains(id)) continue;

            var startedAt = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            var model = reader.IsDBNull(1) ? null : reader.GetString(1);
            // No stated start and no stated model: nothing to bucket tokens
            // under, and neither is fabricated.
            if (startedAt <= 0 || string.IsNullOrEmpty(model)) continue;
            if (!sessions.TryGetValue(id, out var session)) continue;
            var sessionStart = startedAt > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(startedAt)
                : DateTimeOffset.FromUnixTimeSeconds(startedAt);

            records.Add(Record(sessionStart, model,
                session.Input, session.Output, session.Reasoning, session.CacheRead, session.CacheWrite,
                id, id));
        }
        return records;
    }

    // --- one aggregate record -----------------------------------------------------------

    private sealed record SessionRow(string? Model, DateTimeOffset? StartedAt, int Input, int Output, int CacheRead, int CacheWrite, int Reasoning);

    private static Dictionary<string, SessionRow> ReadSessions(SqliteConnection connection)
    {
        var rows = new Dictionary<string, SessionRow>();
        var columns = ColumnsOf(connection, "sessions");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + string.Join(", ",
            columns.Select(c => columns.Contains(c) ? c : "NULL")) + " FROM sessions";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var id = reader.GetString(0);
            var startedAt = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            rows[id] = new SessionRow(
                Model: reader.IsDBNull(1) ? null : reader.GetString(1),
                StartedAt: startedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(startedAt) : null,
                Input: reader.IsDBNull(3) ? 0 : (int)Math.Min(reader.GetInt64(3), int.MaxValue),
                Output: reader.IsDBNull(4) ? 0 : (int)Math.Min(reader.GetInt64(4), int.MaxValue),
                CacheRead: reader.IsDBNull(5) ? 0 : (int)Math.Min(reader.GetInt64(5), int.MaxValue),
                CacheWrite: reader.IsDBNull(6) ? 0 : (int)Math.Min(reader.GetInt64(6), int.MaxValue),
                Reasoning: reader.IsDBNull(7) ? 0 : (int)Math.Min(reader.GetInt64(7), int.MaxValue));
        }
        return rows;
    }

    /// <summary>One aggregate record from the reported columns. The
    /// input/cache relationship is unproven, so it is never summed; reasoning
    /// is not added to output either. A positive cache or reasoning means the
    /// record is a KNOWN SUBSET and is marked partial.</summary>
    private static AgentUsageRecord? Record(
        DateTimeOffset date, string model,
        int input, int output, int reasoning, int cacheRead, int cacheWrite,
        string sessionID, string deduplicationID)
    {
        var hasCache = cacheRead > 0 || cacheWrite > 0;
        var tally = hasCache
            ? new TokenTally(Output: output)
            : new TokenTally(Input: input, Output: output);
        var unclassified = hasCache ? input : 0;
        if (tally.Total + unclassified <= 0) return null;
        var isPartial = hasCache || reasoning > 0;

        return new AgentUsageRecord
        {
            Timestamp = date,
            Model = model,
            Tally = tally,
            UnclassifiedTokens = unclassified,
            SessionID = sessionID,
            DeduplicationID = deduplicationID,
            IsAggregate = true,
            IsPartial = isPartial,
        };
    }

    /// <summary>The table's columns in declared (cid) order. A List, not a
    /// HashSet: the SELECT that consumes it must map columns to fixed ordinals,
    /// which a HashSet's iteration order cannot guarantee.</summary>
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
}

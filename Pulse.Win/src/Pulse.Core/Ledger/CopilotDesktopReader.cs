using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// Copilot's desktop SQLite database and its session-state sidecar; port of
/// upstream CopilotDesktopReader.
///
/// The database row is LIFETIME, the sidecar is a running total: a row's token
/// columns are cumulative against its immutable created_at, and the sidecar
/// event log (`session-state/&lt;id&gt;/events.jsonl`) writes a `session.shutdown`
/// snapshot each time a run ends. Snapshots are differenced per model into
/// increments, and the database row is then the authority: a per-row budget caps
/// what the sidecar may contribute, and whatever the increments leave
/// unexplained is emitted once at created_at so the row's lifetime figure is
/// never lost.
///
/// A missing head is not an increment: without a `session.start` opener the
/// earliest snapshot is an unknown baseline, used only to difference the ones
/// after it, its tokens falling to the created_at remainder.
///
/// cache_write lives only in the sidecar (no row column), so it is never part of
/// the budget or the remainder. Reasoning is NOT added to output: neither source
/// states containment, so reported output stays whole, the ambiguous reasoning
/// count is not placed in any bucket, and a run that stated reasoning is marked
/// isPartial instead.
///
/// Every record from this lane is aggregate: a shutdown snapshot is session-level
/// timing, not the instant the tokens were spent.
/// </summary>
public static class CopilotDesktopReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var database = Path.Combine(home, ".copilot", "data.db");
        var sidecarRoot = Path.Combine(home, ".copilot", "session-state");
        return !File.Exists(database) ? Array.Empty<AgentUsageRecord>() : Records(database, sidecarRoot);
    }

    public static IReadOnlyList<AgentUsageRecord> Records(string databasePath, string sidecarRoot)
    {
        var output = new List<AgentUsageRecord>();
        if (!File.Exists(databasePath)) return output;

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

            var present = ColumnsOf(connection, "sessions");
            if (present.Count == 0) return output;

            string[] columns =
            [
                "id", "title", "model", "total_input_tokens", "total_output_tokens",
                "total_cached_tokens", "total_reasoning_tokens", "created_at",
            ];
            var list = string.Join(", ", columns.Select(c => present.Contains(c) ? c : "NULL"));

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {list} FROM sessions";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Row(reader) is not { } row) continue;
                output.AddRange(ReadSession(row, sidecarRoot));
            }
        }
        catch (SqliteException)
        {
            return output;
        }
        return output;
    }

    // --- one session -----------------------------------------------------------------

    internal sealed record DesktopRow(string Id, string? Title, string? Model, int Input, int Output, int Cached, int Reasoning, DateTimeOffset? CreatedAt);

    internal sealed record Counts(int Input, int Output, int CacheRead, int CacheWrite, int Reasoning)
    {
        public int Counted => Input + Output + CacheRead + CacheWrite;
        public bool OmittedReasoning => Reasoning > 0;

        public static Counts Empty { get; } = new(0, 0, 0, 0, 0);

        public static Counts operator +(Counts a, Counts b) =>
            new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite, a.Reasoning + b.Reasoning);
    }

    private static IReadOnlyList<AgentUsageRecord> ReadSession(DesktopRow row, string sidecarRoot)
    {
        var eventsFile = Path.Combine(sidecarRoot, row.Id, "events.jsonl");
        string[] lines = Array.Empty<string>();
        try
        {
            if (File.Exists(eventsFile)) lines = File.ReadAllLines(eventsFile);
        }
        catch (IOException) { }

        var running = new Dictionary<string, Counts>();
        var applied = Counts.Empty;
        var trackedModel = row.Model;
        string? workspace = null;
        var openedWithStart = false;
        var sawFirst = false;
        var records = new List<AgentUsageRecord>();

        var index = -1;
        foreach (var raw in lines)
        {
            index++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonElement? eventMaybe = null;
            try { eventMaybe = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch (JsonException) { }
            if (eventMaybe is not { } @event || @event.ValueKind != JsonValueKind.Object) continue;

            var type = Str(@event, "type");
            if (!sawFirst)
            {
                sawFirst = true;
                openedWithStart = type == "session.start";
            }

            switch (type ?? "")
            {
                case "session.start":
                    if (@event.TryGetProperty("data", out var startData) &&
                        startData.ValueKind == JsonValueKind.Object &&
                        startData.TryGetProperty("context", out var context) &&
                        context.ValueKind == JsonValueKind.Object &&
                        context.TryGetProperty("cwd", out var cwd) &&
                        cwd.ValueKind == JsonValueKind.String)
                        workspace = cwd.GetString();
                    break;

                case "session.model_change":
                    if (@event.TryGetProperty("data", out var modelData) &&
                        modelData.ValueKind == JsonValueKind.Object &&
                        modelData.TryGetProperty("newModel", out var newModel) &&
                        newModel.ValueKind == JsonValueKind.String &&
                        newModel.GetString() != "auto")
                        trackedModel = newModel.GetString();
                    break;

                case "session.shutdown":
                    // A snapshot with no stated time cannot be placed; its tokens
                    // fall to the remainder at created_at instead.
                    if (EventTimestamp(@event) is not { } timestamp) continue;
                    var data = @event.TryGetProperty("data", out var shutdownData) &&
                               shutdownData.ValueKind == JsonValueKind.Object
                        ? shutdownData
                        : default;
                    var metrics = data.ValueKind == JsonValueKind.Object &&
                                  data.TryGetProperty("modelMetrics", out var mm) &&
                                  mm.ValueKind == JsonValueKind.Object
                        ? mm
                        : default;
                    var identity = EventIdentity(@event, index);

                    foreach (var tracker in SortedKeys(metrics))
                    {
                        if (UsageOf(metrics.GetProperty(tracker)) is not { } current) continue;
                        var model = ResolvedModel(tracker, data, trackedModel, row.Model);
                        var hadPrior = running.ContainsKey(model);
                        var previous = running.GetValueOrDefault(model, Counts.Empty);
                        var delta = Subtract(current, previous);
                        running[model] = current;

                        // No session.start opener: the earliest snapshot is an
                        // unknown baseline, not a delta.
                        if (!hadPrior && !openedWithStart) continue;

                        var budgeted = Budget(delta, applied, row);
                        applied += budgeted;
                        if (budgeted.Counted <= 0) continue;

                        records.Add(new AgentUsageRecord
                        {
                            Timestamp = timestamp,
                            Model = model,
                            Tally = TallyOf(budgeted),
                            IsAggregate = true,
                            // The run itself stated reasoning we could not place:
                            // a known subset. Reads the run's delta, not the
                            // budgeted copy.
                            IsPartial = delta.OmittedReasoning,
                            SessionID = row.Id,
                            Title = row.Title,
                            Project = Project(workspace),
                            DeduplicationID = $"copilot-desktop:{row.Id}:shutdown:{identity}:{model}",
                        });
                    }
                    break;
            }
        }

        // Whatever the snapshots did not account for — a run that died before
        // shutdown, or a missing head — is emitted once at created_at.
        var remainder = Residual(applied, row);
        if (remainder.Counted > 0 && row.CreatedAt is { } createdAt)
        {
            records.Add(new AgentUsageRecord
            {
                Timestamp = createdAt,
                Model = row.Model ?? trackedModel ?? "auto",
                Tally = TallyOf(remainder),
                IsAggregate = true,
                IsPartial = remainder.OmittedReasoning,
                SessionID = row.Id,
                Title = row.Title,
                Project = Project(workspace),
                DeduplicationID = $"copilot-desktop:{row.Id}:row",
            });
        }

        return records;
    }

    private static TokenTally TallyOf(Counts counts) => new(
        Input: counts.Input,
        CacheWrite: counts.CacheWrite,
        CacheRead: counts.CacheRead,
        // The reported output is kept whole; ambiguous reasoning is never added.
        Output: counts.Output);

    // --- budget and remainder ----------------------------------------------------------

    /// <summary>The sidecar's next increment, capped so it can never carry a row
    /// past its own lifetime total. cache_write has no row to compare against.</summary>
    private static Counts Budget(Counts delta, Counts applied, DesktopRow row) => new(
        Input: Math.Min(delta.Input, Math.Max(row.Input - applied.Input, 0)),
        Output: Math.Min(delta.Output, Math.Max(row.Output - applied.Output, 0)),
        CacheRead: Math.Min(delta.CacheRead, Math.Max(row.Cached - applied.CacheRead, 0)),
        CacheWrite: delta.CacheWrite,
        Reasoning: Math.Min(delta.Reasoning, Math.Max(row.Reasoning - applied.Reasoning, 0)));

    private static Counts Residual(Counts applied, DesktopRow row) => new(
        Input: Math.Max(row.Input - applied.Input, 0),
        Output: Math.Max(row.Output - applied.Output, 0),
        CacheRead: Math.Max(row.Cached - applied.CacheRead, 0),
        CacheWrite: 0,
        Reasoning: Math.Max(row.Reasoning - applied.Reasoning, 0));

    private static Counts Subtract(Counts current, Counts? previous) =>
        previous is null ? current : new Counts(
            Math.Max(current.Input - previous.Input, 0),
            Math.Max(current.Output - previous.Output, 0),
            Math.Max(current.CacheRead - previous.CacheRead, 0),
            Math.Max(current.CacheWrite - previous.CacheWrite, 0),
            Math.Max(current.Reasoning - previous.Reasoning, 0));

    // --- event fields --------------------------------------------------------------------

    private static Counts? UsageOf(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
            return null;
        return new Counts(
            Clamped(usage, "inputTokens"),
            Clamped(usage, "outputTokens"),
            Clamped(usage, "cacheReadTokens"),
            Clamped(usage, "cacheWriteTokens"),
            Clamped(usage, "reasoningTokens"));
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int Clamped(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => Math.Max(i, 0),
            JsonValueKind.Number when property.TryGetDouble(out var d) => Math.Max((int)d, 0),
            _ => 0,
        };
    }

    /// <summary>A tracker key of "" or "auto" defers to the run's current model,
    /// then the last model change, then the row.</summary>
    private static string ResolvedModel(string tracker, JsonElement data, string? tracked, string? rowModel)
    {
        if (!string.IsNullOrEmpty(tracker) && tracker != "auto") return tracker;
        if (data.ValueKind == JsonValueKind.Object && Str(data, "currentModel") is { } current) return current;
        if (tracked is { } t) return t;
        if (rowModel is { } r) return r;
        return "auto";
    }

    private static DateTimeOffset? EventTimestamp(JsonElement @event)
    {
        if (Flexible(@event.TryGetProperty("timestamp", out var t1) ? t1 : default) is { } a) return a;
        if (@event.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            Flexible(data.TryGetProperty("timestamp", out var t2) ? t2 : default) is { } b) return b;
        return null;
    }

    /// <summary>The envelope's own id, else a stable hash of the event JSON, else
    /// its position. The id keeps two runs' snapshots apart.</summary>
    private static string EventIdentity(JsonElement @event, int index)
    {
        if (Str(@event, "id") is { } id) return id;
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(@event.GetRawText()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string? Project(string? workspace) =>
        workspace is null ? null : Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    // --- database -----------------------------------------------------------------------

    private static DesktopRow? Row(SqliteDataReader reader)
    {
        if (reader.IsDBNull(0)) return null;
        var id = reader.GetString(0);
        var input = IntColumn(reader, 3);
        var output = IntColumn(reader, 4);
        var cached = IntColumn(reader, 5);
        var reasoning = IntColumn(reader, 6);

        // A row with no tokens at all is not work.
        if ((input ?? 0) <= 0 && (output ?? 0) <= 0 && (cached ?? 0) <= 0 && (reasoning ?? 0) <= 0)
            return null;

        return new DesktopRow(
            Id: id,
            Title: reader.IsDBNull(1) ? null : reader.GetString(1),
            Model: reader.IsDBNull(2) ? null : reader.GetString(2),
            Input: input ?? 0,
            Output: output ?? 0,
            Cached: cached ?? 0,
            Reasoning: reasoning ?? 0,
            CreatedAt: reader.IsDBNull(7) ? null : FlexibleText(reader.GetString(7)));
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

    private static int? IntColumn(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        try { return reader.GetInt32(ordinal); }
        catch (Exception)
        {
            try { return Convert.ToInt32(reader.GetValue(ordinal)); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Flexible stamp: epoch milliseconds, seconds, or ISO text.</summary>
    private static DateTimeOffset? Flexible(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt64(out var number) =>
            number > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                : DateTimeOffset.FromUnixTimeSeconds(number),
        JsonValueKind.String when DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) => parsed,
        _ => null,
    };

    private static DateTimeOffset? FlexibleText(string text) =>
        long.TryParse(text, out var number)
            ? number > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                : DateTimeOffset.FromUnixTimeSeconds(number)
            : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
                ? parsed
                : null;

    private static List<string> SortedKeys(JsonElement element)
    {
        var keys = new List<string>();
        foreach (var property in element.EnumerateObject()) keys.Add(property.Name);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}

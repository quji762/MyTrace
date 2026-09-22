using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// OpenClaw's two stores and its legacy names; port of upstream
/// OpenClawSessionReader. The current store is a SQLite database
/// (`&lt;agentId&gt;/agent/openclaw-agent.sqlite`, `transcript_events` rows), the older
/// store a directory of `*.jsonl` files indexed by `sessions.json`. Both use the
/// same event shape, and `openclaw doctor --fix` imports the logs into the
/// database while leaving the originals behind — so the same call can be present
/// in both. The record identity is therefore CROSS-STORE: event id, timestamp
/// and the input/output counts, which the SQLite row, the retained JSONL line
/// and a /fork copy all share. That keeps one call from being counted twice.
///
/// A `reasoningTokens` value is documented as a subset of output, so it is never
/// added a second time; it stands in only when output was not reported. Rows
/// describing transcript plumbing (`openclaw-transcript`, `delivery-mirror`,
/// `gateway-injected`) are skipped: they are all-zero bookkeeping, not a real
/// zero-usage call. Codex app-server rollups mirrored under an agent and .zst
/// archives are not read here.
///
/// The product has been renamed twice; the same store shapes live under
/// `~/.clawdbot`, `~/.moltbot` and `~/.moldbot` as well as `~/.openclaw`, and
/// every existing root is scanned. Cross-store identity still collapses an
/// import that left the same event in two trees.
/// </summary>
public static class OpenClawSessionReader
{
    /// <summary>Current and legacy product directories, under the user home.</summary>
    public static readonly IReadOnlyList<string> ProductDirectories =
        new[] { ".openclaw", ".clawdbot", ".moltbot", ".moldbot" };

    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return Array.Empty<AgentUsageRecord>();
        var roots = ProductDirectories
            .Select(name => Path.Combine(home, name))
            .ToArray();
        return roots.Any(Directory.Exists) ? RecordsFromRoots(roots) : Array.Empty<AgentUsageRecord>();
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var rootsList = roots.ToList();
        var (sqliteRecords, sqliteIncomplete) = SqliteRecords(rootsList);
        var (jsonlRecords, jsonlIncomplete) = JsonlRecords(rootsList);
        var all = sqliteRecords.Concat(jsonlRecords).ToList();
        return (sqliteIncomplete || jsonlIncomplete) ? all.Select(MarkPartial).ToList() : all;
    }

    // --- SQLite store -----------------------------------------------------------------------

    private static AgentUsageRecord MarkPartial(AgentUsageRecord record) => record with { IsPartial = true };

    public static (IReadOnlyList<AgentUsageRecord> Records, bool Incomplete) SqliteRecords(IReadOnlyList<string> roots)
    {
        var records = new List<AgentUsageRecord>();
        var incomplete = false;

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] databases;
            try
            {
                databases = Directory.EnumerateFiles(root, "openclaw-agent.sqlite", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var database in databases)
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = database,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString();

                try
                {
                    using var connection = new SqliteConnection(connectionString);
                    connection.Open();

                    var metadata = SessionMetadata(connection);
                    var carried = new Dictionary<string, (string? Model, string? Provider)>();

                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT session_id, seq, event_json FROM transcript_events "
                        + "WHERE event_json LIKE '%\"usage\"%' OR event_json LIKE '%\"model_change\"%' "
                        + "OR event_json LIKE '%model-snapshot%' ORDER BY session_id, seq";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(0) || reader.IsDBNull(2)) continue;
                        var session = reader.GetString(0);
                        var seq = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                        string? rowJson = reader.GetString(2);

                        JsonElement? rowMaybe = null;
                        try { using var doc = JsonDocument.Parse(rowJson); rowMaybe = doc.RootElement.Clone(); }
                        catch (JsonException) { }
                        if (rowMaybe is not { } row || row.ValueKind != JsonValueKind.Object) continue;

                        if (ModelBookkeeping(row) is { } update)
                            carried[session] = update;
                        if (MessageEventOf(row) is not { } @event) continue;

                        var hasMeta = metadata.TryGetValue(session, out var meta);
                        var model = @event.Model
                            ?? carried.GetValueOrDefault(session).Model
                            ?? (hasMeta ? meta.Model : null);
                        if (@event.Timestamp is not { } timestamp) { incomplete = true; continue; }
                        if (model is null) { incomplete = true; continue; }

                        records.Add(new AgentUsageRecord
                        {
                            Timestamp = timestamp,
                            Model = model,
                            Tally = @event.Tally,
                            UnclassifiedTokens = @event.Unclassified,
                            SessionID = session,
                            DeduplicationID = DeduplicationID(@event.EventID ?? $"seq-{seq}", timestamp, @event.Tally),
                        });
                    }
                }
                catch (SqliteException)
                {
                    continue; // locked or foreign: nothing to read
                }
            }
        }
        return (records, incomplete);
    }

    /// <summary>`session_windows` is the current join; the older `sessions` table
    /// is used when the former does not exist; null metadata accepted when
    /// neither does.</summary>
    private static Dictionary<string, (string? Model, string? Provider)> SessionMetadata(SqliteConnection connection)
    {
        var map = new Dictionary<string, (string? Model, string? Provider)>();
        foreach (var table in new[] { "session_windows", "sessions" })
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT session_id, model_provider, model FROM {table}";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    map[reader.GetString(0)] = (
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(1) ? null : reader.GetString(1));
                }
                if (map.Count > 0) break;
            }
            catch (SqliteException) { }
        }
        return map;
    }

    // --- JSONL store --------------------------------------------------------------------------

    public static (IReadOnlyList<AgentUsageRecord> Records, bool Incomplete) JsonlRecords(IReadOnlyList<string> roots)
    {
        var registry = SessionRegistry(roots);
        var records = new List<AgentUsageRecord>();
        var incomplete = false;

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                    .Where(IsOpenClawJsonl).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                var session = registry.GetValueOrDefault(Path.GetFileName(file)) ?? UrlSessionID(file);
                (string? Model, string? Provider) carried = (null, null);

                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (IOException) { continue; }

                for (var index = 0; index < lines.Length; index++)
                {
                    var line = lines[index];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JsonElement? rowMaybe = null;
                    try { using var doc = JsonDocument.Parse(line); rowMaybe = doc.RootElement.Clone(); }
                    catch (JsonException) { }
                    if (rowMaybe is not { } row || row.ValueKind != JsonValueKind.Object) continue;

                    if (ModelBookkeeping(row) is { } update) carried = update;
                    if (MessageEventOf(row) is not { } @event) continue;
                    var model = @event.Model ?? carried.Model;
                    if (@event.Timestamp is not { } timestamp) { incomplete = true; continue; }
                    if (model is null) { incomplete = true; continue; }

                    records.Add(new AgentUsageRecord
                    {
                        Timestamp = timestamp,
                        Model = model,
                        Tally = @event.Tally,
                        UnclassifiedTokens = @event.Unclassified,
                        SessionID = session,
                        DeduplicationID = DeduplicationID(@event.EventID ?? $"line-{index}", timestamp, @event.Tally),
                    });
                }
            }
        }
        return (records, incomplete);
    }

    /// <summary>The legacy `sessions.json` registry: sessionFile name → sessionId.</summary>
    public static Dictionary<string, string> SessionRegistry(IReadOnlyList<string> roots)
    {
        var map = new Dictionary<string, string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(root, "sessions.json", SearchOption.AllDirectories).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (var value in document.RootElement.EnumerateObject())
                    {
                        if (value.Value.ValueKind != JsonValueKind.Object) continue;
                        var sessionID = Str(value.Value, "sessionId");
                        var sessionFile = Str(value.Value, "sessionFile");
                        if (sessionID is { } id && sessionFile is { } name)
                            map[Path.GetFileName(name)] = id;
                    }
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
        }
        return map;
    }

    /// <summary>A legacy transcript: `deleted`/`reset` archives still name .jsonl,
    /// the sqlite-import originals are read too; .zst and codex mirrors are not.</summary>
    public static bool IsOpenClawJsonl(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.Contains(".jsonl")) return false;
        if (name.EndsWith(".zst", StringComparison.Ordinal)) return false;
        var normalized = path.Replace('\\', '/');
        if (!normalized.Contains("/sessions/") && !normalized.Contains("/session-sqlite-import-archive/"))
            return false;
        if (normalized.Contains("/codex-home/") || normalized.Contains("/cli-auth/")) return false;
        return true;
    }

    private static string UrlSessionID(string file)
    {
        var name = Path.GetFileName(file);
        var marker = name.IndexOf(".jsonl", StringComparison.Ordinal);
        return marker < 0 ? Path.GetFileNameWithoutExtension(name) : name[..marker];
    }

    // --- events -------------------------------------------------------------------------------

    public sealed record OcEvent(string? EventID, string? Model, string? Provider, DateTimeOffset? Timestamp, TokenTally Tally, int Unclassified);

    public static OcEvent? MessageEventOf(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        if (Str(row, "type") != "message") return null;
        if (!row.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
        if (Str(message, "role") != "assistant") return null;
        if (IsArtifact(row, message)) return null;
        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

        var counts = Counts(usage);
        if (counts.Tally.Total <= 0 && counts.Unclassified <= 0) return null;

        DateTimeOffset? timestamp = null;
        if (message.TryGetProperty("timestamp", out var mts) && mts.ValueKind == JsonValueKind.Number && mts.TryGetInt64(out var mms) && mms > 0)
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mms);
        timestamp ??= row.TryGetProperty("timestamp", out var rts) && rts.ValueKind == JsonValueKind.Number && rts.TryGetInt64(out var rms) && rms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(rms)
            : null;

        return new OcEvent(
            EventID: Str(row, "id"),
            Model: Str(message, "model"),
            Provider: Str(message, "provider"),
            Timestamp: timestamp,
            Tally: counts.Tally,
            Unclassified: counts.Unclassified);
    }

    /// <summary>Rows describing the mirror rather than a call the model made.</summary>
    public static bool IsArtifact(JsonElement row, JsonElement message)
    {
        if (Str(row, "api") == "openclaw-transcript") return true;
        if (Str(message, "api") == "openclaw-transcript") return true;
        var provider = Str(message, "provider") ?? Str(row, "provider");
        var model = Str(message, "model") ?? Str(row, "model");
        return provider == "openclaw" && (model == "delivery-mirror" || model == "gateway-injected");
    }

    /// <summary>The model bookkeeping an event carries, if any.</summary>
    public static (string? Model, string? Provider)? ModelBookkeeping(JsonElement row)
    {
        switch (Str(row, "type"))
        {
            case "model_change":
                return (Str(row, "modelId"), Str(row, "provider"));
            case "custom":
                if (Str(row, "customType") == "model-snapshot" &&
                    row.TryGetProperty("data", out var dataElement) &&
                    dataElement.ValueKind == JsonValueKind.Object)
                    return (Str(dataElement, "modelId"), Str(dataElement, "provider"));
                return null;
            default:
                return null;
        }
    }

    /// <summary>The camelCase usage object. A bare totalTokens with no named kind
    /// is carried as unclassified, never poured into input.</summary>
    public static (TokenTally Tally, int Unclassified) Counts(JsonElement usage)
    {
        int? input = OptInt(usage, "input");
        int? output = OptInt(usage, "output");
        int? cacheRead = OptInt(usage, "cacheRead");
        int? cacheWrite = OptInt(usage, "cacheWrite");
        var reasoning = OptInt(usage, "reasoningTokens") ?? 0;
        int? total = OptInt(usage, "totalTokens");

        var anyKnown = input is not null || output is not null || cacheRead is not null || cacheWrite is not null;
        var tally = new TokenTally(
            Input: input ?? 0,
            CacheWrite: cacheWrite ?? 0,
            CacheRead: cacheRead ?? 0,
            // Reasoning documented as a subset of output: only stands in when
            // output was not reported.
            Output: output ?? reasoning);
        if (total is not { } totalCount) return (tally, 0);
        if (!anyKnown) return (new TokenTally(), totalCount);
        var remainder = totalCount - tally.Total;
        return (tally, remainder > 0 ? remainder : 0);
    }

    /// <summary>The cross-store identity: event id, timestamp and the counts.</summary>
    public static string DeduplicationID(string eventID, DateTimeOffset timestamp, TokenTally tally)
    {
        var milliseconds = timestamp.ToUnixTimeMilliseconds();
        return $"openclaw:{eventID}:{milliseconds}:{tally.Input}:{tally.Output}";
    }

    private static JsonElement ObjectOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object) return default;
        return property;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int? OptInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)property.GetDouble(),
            _ => null,
        };
    }
}

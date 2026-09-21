using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// CodeBuddy and WorkBuddy, Tencent's coding agents; port of upstream
/// TencentBuddyReader. Three shapes dispatched by file:
/// a JSONL transcript under `projects/`, an extension log with
/// `[AgentReporter]` usage lines, and WorkBuddy's aggregate SQLite database.
/// The JSONL and log carry real per-request counts; the database carries only
/// one aggregate quantity per session, reported as `unclassifiedTokens` rather
/// than passed off as fresh input.
///
/// Only the JSONL is the reconcilable channel: its messageId/traceId/line id
/// folds a replayed message. When a transcript is present it is the ONLY thing
/// returned (the fallbacks share no identity with its message ids, so appending
/// them would double count); if the excluded fallback held records, the
/// transcript records are marked `isPartial`. With no transcript the fallback
/// stands alone: the log's lines are counted one by one — a mirrored duplicate
/// is the price of never losing a real request.
/// </summary>
public static class TencentBuddyReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string client, string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = client switch
        {
            "codebuddy" => new[] { Path.Combine(home, ".codebuddy") },
            // `workbuddy` is the 5.0 tree, `workbuddy-ai` the 5.5 one; both can exist.
            "workbuddy" => new[] { Path.Combine(home, ".workbuddy"), Path.Combine(home, ".workbuddy-ai") },
            _ => Array.Empty<string>(),
        };
        return Records(client, roots);
    }

    public static IReadOnlyList<AgentUsageRecord> Records(string client, IReadOnlyList<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                // `binaries` holds the product's own code, not usage.
                files.AddRange(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var normalized = f.Replace('\\', '/');
                        if (normalized.Contains("/binaries/")) return false;
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        return ext is ".jsonl" or ".log" || Path.GetFileName(f) == "workbuddy.db";
                    }));
            }
            catch (Exception) { }
        }
        files.Sort(StringComparer.Ordinal);

        var transcript = new List<AgentUsageRecord>();
        foreach (var file in files.Where(f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
            transcript.AddRange(Jsonl(client, file));

        // The transcript wins and the fallback is not appended: the fallbacks
        // share no identity with the transcript's message ids. When the
        // excluded fallback held records the returned set is only the confirmed
        // subset, marked partial so the doubt is shown.
        var fallback = new List<AgentUsageRecord>();
        var fallbackHadRecords = false;
        foreach (var file in files.Where(f => !f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
        {
            var read = file.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                ? ExtensionLog(client, file, firstOnly: transcript.Count > 0)
                : Sqlite(client, file);
            if (transcript.Count > 0 && read.Count > 0)
            {
                fallbackHadRecords = true;
                break;
            }
            fallback.AddRange(read);
        }
        if (transcript.Count > 0)
        {
            if (fallbackHadRecords)
                transcript = transcript.Select(r => r with { IsPartial = true }).ToList();
            return transcript.OrderBy(r => r.Timestamp).ToList();
        }
        return fallback.OrderBy(r => r.Timestamp).ToList();
    }

    // --- JSONL transcript ----------------------------------------------------------------

    private static IReadOnlyList<AgentUsageRecord> Jsonl(string client, string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var identified = new Dictionary<string, AgentUsageRecord>();
        var unidentified = new List<AgentUsageRecord>();

        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (IOException) { return Array.Empty<AgentUsageRecord>(); }

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(raw); }
            catch (JsonException) { continue; }

            using (document)
            {
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (TimestampOf(row, "timestamp") is not { } timestamp) continue;

                var type = Str(row, "type");
                var role = Str(row, "role");
                var isMessage = type == "message" && role == "assistant";
                var isFunctionCall = type == "function_call";
                if (!isMessage && !isFunctionCall) continue;
                // A record the product itself calls incomplete is not counted.
                if (Str(row, "status") is { } status && status != "completed") continue;

                var message = ObjectOf(row, "message");
                var provider = ObjectOf(row, "providerData");
                var usage = HasObject(message, "usage")
                    ? ObjectOf(message, "usage")
                    : HasObject(provider, "usage")
                        ? ObjectOf(provider, "usage")
                        : ObjectOf(provider, "rawUsage");
                if (usage.ValueKind != JsonValueKind.Object) continue;

                var parts = TranscriptUsage(usage);
                if (parts.Tally.Total + parts.Unclassified <= 0) continue;

                var session = NonBlank(Str(row, "sessionId")) ?? stem;
                var model = EditorLog.ModelID(
                    Str(provider, "model")
                    ?? Str(provider, "requestModelId")
                    ?? Str(message, "model")) ?? client;

                var identity = NonBlank(Str(provider, "messageId"))
                    ?? NonBlank(Str(provider, "traceId"))
                    ?? NonBlank(Str(row, "id"));

                var record = new AgentUsageRecord
                {
                    Timestamp = timestamp,
                    Model = model,
                    Tally = parts.Tally,
                    SessionID = session,
                    Project = EditorLog.Project(Str(row, "cwd")),
                    DeduplicationID = identity is { } id ? $"{client}:{session}:{id}" : null,
                    UnclassifiedTokens = parts.Unclassified,
                };

                // Mirrored writes of one message land on one identity; the more
                // complete snapshot wins rather than both being added.
                if (record.DeduplicationID is { } dedup)
                {
                    if (identified.TryGetValue(dedup, out var existing) &&
                        Total(existing) >= Total(record)) continue;
                    identified[dedup] = record;
                }
                else
                {
                    unidentified.Add(record);
                }
            }
        }

        return identified.Values.Concat(unidentified).ToList();
    }

    private static int Total(AgentUsageRecord record) => record.Tally.Total + record.UnclassifiedTokens;

    private static EditorLog.UsageParts TranscriptUsage(JsonElement usage) => EditorLog.Combine(
        input: EditorLog.FirstCount(usage, "input_tokens", "inputTokens", "prompt_tokens"),
        output: EditorLog.FirstCount(usage, "output_tokens", "outputTokens", "completion_tokens"),
        cacheRead: EditorLog.FirstCount(usage,
            "cache_read_input_tokens", "cacheReadInputTokens", "cacheTokens",
            "prompt_cache_hit_tokens", "cached_tokens"),
        cacheWrite: EditorLog.FirstCount(usage,
            "cache_creation_input_tokens", "cacheCreationInputTokens",
            "cachedWriteTokens", "prompt_cache_write_tokens"),
        reasoning: EditorLog.FirstCount(usage,
            "completion_thinking_tokens", "completionThinkingTokens", "reasoningTokens"),
        total: EditorLog.FirstCount(usage, "total_tokens", "totalTokens"),
        exclusiveInput: EditorLog.FirstCount(usage, "cachedMissTokens", "cacheMissTokens"),
        inputMayIncludeCache: true);

    // --- extension log ---------------------------------------------------------------------

    private static IReadOnlyList<AgentUsageRecord> ExtensionLog(string client, string file, bool firstOnly)
    {
        var workspace = NonBlank(Path.GetFileName(file).Split("__")[0]);

        var models = new Dictionary<string, string>();
        var records = new List<AgentUsageRecord>();

        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (IOException) { return records; }

        foreach (var raw in lines)
        {
            if (!raw.Contains("[CraftInvokableAgent]") && !raw.Contains("[AgentReporter]")) continue;
            if (EditorLog.NaiveTimestamp(raw) is not { } timestamp) continue;

            // "Model prepared" lines carry the model a later report used.
            if (Prepared(raw) is { } prepared)
            {
                models[prepared.Agent] = prepared.Model;
                continue;
            }
            if (Reported(raw) is not { } reported) continue;

            var parts = ExtensionUsage(reported.Usage);
            if (parts.Tally.Total + parts.Unclassified <= 0) continue;

            var model = EditorLog.ModelID(models.GetValueOrDefault(reported.Agent)) ?? client;
            // The line carries no message/request/trace id, so it has no
            // business identity: two real requests in the same second with the
            // same counts are two records; folding by timestamp would silently
            // drop one.
            records.Add(new AgentUsageRecord
            {
                Timestamp = timestamp,
                Model = model,
                Tally = parts.Tally,
                SessionID = reported.Agent,
                Project = workspace,
                UnclassifiedTokens = parts.Unclassified,
            });
            if (firstOnly) return records;
        }
        return records;
    }

    private static EditorLog.UsageParts ExtensionUsage(JsonElement usage) => EditorLog.Combine(
        input: EditorLog.FirstCount(usage, "inputTokens", "prompt_tokens"),
        output: EditorLog.FirstCount(usage, "outputTokens", "output_tokens"),
        cacheRead: EditorLog.FirstCount(usage,
            "cacheTokens", "cachedReadTokens", "cache_read_input_tokens"),
        cacheWrite: EditorLog.FirstCount(usage,
            "cachedWriteTokens", "cacheCreationTokens", "cache_creation_input_tokens"),
        reasoning: EditorLog.FirstCount(usage, "reasoningTokens", "completionThinkingTokens"),
        total: EditorLog.FirstCount(usage, "totalTokens", "total_tokens"),
        exclusiveInput: EditorLog.FirstCount(usage, "cachedMissTokens", "cacheMissTokens"),
        inputMayIncludeCache: true);

    /// <summary>`[CraftInvokableAgent] <agent> ... Model prepared: ... (<model>)`.</summary>
    private static (string Agent, string Model)? Prepared(string line)
    {
        var marker = line.IndexOf("[CraftInvokableAgent]", StringComparison.Ordinal);
        if (marker < 0) return null;
        var agent = Bracketed(line, marker + "[CraftInvokableAgent]".Length);
        if (agent is null) return null;
        var label = line.IndexOf("Model prepared:", marker, StringComparison.Ordinal);
        if (label < 0) return null;
        var rest = line[(label + "Model prepared:".Length)..];
        var open = rest.LastIndexOf('(');
        var close = rest.LastIndexOf(')');
        if (open < 0 || open >= close) return null;
        var model = rest[(open + 1)..close].Trim();
        return model.Length == 0 ? null : (agent, model);
    }

    /// <summary>`[AgentReporter] <agent> ... usage: {...}`.</summary>
    private static (string Agent, JsonElement Usage)? Reported(string line)
    {
        var marker = line.IndexOf("[AgentReporter]", StringComparison.Ordinal);
        if (marker < 0) return null;
        var agent = Bracketed(line, marker + "[AgentReporter]".Length);
        if (agent is null) return null;
        var usageMarker = line.IndexOf("usage:", marker, StringComparison.Ordinal);
        if (usageMarker < 0) return null;
        if (EditorLog.BraceObject(line[(usageMarker + "usage:".Length)..]) is not { } usage) return null;
        return (agent, usage);
    }

    /// <summary>The first `[value]` after index, where the agent id sits.</summary>
    private static string? Bracketed(string line, int from)
    {
        var open = line.IndexOf('[', from);
        if (open < 0) return null;
        var close = line.IndexOf(']', open);
        if (close < 0) return null;
        var value = line[(open + 1)..close].Trim();
        return value.Length == 0 ? null : value;
    }

    // --- WorkBuddy SQLite fallback ----------------------------------------------------------

    private static IReadOnlyList<AgentUsageRecord> Sqlite(string client, string file)
    {
        var records = new List<AgentUsageRecord>();
        if (client != "workbuddy" || !File.Exists(file)) return records;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var sessions = new Dictionary<string, (string? Cwd, string? Model)>();
            using (var sessionCommand = connection.CreateCommand())
            {
                sessionCommand.CommandText = "SELECT id, cwd, model FROM sessions";
                try
                {
                    using var reader = sessionCommand.ExecuteReader();
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(0)) continue;
                        sessions[reader.GetString(0)] = (
                            reader.IsDBNull(1) ? null : reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2));
                    }
                }
                catch (SqliteException) { }
            }

            using (var usageCommand = connection.CreateCommand())
            {
                usageCommand.CommandText = "SELECT session_id, used, updated_at FROM session_usage";
                using var reader = usageCommand.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2)) continue;
                    var sessionID = reader.GetString(0);
                    var used = reader.GetInt64(1);
                    var updated = reader.GetInt64(2);
                    if (used <= 0 || updated <= 0) continue;
                    if (EditorLog.AutoEpoch(updated) is not { } timestamp) continue;

                    var hasSession = sessions.TryGetValue(sessionID, out var session);
                    records.Add(new AgentUsageRecord
                    {
                        Timestamp = timestamp,
                        Model = hasSession && session.Model is { } m ? EditorLog.ModelID(m) ?? "auto" : "auto",
                        Tally = new TokenTally(),
                        SessionID = sessionID,
                        Project = hasSession ? EditorLog.Project(session.Cwd) : null,
                        // One row is the session's whole aggregate, so the
                        // identity includes the write that produced it.
                        DeduplicationID = $"workbuddy:{sessionID}:{updated}",
                        UnclassifiedTokens = (int)Math.Min(used, int.MaxValue),
                        IsAggregate = true,
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

    // --- helpers -----------------------------------------------------------------------------

    private static bool HasObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object) return false;
        return true;
    }

    private static JsonElement ObjectOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object) return default;
        return property;
    }

    private static DateTimeOffset? TimestampOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetInt64(out var millis) && millis > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            case JsonValueKind.String when long.TryParse(property.GetString(), out var millis) && millis > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            case JsonValueKind.String when DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed):
                return parsed;
            default:
                return null;
        }
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

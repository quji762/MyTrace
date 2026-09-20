using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// Unsloth Studio's database, `studio.db` under $UNSLOTH_STUDIO_HOME (else
/// `~/.unsloth/studio`); port of upstream UnslothReader. Two tables carry real
/// counters.
///
/// **`chat_messages` is the measured chat path.** An assistant message's
/// `metadata_json` holds `$.contextUsage` counters, normalized so the four
/// emitted kinds add up to the reported total exactly: a cache read is bounded
/// by the prompt, a cache write by what the read left, the total raised to at
/// least prompt+completion, and the unattributed remainder becomes input.
/// Reasoning is folded into output once (completion already contains it), which
/// is where every price list bills it.
///
/// **`api_usage_events` is the measured API path** with fewer buckets: no cache
/// and no reasoning columns exist, so those stay zero rather than guessed; input
/// is the total minus completion.
///
/// The server-supplied `providerType` route and the store's own cost are not
/// used: a route name the server can rename is not a price key. Rows with no
/// readable timestamp or no message identity are skipped, never dated 1970.
/// </summary>
public static class UnslothReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(
        string? userProfile = null, string? studioHome = null)
    {
        var root = studioHome
            ?? Environment.GetEnvironmentVariable("UNSLOTH_STUDIO_HOME")
            ?? Path.Combine(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unsloth", "studio");
        var database = Path.Combine(root, "studio.db");
        return File.Exists(database) ? Read(database) : Array.Empty<AgentUsageRecord>();
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
            records.AddRange(Chat(connection));
            records.AddRange(Api(connection));
        }
        catch (SqliteException)
        {
            return records; // locked or foreign: nothing to read
        }
        return records;
    }

    // --- the measured chat path -------------------------------------------------------

    private static IReadOnlyList<AgentUsageRecord> Chat(SqliteConnection connection)
    {
        var records = new List<AgentUsageRecord>();
        var messages = ColumnsOf(connection, "chat_messages");
        var threads = ColumnsOf(connection, "chat_threads");
        if (!messages.Contains("id") || !messages.Contains("thread_id") || !messages.Contains("role") ||
            !messages.Contains("metadata_json") || !messages.Contains("created_at") ||
            !threads.Contains("id") || !threads.Contains("model_id"))
            return records;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.thread_id, m.metadata_json, m.created_at, t.model_id
            FROM chat_messages m JOIN chat_threads t ON m.thread_id = t.id
            WHERE m.role = 'assistant'
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var messageID = reader.GetString(0);
            var thread = reader.IsDBNull(1) ? null : reader.GetString(1);
            var metadata = reader.IsDBNull(2) ? null : reader.GetString(2);
            var createdAt = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            var threadModel = reader.IsDBNull(4) ? null : reader.GetString(4);

            if (createdAt <= 0 || string.IsNullOrEmpty(metadata)) continue;

            JsonElement? contextUsage = null, responseDetails = null;
            try
            {
                using var document = JsonDocument.Parse(metadata);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("contextUsage", out var cu) && cu.ValueKind == JsonValueKind.Object)
                        contextUsage = cu.Clone();
                    if (root.TryGetProperty("responseDetails", out var rd) && rd.ValueKind == JsonValueKind.Object)
                        responseDetails = rd.Clone();
                }
            }
            catch (JsonException) { }
            if (contextUsage is null) continue;
            var usage = contextUsage.Value;

            var model =
                (responseDetails is { } detailsElement && Str(detailsElement, "responseModelId") is { } rm) ? rm :
                (Str(usage, "modelId") is { } um) ? um :
                threadModel ?? "unknown";

            var prompt = Clamped(usage, "promptTokens");
            var completion = Clamped(usage, "completionTokens");
            var cacheRead = Math.Min(Clamped(usage, "cachedTokens"), prompt);
            var cacheWrite = Math.Min(Clamped(usage, "cacheWriteTokens"), prompt - cacheRead);
            var total = Math.Max(Clamped(usage, "totalTokens"), prompt + completion);
            if (total <= 0) continue;

            // Reasoning is part of the completion and folds into output once.
            var fresh = total - completion - cacheRead - cacheWrite;
            records.Add(new AgentUsageRecord
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(createdAt),
                Model = model,
                Tally = new TokenTally(
                    Input: Math.Max(0, fresh),
                    CacheWrite: cacheWrite,
                    CacheRead: cacheRead,
                    Output: completion),
                SessionID = thread ?? $"unsloth:chat:{messageID}",
                DeduplicationID = $"unsloth:chat:{messageID}",
            });
        }
        return records;
    }

    // --- the measured API path ----------------------------------------------------------

    private static IReadOnlyList<AgentUsageRecord> Api(SqliteConnection connection)
    {
        var records = new List<AgentUsageRecord>();
        var columns = ColumnsOf(connection, "api_usage_events");
        if (!columns.Contains("id") || !columns.Contains("endpoint") || !columns.Contains("model") ||
            !columns.Contains("prompt_tokens") || !columns.Contains("completion_tokens") ||
            !columns.Contains("total_tokens") || !columns.Contains("created_at"))
            return records;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, endpoint, model, prompt_tokens, completion_tokens, total_tokens, created_at FROM api_usage_events";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var id = reader.GetString(0);
            var model = reader.IsDBNull(2) ? null : reader.GetString(2);
            var createdAt = reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
            if (model is null || createdAt <= 0) continue;

            var prompt = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            var completion = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
            var total = Math.Max(reader.IsDBNull(5) ? 0 : reader.GetInt64(5), prompt + completion);
            if (total <= 0) continue;

            records.Add(new AgentUsageRecord
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(createdAt),
                Model = model,
                Tally = new TokenTally(
                    Input: (int)Math.Max(0, total - completion),
                    Output: (int)completion),
                SessionID = "unsloth:api",
                Title = reader.IsDBNull(1) ? null : reader.GetString(1),
                DeduplicationID = $"unsloth:api:{id}",
            });
        }
        return records;
    }

    // --- helpers -------------------------------------------------------------------------

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

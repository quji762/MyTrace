using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// The three legacy stores that predate the reader catalogue: Devin's CLI
/// transcript database, Grok Build's session updates and Kimi's CLI wire log.
/// Each is normalised into AgentUsageRecords so one Build prices them all; the
/// shared slot/pricing machinery does the rest.
/// </summary>
public static class LegacyStores
{
    // --- Devin CLI ----------------------------------------------------------

    /// <summary>
    /// Devin's CLI keeps its conversations in SQLite at
    /// `~/.local/share/devin/cli/sessions.db`. `message_nodes.chat_message` is
    /// the message as JSON, and an assistant's carries its own metrics:
    /// `metadata.metrics.{input_tokens,output_tokens,cache_read_tokens,
    /// cache_creation_tokens}` with `generation_model` beside them. **Not the
    /// same store as the quota route** — the desktop app's saved plan is a
    /// different database, and the two know nothing about each other.
    ///
    /// A metadata row, an empty metrics object or an undated message is not
    /// usage. Seconds and milliseconds epochs are both tolerated.
    /// </summary>
    public static IReadOnlyList<AgentUsageRecord> DevinCliRecords(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var database = Path.Combine(home, ".local", "share", "devin", "cli", "sessions.db");
        return File.Exists(database) ? DevinCliFromRoots(new[] { Path.GetDirectoryName(database)! }) : Array.Empty<AgentUsageRecord>();
    }

    public static IReadOnlyList<AgentUsageRecord> DevinCliFromRoots(IEnumerable<string> roots)
    {
        var records = new List<AgentUsageRecord>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] databases;
            try
            {
                databases = Directory.EnumerateFiles(root, "sessions.db", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var database in databases)
                records.AddRange(DevinCliDatabase(database));
        }
        return records;
    }

    private static IReadOnlyList<AgentUsageRecord> DevinCliDatabase(string databasePath)
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

            var directories = new Dictionary<string, (string? Directory, string? Title)>();
            using (var sessions = connection.CreateCommand())
            {
                sessions.CommandText = "SELECT id, working_directory, title FROM sessions";
                try
                {
                    using var sessionReader = sessions.ExecuteReader();
                    while (sessionReader.Read())
                    {
                        if (sessionReader.IsDBNull(0)) continue;
                        var id = sessionReader.GetString(0);
                        var directory = sessionReader.IsDBNull(1) ? null : sessionReader.GetString(1);
                        var title = sessionReader.IsDBNull(2) ? null : sessionReader.GetString(2);
                        directories[id] = (directory, title);
                    }
                }
                catch (SqliteException) { }
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT session_id, chat_message, created_at FROM message_nodes";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var session = reader.GetString(0);
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

                    var tally = new TokenTally(
                        Input: IntOf(metrics, "input_tokens"),
                        CacheWrite: IntOf(metrics, "cache_creation_tokens"),
                        CacheRead: IntOf(metrics, "cache_read_tokens"),
                        Output: IntOf(metrics, "output_tokens"));
                    if (tally.Total <= 0) continue;

                    // Seconds and milliseconds are both tolerated.
                    if (reader.IsDBNull(2)) continue;
                    var seconds = reader.GetInt64(2);
                    if (seconds <= 0) continue;
                    var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
                        seconds > 10_000_000_000 ? seconds : seconds * 1000);

                    var model = metadata.TryGetProperty("generation_model", out var gm) &&
                                gm.ValueKind == JsonValueKind.String &&
                                gm.GetString() is { } generation && generation.Length > 0
                        ? generation
                        : "devin";
                    var directory = directories.GetValueOrDefault(session).Directory;
                    records.Add(new AgentUsageRecord
                    {
                        Timestamp = timestamp,
                        Model = model,
                        Tally = tally,
                        SessionID = session,
                        Title = directories.GetValueOrDefault(session).Title,
                        Project = EditorLog.Project(directory),
                        DeduplicationID = $"devin-cli:{databasePath}:{session}:{timestamp.ToUnixTimeMilliseconds()}:{tally.Total}",
                        IsAggregate = false,
                    });
                }
            }
        }
        catch (Exception) { }
        return records;
    }

    // --- Grok Build ---------------------------------------------------------

    /// <summary>
    /// Grok Build's transcripts, `~/.grok/sessions/&lt;percent-encoded working
    /// directory&gt;/&lt;session id&gt;/updates.jsonl`. One line per event; the
    /// one that counts is `sessionUpdate == "turn_completed"` with its `usage`.
    ///
    /// **`modelUsage` is what names the model**, and a turn can touch more
    /// than one — per-model counts are read from it and the flat totals beside
    /// it are used only when it is absent. The opening `user_message_chunk`
    /// gives an otherwise-uuid row its title. Reasoning is folded into output.
    /// </summary>
    public static IReadOnlyList<AgentUsageRecord> GrokRecords(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".grok", "sessions");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : GrokFromRoots(new[] { root });
    }

    public static IReadOnlyList<AgentUsageRecord> GrokFromRoots(IEnumerable<string> roots)
    {
        var records = new List<AgentUsageRecord>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(root, "updates.jsonl", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                var runDirectory = Path.GetDirectoryName(file)!;
                var run = Path.GetFileName(runDirectory);

                // The folder is the working directory, percent-encoded.
                var project = Uri.UnescapeDataString(Path.GetFileName(Path.GetDirectoryName(runDirectory)!));
                var projectDirectory = Project(project);

                var title = default(string?);
                var lineIndex = 0;
                foreach (var raw in File.ReadLines(file))
                {
                    lineIndex++;
                    JsonDocument document;
                    try { document = JsonDocument.Parse(raw); }
                    catch (JsonException) { continue; }
                    using (document)
                    {
                        var line = document.RootElement;
                        if (line.ValueKind != JsonValueKind.Object ||
                            !line.TryGetProperty("params", out var parameters) ||
                            parameters.ValueKind != JsonValueKind.Object ||
                            !parameters.TryGetProperty("update", out var update) ||
                            update.ValueKind != JsonValueKind.Object)
                            continue;

                        // The opening prompt, for a row that would otherwise be
                        // a uuid.
                        if (title is null &&
                            update.TryGetProperty("sessionUpdate", out var kind) &&
                            kind.ValueKind == JsonValueKind.String &&
                            kind.GetString() == "user_message_chunk" &&
                            TitleOf(update) is { } opening)
                            title = opening;

                        if (!update.TryGetProperty("sessionUpdate", out var kind2) ||
                            kind2.ValueKind != JsonValueKind.String ||
                            kind2.GetString() != "turn_completed" ||
                            !update.TryGetProperty("usage", out var usage) ||
                            usage.ValueKind != JsonValueKind.Object)
                            continue;
                        if (!line.TryGetProperty("timestamp", out var ts) ||
                            !ts.TryGetDouble(out var seconds) || seconds <= 0)
                            continue;
                        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));

                        // modelUsage names the model; the flat totals beside it
                        // are only the fallback when it is absent.
                        var perModel = new List<(string Model, JsonElement Counts)>();
                        if (usage.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var entry in mu.EnumerateObject())
                                if (entry.Value.ValueKind == JsonValueKind.Object)
                                    perModel.Add((entry.Name, entry.Value));
                        }
                        if (perModel.Count == 0) perModel.Add(("grok", usage));

                        foreach (var (model, counts) in perModel)
                        {
                            var tally = new TokenTally(
                                Input: IntOf(counts, "inputTokens"),
                                CacheWrite: IntOf(counts, "cacheCreationTokens"),
                                CacheRead: IntOf(counts, "cachedReadTokens"),
                                Output: IntOf(counts, "outputTokens") + IntOf(counts, "reasoningTokens"));
                            if (tally.Total <= 0) continue;

                            records.Add(new AgentUsageRecord
                            {
                                Timestamp = timestamp,
                                Model = model,
                                Tally = tally,
                                SessionID = run,
                                Title = title,
                                Project = projectDirectory,
                                DeduplicationID = $"grok:{file}:{lineIndex}:{model}:{tally.Total}",
                            });
                        }
                    }
                }
            }
        }
        return records;
    }

    /// <summary>The first line of the opening user message, if it reads as a
    /// usable title.</summary>
    private static string? TitleOf(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content)) return null;
        var text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Object when content.TryGetProperty("text", out var t) &&
                                      t.ValueKind == JsonValueKind.String => t.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        var first = text!.Split('\n')[0].Trim();
        return first.Length == 0 ? null : first.Length > 60 ? first[..60] : first;
    }

    // --- Kimi CLI -----------------------------------------------------------

    /// <summary>
    /// Kimi's CLI writes the raw exchange to `wire.jsonl` under
    /// `~/.kimi/sessions/&lt;session id&gt;/`. `message.payload.token_usage`
    /// carries `input_other` (fresh input, the cache figures counted beside it
    /// — the same arrangement Claude Code uses, the opposite of Codex's),
    /// `input_cache_read`, `input_cache_creation` and `output`.
    ///
    /// **It names no model, anywhere.** Its tokens are counted and never
    /// costed — the same answer the pane gives for any model with no published
    /// price, arrived at one step earlier. The id below is a placeholder so
    /// the buckets have a key, deliberately one no price list can match.
    /// The session's own state.json keeps the title beside the wire log.
    /// </summary>
    public static IReadOnlyList<AgentUsageRecord> KimiRecords(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".kimi", "sessions");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : KimiFromRoots(new[] { root });
    }

    public static IReadOnlyList<AgentUsageRecord> KimiFromRoots(IEnumerable<string> roots)
    {
        const string model = "kimi (unnamed)";
        var records = new List<AgentUsageRecord>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(root, "wire.jsonl", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                var sessionDirectory = Path.GetDirectoryName(file)!;
                var title = TitleBesideWire(file);
                var lineIndex = 0;
                foreach (var raw in File.ReadLines(file))
                {
                    lineIndex++;
                    JsonDocument document;
                    try { document = JsonDocument.Parse(raw); }
                    catch (JsonException) { continue; }
                    using (document)
                    {
                        var line = document.RootElement;
                        if (line.ValueKind != JsonValueKind.Object ||
                            !line.TryGetProperty("message", out var message) ||
                            message.ValueKind != JsonValueKind.Object ||
                            !message.TryGetProperty("payload", out var payload) ||
                            payload.ValueKind != JsonValueKind.Object ||
                            !payload.TryGetProperty("token_usage", out var usage) ||
                            usage.ValueKind != JsonValueKind.Object)
                            continue;

                        var tally = new TokenTally(
                            Input: IntOf(usage, "input_other"),
                            CacheWrite: IntOf(usage, "input_cache_creation"),
                            CacheRead: IntOf(usage, "input_cache_read"),
                            Output: IntOf(usage, "output"));
                        if (tally.Total <= 0) continue;

                        if (!line.TryGetProperty("timestamp", out var ts)) continue;
                        double seconds = ts.ValueKind switch
                        {
                            JsonValueKind.Number => ts.TryGetDouble(out var s) ? s : 0,
                            JsonValueKind.String => double.TryParse(ts.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0,
                            _ => 0,
                        };
                        if (seconds <= 0) continue;
                        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
                            (long)(seconds > 10_000_000_000 ? seconds : seconds * 1000));

                        records.Add(new AgentUsageRecord
                        {
                            Timestamp = timestamp,
                            Model = model,
                            Tally = tally,
                            SessionID = Path.GetFileName(sessionDirectory),
                            Title = title,
                            DeduplicationID = $"kimi:{file}:{lineIndex}:{tally.Total}",
                        });
                    }
                }
            }
        }
        return records;
    }

    /// <summary>The state.json beside the wire log carries `custom_title`.</summary>
    private static string? TitleBesideWire(string wirePath)
    {
        try
        {
            var statePath = Path.Combine(Path.GetDirectoryName(wirePath)!, "state.json");
            if (!File.Exists(statePath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("custom_title", out var custom) ||
                custom.ValueKind != JsonValueKind.String ||
                custom.GetString() is not { } title || title.Length == 0)
                return null;
            var first = title.Split('\n')[0].Trim();
            return first.Length == 0 ? null : first.Length > 60 ? first[..60] : first;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // --- shared helpers -----------------------------------------------------

    private static int IntOf(JsonElement element, string name) =>
        EditorLog.FirstCount(element, name) ?? 0;

    private static string? Project(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? null : name;
    }
}

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// Which CLI transcript/store families the scanner knows; the enum keys the
/// per-source cache files and locator roots.
/// </summary>
public enum TranscriptKind
{
    ClaudeCode,
    Codex,
    OpenCode,
    CherryStudio,
    Cline,
    Amp,
}

public static partial class TranscriptLocator
{
    /// <summary>
    /// OpenCode (and Kilo CLI — a fork sharing the schema) keeps a SQLite store.
    /// Upstream reads `~/.local/share/opencode/`; on Windows the CLI writes under
    /// %USERPROFILE%\.local\share\opencode\ (the same path it uses elsewhere) and
    /// %LOCALAPPDATA% variants are probed as fallbacks.
    /// </summary>
    public static string? OpenCodeRoot(string? userProfile = null)
    {
        var candidates = new List<string>();
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            candidates.Add(Path.Combine(home, ".local", "share", "opencode"));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            candidates.Add(Path.Combine(localAppData, "opencode"));

        return candidates.FirstOrDefault(Directory.Exists);
    }
}

public static partial class TranscriptScannerExtensions
{
    /// <summary>Locator root for a kind, shared with <see cref="TranscriptLocator"/>.</summary>
    public static string? RootFor(this TranscriptKind kind, string? userProfile = null) => kind switch
    {
        TranscriptKind.ClaudeCode => TranscriptLocator.ClaudeRoot(userProfile),
        TranscriptKind.Codex => TranscriptLocator.CodexRoot(userProfile),
        TranscriptKind.OpenCode => TranscriptLocator.OpenCodeRoot(userProfile),
        _ => null,
    };
}

/// <summary>
/// OpenCode's store, and Kilo CLI's — the same schema, because Kilo is a fork of
/// it down to the migrations; port of upstream OpenCodeStore.
///
/// ```sql
/// session(id, project_id, slug, directory, title, …)
/// message(id, session_id, time_created, time_updated, data)
/// ```
///
/// `data` is the message as JSON; an assistant's carries everything a ledger
/// needs. **Its own `cost` is ignored** — it is whatever OpenCode's own table said
/// at the time, is zero for a plan it has no rate for, and would put two
/// differently-sourced figures in one total. Everything is priced from
/// ModelPrices like the rest of the page.
///
/// **Reasoning tokens are counted as output**, which is where every price list
/// bills them and where the two CLIs' own counts already put them.
/// </summary>
public static class OpenCodeStoreReader
{
    public static UsageLedger LedgerAt(string databasePath, ModelPrices prices, TimeZoneInfo? timeZone = null)
    {
        if (!File.Exists(databasePath)) return UsageLedger.EmptyLedger;

        var buckets = new Dictionary<string, Dictionary<string, TokenTally>>();
        var sessionsMeta = new Dictionary<string, (string? Slug, string? Title, string? Directory)>();

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly, // read-only and in place: the agent may be running
            Pooling = false,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            // Sessions first, so a message row can resolve its own name/project.
            using (var sessionCommand = connection.CreateCommand())
            {
                sessionCommand.CommandText = "SELECT id, slug, title, directory FROM session";
                using var reader = sessionCommand.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (id is null) continue;
                    sessionsMeta[id] = (
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3));
                }
            }

            using (var messageCommand = connection.CreateCommand())
            {
                messageCommand.CommandText = "SELECT session_id, data FROM message";
                using var reader = messageCommand.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                    var sessionId = reader.GetString(0);
                    var dataText = reader.GetString(1);

                    System.Text.Json.JsonDocument? document = null;
                    try
                    {
                        document = System.Text.Json.JsonDocument.Parse(dataText);
                        var root = document.RootElement;
                        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        if (!root.TryGetProperty("role", out var role) || role.GetString() != "assistant") continue;
                        var model = root.TryGetProperty("modelID", out var modelElement) ? modelElement.GetString() : null;
                        if (string.IsNullOrEmpty(model)) continue;
                        if (!root.TryGetProperty("tokens", out var counts) ||
                            counts.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        if (DateIn(root) is not { } at) continue;

                        // The cache split lives INSIDE the tokens object.
                        counts.TryGetProperty("cache", out var cacheElement);
                        var cache = cacheElement.ValueKind == System.Text.Json.JsonValueKind.Object
                            ? cacheElement
                            : default;

                        var tally = new TokenTally(
                            Input: IntOf(counts, "input"),
                            CacheWrite: IntOf(cache, "write"),
                            CacheRead: IntOf(cache, "read"),
                            // Reasoning bills as output.
                            Output: IntOf(counts, "output") + IntOf(counts, "reasoning"));
                        if (tally.Total <= 0) continue;

                        var slot = TranscriptParser.SlotKeyFor(at);
                        if (!buckets.TryGetValue(slot, out var models))
                            buckets[slot] = models = new Dictionary<string, TokenTally>();
                        models[model!] = models.GetValueOrDefault(model!, new TokenTally()) + tally;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        continue; // a message whose data is not the shape we know
                    }
                    finally
                    {
                        document?.Dispose();
                    }
                }
            }
        }
        catch (SqliteException)
        {
            return UsageLedger.EmptyLedger; // a locked or foreign store is "nothing to read"
        }

        return TranscriptParser.Price(buckets, prices, timeZone);
    }

    /// <summary>`time.created` in milliseconds; a message with no time contributes nothing.</summary>
    private static DateTimeOffset? DateIn(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("time", out var time) || time.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;
        if (!time.TryGetProperty("created", out var created) || !created.TryGetInt64(out var millis))
            return null;
        if (millis <= 0) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(millis);
    }

    private static int IntOf(System.Text.Json.JsonElement element, string name)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return 0;
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when property.TryGetInt32(out var i) => i,
            System.Text.Json.JsonValueKind.Number => (int)property.GetDouble(),
            System.Text.Json.JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => 0,
        };
    }
}

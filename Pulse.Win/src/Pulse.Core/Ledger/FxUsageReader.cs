using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Fx's per-session usage snapshot; port of upstream FxUsageReader.
/// `~/.fx/sessions/&lt;sessionId&gt;/usage-v2.json` holds a `snapshot` and a `models`
/// array; `session.json` in the same directory carries the session's timing and
/// workspace, and `~/.fx/sessions/index.json` carries its title.
///
/// One record per model, each a cumulative session total: the whole snapshot is
/// the increment. A snapshot whose `models` array is empty but whose aggregate
/// has usage still emits once under `fx-unknown`, so totals are not lost when
/// the product groups nothing.
///
/// Reasoning is LEFT OUT: Fx reports a per-model `reasoning_tokens` beside
/// `output_tokens` and states no token total, so nothing says whether reasoning
/// is already inside output. The reported output is kept and the ambiguous
/// reasoning figure is not counted — an undercount rather than a guess; a
/// reported reasoning count marks the record partial.
///
/// One timestamp per session, `updated_at_ms` else `created_at_ms`; a snapshot
/// with neither is skipped. Every model record is marked aggregate because none
/// has its own call time. `total_cost` is Fx's own dollars and is not read as
/// tokens. The global `~/.fx/usage.jsonl` stream has no session id and is not
/// scanned.
/// </summary>
public static class FxUsageReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".fx", "sessions");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoot(root);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoot(string root)
    {
        var titles = Titles(root);
        var records = new List<AgentUsageRecord>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "usage-v2.json", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                JsonElement rootElement;
                try { rootElement = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone(); }
                catch (JsonException) { continue; }
                if (rootElement.ValueKind != JsonValueKind.Object) continue;
                if (!rootElement.TryGetProperty("snapshot", out var snapshot) ||
                    snapshot.ValueKind != JsonValueKind.Object) continue;

                var directory = Path.GetDirectoryName(file)!;
                var sessionID = Str(rootElement, "session_id") ?? Path.GetFileName(directory);

                // session.json sidecar: timing and workspace.
                JsonElement? sidecar = null;
                var sidecarPath = Path.Combine(directory, "session.json");
                if (File.Exists(sidecarPath))
                {
                    try
                    {
                        var parsed = JsonDocument.Parse(File.ReadAllText(sidecarPath));
                        if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                            sidecar = parsed.RootElement.Clone();
                        parsed.Dispose();
                    }
                    catch (JsonException) { }
                }

                // A zero updated_at_ms is "unset", so created_at_ms is tried.
                DateTimeOffset? timestamp = null;
                if (sidecar is { } side)
                {
                    if (side.TryGetProperty("updated_at_ms", out var updated) &&
                        updated.TryGetInt64(out var updatedMs) && updatedMs > 0)
                        timestamp = DateTimeOffset.FromUnixTimeMilliseconds(updatedMs);
                    if (timestamp is null &&
                        side.TryGetProperty("created_at_ms", out var created) &&
                        created.TryGetInt64(out var createdMs) && createdMs > 0)
                        timestamp = DateTimeOffset.FromUnixTimeMilliseconds(createdMs);
                }
                if (timestamp is null) continue; // not dated from the file's mtime

                var workspace = sidecar is { } s && Str(s, "workspace_root") is { } ws ? ws : null;
                var title = titles.GetValueOrDefault(sessionID);

                var hasModels = snapshot.TryGetProperty("models", out var models) &&
                                models.ValueKind == JsonValueKind.Array &&
                                models.GetArrayLength() > 0;

                if (hasModels)
                {
                    foreach (var entry in models.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object) continue;
                        var reasoning = IntOf(entry, "reasoning_tokens");
                        var tally = new TokenTally(
                            Input: IntOf(entry, "input_tokens"),
                            CacheWrite: IntOf(entry, "cache_write_tokens"),
                            CacheRead: IntOf(entry, "cache_read_tokens"),
                            // Reasoning containment undeclared: output kept whole.
                            Output: IntOf(entry, "output_tokens"));
                        if (tally.Total <= 0) continue;

                        var model = Str(entry, "model") ?? "fx-unknown";
                        records.Add(new AgentUsageRecord
                        {
                            Timestamp = timestamp.Value,
                            Model = model,
                            Tally = tally,
                            IsAggregate = true,
                            IsPartial = reasoning > 0,
                            SessionID = sessionID,
                            SessionName = sessionID,
                            Title = title,
                            Project = Project(workspace),
                            DeduplicationID = $"fx:{sessionID}:{model}",
                        });
                    }
                }
                else
                {
                    // The aggregate still carries usage: emit once under
                    // fx-unknown so totals are not lost.
                    var reasoning = IntOf(snapshot, "reasoning_tokens");
                    var tally = new TokenTally(
                        Input: IntOf(snapshot, "input_tokens"),
                        CacheWrite: IntOf(snapshot, "cache_write_tokens"),
                        CacheRead: IntOf(snapshot, "cache_read_tokens"),
                        Output: IntOf(snapshot, "output_tokens"));
                    if (tally.Total <= 0) continue;

                    records.Add(new AgentUsageRecord
                    {
                        Timestamp = timestamp.Value,
                        Model = "fx-unknown",
                        Tally = tally,
                        IsAggregate = true,
                        IsPartial = reasoning > 0,
                        SessionID = sessionID,
                        SessionName = sessionID,
                        Title = title,
                        Project = Project(workspace),
                        DeduplicationID = $"fx:{sessionID}:fx-unknown",
                    });
                }
            }
        }
        catch (Exception)
        {
            return records; // an unreadable tree reads short, not fatal
        }
        return records;
    }

    /// <summary>index.json's `sessions[&lt;id&gt;].title`, where present.</summary>
    private static Dictionary<string, string> Titles(string root)
    {
        var titles = new Dictionary<string, string>();
        var indexFile = Path.Combine(root, "index.json");
        if (!File.Exists(indexFile)) return titles;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(indexFile));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("sessions", out var sessions) &&
                sessions.ValueKind == JsonValueKind.Object)
            {
                foreach (var session in sessions.EnumerateObject())
                {
                    if (session.Value.ValueKind == JsonValueKind.Object &&
                        session.Value.TryGetProperty("title", out var title) &&
                        title.ValueKind == JsonValueKind.String &&
                        title.GetString() is { } text)
                        titles[session.Name] = text;
                }
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return titles;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int IntOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)property.GetDouble(),
            _ => 0,
        };
    }

    private static string? Project(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return null;
        var trimmed = workspace.TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

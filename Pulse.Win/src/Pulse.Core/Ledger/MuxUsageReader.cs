using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Mux's per-workspace usage snapshot; port of upstream MuxUsageReader.
/// `~/.mux/sessions/&lt;workspaceId&gt;/session-usage.json` holds one cumulative
/// session total per `"&lt;provider&gt;:&lt;model&gt;"` key, each bucket shaped
/// `{ tokens, cost_usd }`.
///
/// The snapshot is already one reading per model: a single file read emits one
/// record per model keyed by the model, and the builder's global dedup folds a
/// workspace copied into two roots.
///
/// Reasoning is LEFT OUT, not added and not carried as unknown: the file reports
/// `output` and `reasoning` side by side with no token total, so nothing on disk
/// says whether reasoning is inside output. The reported output is kept and the
/// ambiguous reasoning count is not counted at all — an undercount the file
/// cannot rule out, preferred to a guess in either direction. A reported
/// reasoning count that could not be placed marks the record partial.
///
/// One timestamp per session: lastRequest.timestamp is shared by every model
/// record, so every record is aggregate. A file with no such timestamp is
/// skipped rather than dated from its own modification time.
/// </summary>
public static class MuxUsageReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".mux", "sessions");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoot(root);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoot(string root)
    {
        var records = new List<AgentUsageRecord>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "session-usage.json", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                if (Read(file) is { } sessionRecords)
                    records.AddRange(sessionRecords);
            }
        }
        catch (Exception)
        {
            return records; // an unreadable tree reads short, not fatal
        }
        return records;
    }

    private static IReadOnlyList<AgentUsageRecord>? Read(string file)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object) return null;

        // The same timestamp belongs to every model; without a real one the
        // session has no date and is not guessed from the file's mtime.
        if (!root.TryGetProperty("lastRequest", out var lastRequest) ||
            lastRequest.ValueKind != JsonValueKind.Object ||
            !lastRequest.TryGetProperty("timestamp", out var timestampElement) ||
            !timestampElement.TryGetInt64(out var millis) ||
            millis <= 0)
            return null;
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(millis);

        if (!root.TryGetProperty("byModel", out var byModel) || byModel.ValueKind != JsonValueKind.Object)
            return null;
        var sessionID = Path.GetFileName(Path.GetDirectoryName(file));
        var fallbackModel = Str(lastRequest, "model");

        var records = new List<AgentUsageRecord>();
        foreach (var key in SortedKeys(byModel))
        {
            if (!byModel.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object) continue;
            var reasoning = Bucket(entry, "reasoning") ?? 0;
            var tally = new TokenTally(
                Input: Bucket(entry, "input") ?? 0,
                CacheWrite: Bucket(entry, "cacheCreate") ?? 0,
                CacheRead: Bucket(entry, "cached") ?? 0,
                // No total exists to decide reasoning containment: the reported
                // output is kept and the reasoning count is placed nowhere.
                Output: Bucket(entry, "output") ?? 0);
            var model = ModelName(key) ?? fallbackModel;
            if (model is null || tally.Total <= 0) continue;

            records.Add(new AgentUsageRecord
            {
                Timestamp = timestamp,
                Model = model,
                Tally = tally,
                IsAggregate = true,
                // A reported reasoning count that could not be placed leaves the
                // record short of the session's real work; say so.
                IsPartial = reasoning > 0,
                SessionID = sessionID,
                SessionName = sessionID,
                DeduplicationID = $"mux:{sessionID}:{key}",
            });
        }
        return records;
    }

    /// <summary>`"&lt;provider&gt;:&lt;model&gt;"` → the model after the first colon. The
    /// provider is routing, not part of the model's name, and is dropped rather
    /// than folded into a key no price list could match.</summary>
    public static string? ModelName(string key)
    {
        var colon = key.IndexOf(':');
        var model = colon < 0 ? key : key[(colon + 1)..];
        return string.IsNullOrWhiteSpace(model) ? null : model;
    }

    /// <summary>A named bucket's `tokens`, where the bucket is { tokens, cost_usd }.</summary>
    private static int? Bucket(JsonElement entry, string key)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty(key, out var bucket) ||
            bucket.ValueKind != JsonValueKind.Object ||
            !bucket.TryGetProperty("tokens", out var tokens) ||
            tokens.ValueKind != JsonValueKind.Number)
            return null;
        return tokens.TryGetInt32(out var value) ? value : (int)tokens.GetDouble();
    }

    private static List<string> SortedKeys(JsonElement element)
    {
        var keys = new List<string>();
        foreach (var property in element.EnumerateObject()) keys.Add(property.Name);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }
}

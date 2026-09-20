using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// JCode's sessions and their append-only journals; port of upstream
/// JcodeUsageReader. `~/.jcode/sessions/session_*.json` carries `id`,
/// `provider_key`, `model`, `working_dir` and `messages[]`; the sidecar
/// `session_*.journal.jsonl` carries `{ "meta": …, "append_messages": […] }`
/// lines replayed into the session before anything is emitted, so a message
/// written once and replayed once is one record.
///
/// **A message's cache shape is settled only by an explicit marker.** The
/// Anthropic-style `cache_creation_input_tokens` key means `input_tokens` is
/// already cache-exclusive; an OpenAI-native details object means the cached
/// tokens are a subset of input. A positive `cache_read_input_tokens` with
/// NEITHER marker is ambiguous: the input is carried in `unclassifiedTokens` —
/// never priced as fresh — the cache is not added, and the record is marked
/// partial. Magnitude is never used to infer the convention.
///
/// **Reasoning is left out and marks the record partial**: reasoning_output_tokens
/// is reported beside output with no token total and no containment statement.
/// A message with no token_usage emits nothing; a message whose timestamp is
/// missing is skipped rather than dated from the file.
/// </summary>
public static class JcodeUsageReader
{
    private const CacheShapeKind AnthropicDisjoint = CacheShapeKind.AnthropicDisjoint;
    private const CacheShapeKind OpenAiContained = CacheShapeKind.OpenAiContained;
    private const CacheShapeKind Ambiguous = CacheShapeKind.Ambiguous;

    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".jcode", "sessions");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoot(root);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoot(string root)
    {
        var records = new List<AgentUsageRecord>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "session_*.json", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
                records.AddRange(ReadSession(file));
        }
        catch (Exception)
        {
            return records; // an unreadable tree reads short, not fatal
        }
        return records;
    }

    private static IEnumerable<AgentUsageRecord> ReadSession(string file)
    {
        JsonElement session;
        try
        {
            session = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
        }
        catch (Exception)
        {
            yield break;
        }
        if (session.ValueKind != JsonValueKind.Object) yield break;

        var sessionID = Str(session, "id") ?? Stem(file);
        var workspace = Str(session, "working_dir");
        var sessionModel = Str(session, "model");
        var provider = Str(session, "provider_key");

        // (message, model-in-force when it was appended).
        var messages = new List<(JsonElement Message, string? Model)>();
        if (session.TryGetProperty("messages", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
                messages.Add((row.Clone(), sessionModel));
        }

        // Journal lines update the session's model as they are replayed, so a
        // message takes the meta current when it was appended.
        var journalModel = sessionModel;
        var journalPath = Path.Combine(Path.GetDirectoryName(file)!, $"{Stem(file)}.journal.jsonl");
        if (File.Exists(journalPath))
        {
            string[] journalLines;
            try { journalLines = File.ReadAllLines(journalPath); }
            catch (IOException) { journalLines = Array.Empty<string>(); }

            foreach (var line in journalLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }

                using (document)
                {
                    var entry = document.RootElement;
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    if (entry.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
                        journalModel = Str(meta, "model") ?? journalModel;
                    if (entry.TryGetProperty("append_messages", out var appended) &&
                        appended.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var appendedMessage in appended.EnumerateArray())
                            messages.Add((appendedMessage.Clone(), journalModel));
                    }
                }
            }
        }

        foreach (var (message, model) in messages)
        {
            if (message.ValueKind != JsonValueKind.Object) continue;
            if (!message.TryGetProperty("token_usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
            if (MessageTimestamp(message) is not { } timestamp) continue;

            var reportedInput = IntOf(usage, "input_tokens");
            var cacheRead = IntOf(usage, "cache_read_input_tokens");
            var cacheWrite = IntOf(usage, "cache_creation_input_tokens");
            var reportedOutput = IntOf(usage, "output_tokens");
            var reasoning = IntOf(usage, "reasoning_output_tokens");

            var output = reportedOutput; // reasoning containment unknown: kept whole
            var unclassified = 0;
            var isPartial = reasoning > 0;

            TokenTally tally;
            switch (CacheShape(usage))
            {
                case CacheShapeKind.AnthropicDisjoint:
                    // The schema states input excludes the cache.
                    tally = new TokenTally(reportedInput, cacheWrite, cacheRead, output);
                    break;

                case CacheShapeKind.OpenAiContained:
                    // An explicit native details field states the cache is a
                    // subset of the input.
                    tally = new TokenTally(
                        Input: Math.Max(0, reportedInput - Math.Min(cacheRead, reportedInput)),
                        CacheWrite: cacheWrite, CacheRead: cacheRead, Output: output);
                    break;

                default: // ambiguous
                    if (cacheRead > 0)
                    {
                        // A positive cache with no distinguishing marker: the
                        // input may or may not already contain it, so it cannot
                        // be priced as fresh. It is carried unclassified and the
                        // record marked partial.
                        tally = new TokenTally(Output: output);
                        unclassified = reportedInput;
                        isPartial = true;
                    }
                    else
                    {
                        // No cache in play, so the input is fresh.
                        tally = new TokenTally(Input: reportedInput, Output: output);
                    }
                    break;
            }
            if (tally.Total + unclassified <= 0) continue;

            var name = Str(message, "id")
                ?? $"{sessionID}:{timestamp.ToUnixTimeSeconds()}:{model ?? ""}:"
                + $"{reportedInput}:{cacheWrite}:{cacheRead}:{reportedOutput}";

            yield return new AgentUsageRecord
            {
                Timestamp = timestamp,
                Model = model ?? sessionModel ?? "unknown",
                Tally = tally,
                UnclassifiedTokens = unclassified,
                IsPartial = isPartial,
                SessionID = sessionID,
                SessionName = provider,
                Project = Project(workspace),
                DeduplicationID = $"jcode:{sessionID}:{name}",
            };
        }
    }

    /// <summary>How a usage object says its cached tokens relate to its input.
    /// ONLY an explicit marker settles it; magnitude is never evidence.</summary>
    public enum CacheShapeKind { AnthropicDisjoint, OpenAiContained, Ambiguous }

    public static CacheShapeKind CacheShape(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return CacheShapeKind.Ambiguous;
        if (usage.TryGetProperty("cache_creation_input_tokens", out _)) return CacheShapeKind.AnthropicDisjoint;
        foreach (var detailsKey in new[] { "prompt_tokens_details", "promptTokensDetails", "input_tokens_details", "inputTokensDetails" })
        {
            if (usage.TryGetProperty(detailsKey, out _)) return CacheShapeKind.OpenAiContained;
        }
        return CacheShapeKind.Ambiguous;
    }


    private static DateTimeOffset? MessageTimestamp(JsonElement message)
    {
        if (!message.TryGetProperty("timestamp", out var element)) return null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt64(out var millis) && millis > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            case JsonValueKind.String when DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed):
                return parsed;
            default:
                return null;
        }
    }

    private static string Stem(string file) => Path.GetFileNameWithoutExtension(file);

    private static string? Project(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return null;
        var trimmed = workspace.TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? null : name;
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
}

using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Droid's session store: real totals, no per-reply counts; port of upstream
/// DroidSessionReader. `&lt;session&gt;.settings.json` holds the session's
/// CUMULATIVE usage; a sibling `&lt;session&gt;.jsonl` transcript holds assistant
/// replies with their times but no tokens at all. Splitting a session total
/// across replies would invent a per-reply split nobody reported, so this reader
/// emits ONE AGGREGATE record per session (`isAggregate`: lands on its day,
/// never in an hour profile).
///
/// The input/cache relation is never assumed: a REPORTED total is the only
/// authority — it can prove the cache read inside the prompt or beside it, and
/// one that proves neither becomes `unclassifiedTokens` (the total itself is
/// complete, only its kinds unknown). With NO total and a positive cache, the
/// reported output is kept priced, the input carried as a known unknown, the
/// cache not added, and the record marked `isPartial`. A positive thinking count
/// of undocumented relation to output marks the record partial even with no
/// cache.
///
/// The time is the store's own `providerLockTimestamp`; a session with usage but
/// no locatable time makes the whole run partial. The model id is kept as
/// written (only an explicit `custom:` transport prefix and a bracketed
/// qualifier removed) so a published id like `claude-opus-4.5` is not mangled; a
/// provider with no usable model gets a valueless `*-unknown` placeholder.
/// </summary>
public static class DroidSessionReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".factory", "sessions");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoot(root);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoot(string root)
    {
        var records = new List<AgentUsageRecord>();
        var incomplete = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.settings.json", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var (record, sawUnusable) = Evaluate(file);
                if (record is not null) records.Add(record);
                incomplete |= sawUnusable;
            }
        }
        catch (Exception)
        {
            return records; // an unreadable tree reads short, not fatal
        }
        return incomplete ? records.Select(MarkPartial).ToList() : records;
    }

    public static AgentUsageRecord? RecordAt(string file) => Evaluate(file).Record;

    private static (AgentUsageRecord? Record, bool SawUnusableUsage) Evaluate(string file)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
        }
        catch (Exception)
        {
            return (null, false);
        }
        if (root.ValueKind != JsonValueKind.Object) return (null, false);
        if (!root.TryGetProperty("tokenUsage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
            return (null, false);
        if (Decode(usageElement) is not { } counts) return (null, false);
        if (counts.Tally.Total <= 0 && counts.Unclassified <= 0) return (null, false);

        // The file holds recognized usage; a missing locatable time makes it
        // uncountable rather than a non-reading.
        if (TimestampOf(root, "providerLockTimestamp") is not { } timestamp)
            return (null, true);

        var session = SessionID(file);
        var model = Normalize(Str(root, "model"))
            ?? TranscriptModel(sibling: file)
            ?? ProviderDefault(Str(root, "providerLock"));
        if (model is null) return (null, true);

        return (new AgentUsageRecord
        {
            Timestamp = timestamp,
            Model = model,
            Tally = counts.Tally,
            SessionID = session,
            DeduplicationID = $"droid:{session}",
            UnclassifiedTokens = counts.Unclassified,
            IsAggregate = true,
            IsPartial = counts.IsPartial,
        }, false);
    }

    /// <summary>The file stem with `.settings` removed.</summary>
    public static string SessionID(string file)
    {
        var name = Path.GetFileName(file);
        if (name.EndsWith(".settings.json", StringComparison.Ordinal))
            return name[..^".settings.json".Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Keeps the model id as the store wrote it. Only an explicit
    /// `custom:` transport prefix and a bracketed qualifier are removed; case
    /// and punctuation (`claude-opus-4.5`) are part of the pricing identity.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw;
        if (value.StartsWith("custom:", StringComparison.Ordinal))
            value = value["custom:".Length..];
        value = System.Text.RegularExpressions.Regex.Replace(value, "\\[[^\\]]*\\]", "").Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>A transcript system-reminder line names the model when the
    /// settings file does not.</summary>
    public static string? TranscriptModel(string sibling)
    {
        var transcript = Path.Combine(
            Path.GetDirectoryName(sibling)!,
            SessionID(sibling) + ".jsonl");
        if (!File.Exists(transcript)) return null;

        string[] lines;
        try { lines = File.ReadAllLines(transcript); }
        catch (IOException) { return null; }

        foreach (var line in lines)
        {
            var marker = line.IndexOf("Model:", StringComparison.Ordinal);
            if (marker < 0) continue;
            var remainder = line[(marker + "Model:".Length)..].TrimStart(' ', '\t');
            var name = TakeName(remainder);
            if (Normalize(name) is { } normalized) return normalized;
        }
        return null;
    }

    private static string TakeName(string text)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c == '<' || c == '"' || c == '\\') break;
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>A valueless placeholder for a provider with no usable model. Never
    /// a concrete model: naming one would price another model's rates.</summary>
    public static string? ProviderDefault(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        var lower = provider.ToLowerInvariant();
        if (lower.Contains("anthropic") || lower.Contains("claude")) return "claude-unknown";
        if (lower.Contains("openai") || lower.Contains("gpt")) return "gpt-unknown";
        if (lower.Contains("google") || lower.Contains("gemini")) return "gemini-unknown";
        if (lower.Contains("xai") || lower.Contains("grok")) return "grok-unknown";
        return $"{provider}-unknown";
    }

    private static AgentUsageRecord MarkPartial(AgentUsageRecord record) => record with { IsPartial = true };

    /// <summary>
    /// Splits one tokenUsage object. Nil when nothing countable was reported.
    /// With a reported total, the total is reconciled against candidate
    /// identities; one that matches exactly one settles both. With no total, a
    /// positive cache or thinking figure cannot be placed without a guess: the
    /// output is kept, the input carried as unknown, and the record marked
    /// partial.
    /// </summary>
    public static (TokenTally Tally, int Unclassified, bool IsPartial)? Decode(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;

        int? input = OptInt(usage, "inputTokens");
        int? output = OptInt(usage, "outputTokens");
        int? thinking = OptInt(usage, "thinkingTokens");
        int? cacheWrite = OptInt(usage, "cacheCreationTokens");
        int? cacheRead = OptInt(usage, "cacheReadTokens");
        int? total = OptInt(usage, "totalTokens") ?? OptInt(usage, "total") ?? OptInt(usage, "total_tokens");

        if (input is null && output is null && thinking is null && cacheWrite is null && cacheRead is null && total is null)
            return null;

        var i = input ?? 0;
        var o = output ?? 0;
        var k = thinking ?? 0;
        var cw = cacheWrite ?? 0;
        var cr = cacheRead ?? 0;
        var outputBucket = output ?? thinking ?? 0;

        if (total is { } totalCount)
        {
            // Four candidate identities for how reasoning and cache relate to
            // their buckets. A total that matches exactly one settles both.
            var variants = new (int Input, int CacheWrite, int CacheRead, int Output)[]
            {
                (i, cw, cr, o + k),
                (Math.Max(0, i - cr - cw), cw, cr, o + k),
                (i, cw, cr, o),
                (Math.Max(0, i - cr - cw), cw, cr, o),
            };
            foreach (var variant in variants)
            {
                if (variant.Input + variant.CacheWrite + variant.CacheRead + variant.Output == totalCount)
                    return (new TokenTally(
                        Input: variant.Input, CacheWrite: variant.CacheWrite,
                        CacheRead: variant.CacheRead, Output: variant.Output), 0, false);
            }
            // The total itself is complete, only its kinds unknown.
            return (new TokenTally(), totalCount, false);
        }

        if (cr == 0 && cw == 0)
        {
            // No cache ambiguity. A positive thinking count is still of
            // undocumented relation to output: partial rather than dropped.
            return (new TokenTally(Input: i, CacheWrite: 0, CacheRead: 0, Output: outputBucket), 0, k > 0);
        }

        // Positive cache with no total: the relation is not provable. Keep the
        // reported output, carry the input as a known unknown, add nothing for
        // the cache, and say the count is partial.
        return (new TokenTally(Output: outputBucket), i, true);
    }

    private static DateTimeOffset? TimestampOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property))
            return null;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetInt64(out var millis) && millis > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            case JsonValueKind.String when DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed):
                return parsed;
            default:
                return null;
        }
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

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }
}

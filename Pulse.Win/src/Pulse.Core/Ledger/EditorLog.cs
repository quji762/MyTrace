using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Shared arithmetic for structured-log readers; port of the parts of upstream
/// EditorLogSupport the ported readers use. Nothing here invents a count: a key
/// that is missing or zero stays zero, an unreadable line is skipped, and the
/// only timestamp a record gets is one the store actually wrote.
/// </summary>
public static class EditorLog
{
    public sealed record UsageParts(TokenTally Tally, int Unclassified);

    /// <summary>The first named key that carries a real count.</summary>
    public static int? FirstCount(JsonElement @object, params string[] keys)
    {
        if (@object.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
        {
            if (!@object.TryGetProperty(key, out var property)) continue;
            if (property.ValueKind == JsonValueKind.Number)
            {
                if (property.TryGetInt32(out var i)) return i;
                return (int)property.GetDouble();
            }
        }
        return null;
    }

    /// <summary>`provider/model` → the model after the slash; the provider is
    /// routing, not the name a price list matches.</summary>
    public static string? ModelID(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var slash = raw.LastIndexOf('/');
        return slash < 0 ? raw : raw[(slash + 1)..].Length > 0 ? raw[(slash + 1)..] : raw;
    }

    /// <summary>The last component of a working directory the store stated.</summary>
    public static string? Project(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>An epoch value whose unit the schema fixes per branch: seconds
    /// below 1e10, milliseconds above it (WorkBuddy's updated_at).</summary>
    public static DateTimeOffset? AutoEpoch(long? value)
    {
        if (value is not { } v || v <= 0) return null;
        var seconds = v > 10_000_000_000 ? v / 1000.0 : v;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
    }

    private static readonly string[] NaiveFormats =
    [
        "yyyy/MM/dd HH:mm:ss.SSS", "yyyy/MM/dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.SSS", "yyyy-MM-dd HH:mm:ss",
    ];

    /// <summary>A naive local `YYYY/MM/DD HH:mm:ss[.fff]` prefix, which is what
    /// the Tencent extension log writes. The wall-clock string IS the
    /// timestamp; nothing is filled from a file's mtime or the clock.</summary>
    public static DateTimeOffset? NaiveTimestamp(string line)
    {
        var trimmed = line.TrimStart(' ', '\t');
        foreach (var format in NaiveFormats)
        {
            if (trimmed.Length < format.Length) continue;
            var candidate = trimmed[..format.Length];
            if (DateTime.TryParseExact(candidate, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
        }
        return null;
    }

    /// <summary>The first `{...}` object in the text, balanced. Used to pull a
    /// usage object out of a log line that prefixes it with prose.</summary>
    public static JsonElement? BraceObject(string text)
    {
        var open = text.IndexOf('{');
        if (open < 0) return null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    try
                    {
                        return JsonDocument.Parse(text[open..(i + 1)]).RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Combines one usage object's fields into the four kinds. The documented
    /// relations it honors:
    /// - no named kind but a positive total: the whole total is unclassified;
    /// - `cachedMissTokens` names a fresh-input counter: it wins as input;
    /// - a total equal to input+output (and not the disjoint sum) proves the
    ///   cache sits inside the reported input, which is then reduced;
    /// - a total equal to the disjoint sum proves reasoning is a separate kind
    ///   and folds it into output once;
    /// - a total beyond the disjoint sum leaves the excess unclassified.
    /// </summary>
    public static UsageParts Combine(
        int? input, int? output, int? cacheRead, int? cacheWrite,
        int? reasoning, int? total,
        int? exclusiveInput = null, bool inputMayIncludeCache = true)
    {
        var cacheReadValue = cacheRead ?? 0;
        var cacheWriteValue = cacheWrite ?? 0;
        var reasoningValue = reasoning ?? 0;
        var namesAKind = input is not null || output is not null || cacheRead is not null ||
                         cacheWrite is not null || reasoning is not null || exclusiveInput is not null;

        if (!namesAKind && total is { } bareTotal && bareTotal > 0)
            return new UsageParts(new TokenTally(), bareTotal);

        var reportedInput = input ?? 0;
        var rawOutput = output ?? 0;
        var baseInput = exclusiveInput ?? reportedInput;
        var disjoint = baseInput + rawOutput + cacheReadValue + cacheWriteValue + reasoningValue;

        var inclusive = exclusiveInput is null
            && inputMayIncludeCache
            && total is not null
            && total == reportedInput + rawOutput
            && total != disjoint;

        var freshInput = baseInput;
        if (inclusive)
            freshInput = Math.Max(0, reportedInput - cacheReadValue - cacheWriteValue);

        var unclassified = 0;
        if (total is { } t && t > disjoint) unclassified = t - disjoint;

        // Reasoning is a separate kind only when the store's total counts it
        // separately; a store whose output already contains it keeps the whole
        // reported output.
        var reasoningIsSeparate = total is { } t2 && t2 == disjoint;
        var foldedOutput = reasoningIsSeparate ? rawOutput + reasoningValue : rawOutput;

        return new UsageParts(
            new TokenTally(freshInput, cacheWriteValue, cacheReadValue, foldedOutput),
            unclassified);
    }
}

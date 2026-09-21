using System.Globalization;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Pulse.Core.Ledger;

/// <summary>
/// LM Studio's server logs; port of upstream LMStudioUsageReader.
/// `~/.lmstudio/server-logs/**/*.log` is pretty-printed OpenAI-compatible
/// server output, so the usage is NOT one JSON object per line: each response
/// ends with a balanced `"usage": { … }` block, and the response `id`, `model`
/// and a local log timestamp sit in the text just before it. This reader finds
/// those blocks, reads the facts around them, and keeps no part of a prompt or
/// completion body.
///
/// The timestamp is the log line's own and nothing else: a block with no local
/// `YYYY-MM-DD HH:MM:SS` prefix is skipped — never filled from the file's mtime
/// or the clock.
///
/// The prompt is cache-inclusive and completion includes reasoning: cache read
/// and write are clamped to the prompt, the total is at least prompt +
/// completion, and fresh input is the total minus everything already accounted
/// for — including completion, which is kept whole because its reasoning_tokens
/// detail is a subset the output bucket already counts once. Reasoning is not
/// subtracted and not carried as unknown. Local inference has no money behind
/// it, so no cost is read.
/// </summary>
public static class LmStudioUsageReader
{
    private static readonly string[] PromptKeys = ["prompt_tokens", "promptTokens", "input_tokens", "inputTokens"];
    private static readonly string[] CompletionKeys = ["completion_tokens", "completionTokens", "output_tokens", "outputTokens"];
    private static readonly string[] TotalKeys = ["total_tokens", "totalTokens"];
    private static readonly string[] PromptDetailKeys = ["prompt_tokens_details", "input_tokens_details", "inputTokensDetails"];

    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".lmstudio", "server-logs");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoots(new[] { root });
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var records = new List<AgentUsageRecord>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception) { continue; }
                records.AddRange(RecordsFromText(text, file));
            }
        }
        return records;
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromText(string text, string path)
    {
        var records = new List<AgentUsageRecord>();
        var cursor = 0;
        while (true)
        {
            var marker = text.IndexOf("\"usage\"", cursor, StringComparison.Ordinal);
            if (marker < 0) break;
            if (UsageBlock(text, marker + "\"usage\"".Length) is not { } block)
            {
                cursor = marker + "\"usage\"".Length;
                continue;
            }
            cursor = block.End;

            JsonElement usage;
            try { usage = JsonDocument.Parse(block.Json).RootElement.Clone(); }
            catch (JsonException) { continue; }

            var context = PrecedingContext(text, marker);
            // The timestamp is the log line's own and nothing else.
            if (LogTimestamp(context) is not { } timestamp) continue;
            var model = LastCapture("\"model\"\\s*:\\s*\"([^\"]*)\"", context);
            if (model is null) continue;

            var resolved = Counts(usage);
            if (!resolved.Any) continue;

            var offset = marker;
            var identity = LastCapture("\"id\"\\s*:\\s*\"([^\"]*)\"", context)
                ?? FnvHash($"{path}:{offset}:{model}:{resolved.Tally.Input}:"
                    + $"{resolved.Tally.CacheWrite}:{resolved.Tally.CacheRead}:{resolved.Tally.Output}");

            records.Add(new AgentUsageRecord
            {
                Timestamp = timestamp,
                Model = model,
                Tally = resolved.Tally,
                UnclassifiedTokens = resolved.Unclassified,
                SessionID = $"lmstudio:{path}",
                SessionName = Path.GetFileName(path),
                DeduplicationID = $"lmstudio:{identity}",
            });
        }
        return records;
    }

    /// <summary>The usage counts, with the cache and reasoning clamps applied.</summary>
    public static (TokenTally Tally, int Unclassified, bool Any) Counts(JsonElement usage)
    {
        var prompt = EditorLog.FirstCount(usage, PromptKeys);
        var completion = EditorLog.FirstCount(usage, CompletionKeys);
        var reportedTotal = EditorLog.FirstCount(usage, TotalKeys);

        JsonElement? promptDetails = null;
        foreach (var key in PromptDetailKeys)
        {
            if (usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty(key, out var pd) && pd.ValueKind == JsonValueKind.Object)
            {
                promptDetails = pd;
                break;
            }
        }

        var cached = promptDetails is { } pde && EditorLog.FirstCount(pde, "cached_tokens") is { } c1
            ? c1 : EditorLog.FirstCount(usage, "cached_tokens");
        var cacheCreation = promptDetails is { } pde2 && EditorLog.FirstCount(pde2, "cache_creation_input_tokens") is { } c2
            ? c2 : EditorLog.FirstCount(usage, "cache_creation_input_tokens");
        var reasoning = EditorLog.FirstCount(usage, "reasoning_tokens");

        // A bare total with no prompt/completion is real but unclassifiable.
        if (prompt is null && completion is null)
            return reportedTotal is { } rt && rt > 0
                ? (new TokenTally(), rt, true)
                : (new TokenTally(), 0, false);

        var promptTokens = prompt ?? 0;
        var completionTokens = completion ?? 0;
        var cacheRead = Math.Min(cached ?? 0, promptTokens);
        var cacheWrite = Math.Min(cacheCreation ?? 0, Math.Max(0, promptTokens - cacheRead));
        // reasoning_tokens is a detail of completion: already in the output
        // bucket, read only to keep the argument explicit.
        var reasoningTokens = Math.Min(reasoning ?? 0, completionTokens);
        var total = Math.Max(reportedTotal ?? 0, promptTokens + completionTokens);
        var input = Math.Max(0, total - completionTokens - cacheRead - cacheWrite);

        return (new TokenTally(input, cacheWrite, cacheRead, completionTokens), 0, true);
    }

    // --- text scanning ------------------------------------------------------------------

    /// <summary>The balanced `{ … }` after a `"usage"` key, and where it ends.</summary>
    private static (string Json, int End)? UsageBlock(string text, int keyEnd)
    {
        var index = keyEnd;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        if (index >= text.Length || text[index] != ':') return null;
        index++;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        if (index >= text.Length || text[index] != '{') return null;

        var start = index;
        var depth = 0;
        var inString = false;
        var escaped = false;

        while (index < text.Length)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
            }
            else if (character == '"') inString = true;
            else if (character == '{') depth++;
            else if (character == '}')
            {
                depth--;
                if (depth == 0) return (text[start..(index + 1)], index + 1);
            }
            index++;
        }
        return null;
    }

    /// <summary>Up to 4 KB of text before the block, where the facts sit.</summary>
    private static string PrecedingContext(string text, int start)
    {
        var contextStart = Math.Max(0, start - 4096);
        return text[contextStart..start];
    }

    /// <summary>The last capture of pattern in text, so an id repeated in a
    /// listing resolves to the one nearest the block.</summary>
    private static string? LastCapture(string pattern, string text)
    {
        var matches = Regex.Matches(text, pattern);
        if (matches.Count == 0) return null;
        var last = matches[^1];
        return last.Groups.Count > 1 && last.Groups[1].Value.Trim().Length > 0
            ? last.Groups[1].Value
            : null;
    }

    /// <summary>The local `YYYY-MM-DD HH:MM:SS` prefix nearest the block,
    /// parsed in the machine's own time zone.</summary>
    private static DateTimeOffset? LogTimestamp(string text)
    {
        var raw = LastCapture("(\\d{4}-\\d{2}-\\d{2}[ T]\\d{2}:\\d{2}:\\d{2})", text);
        if (raw is null) return null;
        var normalized = raw.Replace('T', ' ');
        if (!DateTime.TryParseExact(normalized, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return null;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <summary>A stable 64-bit FNV-1a hash, hex-encoded: `Hasher` is seeded per
    /// process, so it cannot key a record that must fold across launches; this
    /// can. Used only as the fallback identity for a block that carries no id of
    /// its own.</summary>
    private static string FnvHash(string text)
    {
        ulong hash = 0xcbf2_9ce4_8422_2325;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 0x0000_0100_0000_01b3;
        }
        return hash.ToString("x", CultureInfo.InvariantCulture);
    }
}

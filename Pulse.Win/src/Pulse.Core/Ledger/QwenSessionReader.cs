using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Qwen Code's chat transcripts; port of upstream QwenSessionReader.
/// `~/.qwen/projects/&lt;projectPath&gt;/chats/*.jsonl`; only an `assistant` line
/// with a `usageMetadata` object carries a count.
///
/// The field set is Gemini's documented `usageMetadata`, and Qwen normalizes
/// every backend into it. Google documents `promptTokenCount` as INCLUDING the
/// cached content, and `totalTokenCount` as prompt + candidates + tool +
/// thoughts. Fresh input is therefore the prompt minus the cache read, and
/// reasoning (`thoughtsTokenCount`) is billed as output, added once.
///
/// The declared `totalTokenCount` is used as a CHECK when present: it can prove
/// the cache read sits inside the prompt (subtract) or beside it (disjoint), and
/// when neither identity holds the total is carried as `unclassifiedTokens`
/// rather than split on a guess.
///
/// Record identity prefers the line's own message id; lines without one are
/// keyed by the file fragment's CONTENT DIGEST plus emitted position, so two
/// different fragments of one session never collide while a byte-identical
/// mirror of one file still folds.
/// </summary>
public static class QwenSessionReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".qwen", "projects");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoot(root);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoot(string root)
    {
        var records = new List<AgentUsageRecord>();
        var incomplete = false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var fragment = Digest(file);
                if (fragment is null) continue;
                var project = ProjectSegment(file);
                var fileStem = Path.GetFileNameWithoutExtension(file);
                var fallbackID = string.Join("-", new[] { project, fileStem }.Where(part => !string.IsNullOrEmpty(part)));

                string? sessionId = null;
                var emitted = 0;
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (IOException) { continue; }

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JsonDocument document;
                    try { document = JsonDocument.Parse(line); }
                    catch (JsonException) { continue; }

                    using (document)
                    {
                        var row = document.RootElement;
                        if (row.ValueKind != JsonValueKind.Object) continue;
                        if (Str(row, "type") != "assistant") continue;
                        if (!row.TryGetProperty("usageMetadata", out var metadataElement) ||
                            metadataElement.ValueKind != JsonValueKind.Object) continue;
                        if (Decode(metadataElement) is not { } usage) continue;
                        if (usage.Tally.Total <= 0 && usage.Unclassified <= 0) continue;

                        var model = Str(row, "model");
                        DateTimeOffset? timestamp = null;
                        if (Str(row, "timestamp") is { } stamp &&
                            DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var parsed))
                            timestamp = parsed;
                        if (model is null || timestamp is null)
                        {
                            // Real usage with no model or no locatable time.
                            incomplete = true;
                            continue;
                        }

                        sessionId = Str(row, "sessionId") ?? fallbackID;
                        var messageID = Str(row, "id") ?? Str(row, "messageId");
                        var identity = messageID is { } id
                            ? $"{sessionId}:{id}"
                            : $"{sessionId}:{fragment}:{emitted}";

                        records.Add(new AgentUsageRecord
                        {
                            Timestamp = timestamp.Value,
                            Model = model,
                            Tally = usage.Tally,
                            SessionID = sessionId,
                            Project = project,
                            DeduplicationID = $"qwen:{identity}",
                            UnclassifiedTokens = usage.Unclassified,
                        });
                        emitted++;
                    }
                }
            }
        }
        catch (Exception)
        {
            return records; // an unreadable tree reads short, not fatal
        }
        return incomplete ? records.Select(MarkPartial).ToList() : records;
    }

    private static AgentUsageRecord MarkPartial(AgentUsageRecord record) => record with { IsPartial = true };

    /// <summary>Decodes one Gemini-shaped usageMetadata object. Nil when nothing
    /// countable was reported. A reported total that cannot be reconciled to a
    /// split is carried as unclassifiedTokens, never distributed across kinds.</summary>
    public static (TokenTally Tally, int Unclassified)? Decode(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object) return null;

        int? prompt = OptInt(metadata, "promptTokenCount");
        int? candidates = OptInt(metadata, "candidatesTokenCount");
        int? thoughts = OptInt(metadata, "thoughtsTokenCount");
        int? cached = OptInt(metadata, "cachedContentTokenCount");
        int? total = OptInt(metadata, "totalTokenCount") ?? OptInt(metadata, "total") ?? OptInt(metadata, "total_tokens");

        if (prompt is null && candidates is null && thoughts is null && cached is null && total is null)
            return null;

        var promptCount = prompt ?? 0;
        var cachedCount = cached ?? 0;
        // Gemini bills thinking as output; the normalized output bucket holds it once.
        var output = (candidates ?? 0) + (thoughts ?? 0);

        if (total is { } totalCount)
        {
            var included = promptCount + output;
            var disjoint = promptCount + cachedCount + output;
            if (cachedCount == 0)
            {
                if (totalCount != included)
                    return (new TokenTally(), totalCount);
                return (new TokenTally(Input: promptCount, CacheWrite: 0, CacheRead: 0, Output: output), 0);
            }
            if (totalCount == included && totalCount != disjoint)
            {
                // Proven: the cache read is inside the prompt.
                return (new TokenTally(
                    Input: Math.Max(0, promptCount - cachedCount),
                    CacheWrite: 0, CacheRead: cachedCount, Output: output), 0);
            }
            if (totalCount == disjoint && totalCount != included)
            {
                // Proven: the cache read sits beside the prompt.
                return (new TokenTally(Input: promptCount, CacheWrite: 0, CacheRead: cachedCount, Output: output), 0);
            }
            // Reported total matching neither identity: keep the total, name no kind.
            return (new TokenTally(), totalCount);
        }

        // No total: the documented field semantics (prompt includes the cached
        // content) are what the four buckets are derived from.
        return (new TokenTally(
            Input: Math.Max(0, promptCount - cachedCount),
            CacheWrite: 0, CacheRead: cachedCount, Output: output), 0);
    }

    /// <summary>The &lt;projectPath&gt; segment between `projects` and `chats`.</summary>
    public static string? ProjectSegment(string path)
    {
        var components = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.LastIndexOf(components, "projects");
        if (index < 0 || index + 1 >= components.Length) return null;
        var segment = components[index + 1];
        if (segment == "chats" || segment.Length == 0) return null;
        return segment;
    }

    /// <summary>SHA-256 of a file's bytes, hex — the fragment identity that makes
    /// two different fragments never collide while a byte-identical mirror folds.</summary>
    private static string? Digest(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return null;
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..32];
        }
        catch (Exception)
        {
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

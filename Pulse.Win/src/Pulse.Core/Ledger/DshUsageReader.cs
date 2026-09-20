using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// DeepSeek Harness (`dsh`) transcripts; port of upstream DSHUsageReader.
/// `~/.dsh/sessions/&lt;encoded-cwd&gt;/&lt;session-id&gt;/session.jsonl[.zstd]` holds one
/// event per line. Assistant replies and compaction summaries are both real
/// provider calls and are counted additively.
///
/// The suffix is physical; the magic is the truth: a payload is decompressed
/// only when its bytes carry the zstd frame magic (0x28 B5 2F FD), never because
/// the name ends in `.zstd`. .NET ships no zstd codec, so a compressed frame
/// that cannot be decoded by the runtime is reported as a known incomplete read
/// (isPartial on the run level via the caller) rather than silently shrinking
/// the store — the same honesty upstream's `notes()` provides. Plain JSONL is
/// read directly.
///
/// Reasoning is a subset of output (the format states containment), so the
/// reported `outputTokens` is kept whole — subtracting reasoning and counting
/// it again would double it.
///
/// A forked prefix is not this session's work: events whose `seq` is below the
/// session header's `seedLength` are skipped. The identity is deliberately not
/// session-scoped, so a call copied into a fork collapses with its original.
/// </summary>
public static class DshUsageReader
{
    private const long MaxRawBytes = 64 * 1024 * 1024;
    private const long MaxDecodedBytes = 256 * 1024 * 1024;

    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".dsh", "sessions");
        return !Directory.Exists(root) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoots(new[] { root });
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var records = new List<AgentUsageRecord>();
        foreach (var file in TranscriptFiles(roots))
        {
            if (!Read(file, out var text) || text.Length == 0) continue;
            records.AddRange(ParseLines(text.Split('\n')));
        }
        return records;
    }

    /// <summary>Every transcript: `session.` prefixed names containing `.jsonl`.</summary>
    private static IReadOnlyList<string> TranscriptFiles(IEnumerable<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "session.*.jsonl*", SearchOption.AllDirectories)
                    .Where(path =>
                    {
                        var name = Path.GetFileName(path);
                        return name.StartsWith("session.", StringComparison.Ordinal) && name.Contains(".jsonl");
                    }));
            }
            catch (Exception) { }
        }
        return files;
    }

    /// <summary>The transcript's text: the file itself when plain JSONL, or its
    /// decompressed bytes when the zstd frame magic is actually present. The
    /// magic is the truth, never the suffix.</summary>
    private static bool Read(string file, out string text)
    {
        text = "";
        try
        {
            if (new FileInfo(file).Length > MaxRawBytes) return false;
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length == 0) return false;
            if (IsZstd(bytes)) return false; // no in-box zstd codec: reported incomplete, not silently zero
            text = System.Text.Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The zstd frame magic: 28 B5 2F FD.</summary>
    public static bool IsZstd(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 &&
        bytes[0] == 0x28 && bytes[1] == 0xB5 && bytes[2] == 0x2F && bytes[3] == 0xFD;

    public static IReadOnlyList<AgentUsageRecord> ParseLines(string[] lines)
    {
        var records = new List<AgentUsageRecord>();
        string? sessionID = null;
        string? workspace = null;
        var seedLength = 0;
        string? headerProvider = null;
        string? headerModel = null;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(raw); }
            catch (JsonException) { continue; }

            using (document)
            {
                var @event = document.RootElement;
                if (@event.ValueKind != JsonValueKind.Object) continue;
                var type = Str(@event, "type");
                if (type is null) continue;

                var seq = IntOf(@event, "seq");
                var data = ObjectOf(@event, "data");

                switch (type)
                {
                    case "session":
                        sessionID ??= Str(@event, "id");
                        workspace ??= Str(@event, "cwd");
                        if (seedLength == 0) seedLength = IntOf(@event, "seedLength");
                        break;

                    case "request/header":
                        if (data.ValueKind == JsonValueKind.Object &&
                            data.TryGetProperty("header", out var header) &&
                            header.ValueKind == JsonValueKind.Object &&
                            header.TryGetProperty("config", out var config) &&
                            config.ValueKind == JsonValueKind.Object)
                        {
                            headerProvider = Str(config, "provider") ?? headerProvider;
                            headerModel = Str(config, "model") ?? headerModel;
                        }
                        break;

                    case "assistant/message":
                    case "compaction/summary":
                    {
                        // A forked prefix is not this session's work.
                        if (seq < seedLength) continue;
                        if (data.ValueKind != JsonValueKind.Object ||
                            !data.TryGetProperty("usage", out var usage) ||
                            usage.ValueKind != JsonValueKind.Object) continue;

                        var message = ObjectOf(data, "message");
                        var source = ObjectOf(message, "source");
                        var response = ObjectOf(ObjectOf(source, "replayState"), "response");
                        var model = Str(response, "responseModel")
                            ?? Str(source, "model")
                            ?? headerModel;
                        if (model is null) continue;

                        // Millisecond epoch stamps overflow an int; read long.
                        var milliseconds = Int64Of(@event, "time");
                        if (milliseconds <= 0) continue;
                        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

                        var reasoning = IntOf(usage, "reasoningTokens");
                        var tally = new TokenTally(
                            Input: IntOf(usage, "inputTokens"),
                            CacheWrite: IntOf(usage, "cacheWriteTokens"),
                            CacheRead: IntOf(usage, "cacheReadTokens"),
                            // reasoningTokens is a subset of outputTokens: kept whole.
                            Output: IntOf(usage, "outputTokens"));

                        string identity;
                        if (type == "compaction/summary")
                        {
                            identity = Str(data, "compactionId") is { } compactionId
                                ? $"summary:cmp:{compactionId}"
                                : $"seq:{seq}";
                        }
                        else if (Str(message, "id") is { } messageID)
                        {
                            identity = $"msg:{messageID}";
                        }
                        else
                        {
                            identity = $"assistant:seq:{seq}";
                        }

                        var key = $"dsh:{identity}:{milliseconds}:{headerProvider ?? ""}:{model}:"
                            + $"{tally.Input}:{tally.Output}:{tally.CacheRead}:{tally.CacheWrite}:{reasoning}";

                        records.Add(new AgentUsageRecord
                        {
                            Timestamp = timestamp,
                            Model = model,
                            Tally = tally,
                            SessionID = sessionID ?? "unknown",
                            SessionName = headerProvider,
                            Project = workspace is null ? null : Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                            DeduplicationID = key,
                        });
                        break;
                    }
                }
            }
        }
        return records;
    }

    private static JsonElement ObjectOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
            return default;
        return property;
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

    private static long Int64Of(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var l) => l,
            JsonValueKind.Number => (long)property.GetDouble(),
            _ => 0,
        };
    }
}

using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

public static partial class TranscriptLocator
{
    /// <summary>
    /// Cherry Studio keeps a Claude Code-shaped JSONL tree under the app's data
    /// directory — once for the legacy V1 layout and once for V2. Windows paths:
    /// %APPDATA%\CherryStudio (the roaming Application Support equivalent).
    /// V2 is probed before V1 so a same-named session in both trees resolves to
    /// the current one.
    /// </summary>
    public static IReadOnlyList<string> CherryStudioRoots(string? userProfile = null, string? appDataRoaming = null)
    {
        var roaming = appDataRoaming ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(roaming)) return Array.Empty<string>();

        var candidates = new[]
        {
            Path.Combine(roaming, "CherryStudio", "Data", "Agents", ".claude", "projects"),
            Path.Combine(roaming, "CherryStudio", ".claude", "projects"),
        };
        // The Unix-home variant is kept for portable installs driven off $HOME.
        if (!string.IsNullOrEmpty(home))
            candidates = candidates.Append(Path.Combine(home, ".config", "CherryStudio", "Data", "Agents", ".claude", "projects"))
                .Append(Path.Combine(home, ".config", "CherryStudio", ".claude", "projects"))
                .ToArray();

        return candidates.Where(Directory.Exists).ToList();
    }
}

/// <summary>
/// Cherry Studio's agent transcripts; port of upstream CherryStudioReader.
/// The store is a standard Claude Code-shaped JSONL tree. Cherry appends the
/// SAME API CALL three or four times as a response streams, each copy with a
/// fresh uuid but the same requestId, message.id and usage — so counting lines
/// would multiply every call by four.
///
/// The identity is the call, and the counters are cumulative: records sharing
/// requestId (falling back to message.id, then uuid) are folded into one,
/// merging each bucket by field-wise maximum, which is what a stream of growing
/// snapshots needs. A record with no identity at all is kept on its own: usage
/// that merely looks alike is not the same call.
/// </summary>
public static class CherryStudioReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null, string? appDataRoaming = null)
    {
        var records = new List<AgentUsageRecord>();
        var seenRelative = new HashSet<string>();

        foreach (var root in TranscriptLocator.CherryStudioRoots(userProfile, appDataRoaming))
        {
            foreach (var file in TranscriptLocator.FindTranscripts(root))
            {
                // Both trees hold the same relative session path; the first root
                // listed (V2) wins it.
                var relative = Path.GetRelativePath(root, file);
                if (!seenRelative.Add(relative.Replace('\\', '/'))) continue;
                records.AddRange(Parse(file, relative));
            }
        }
        return records;
    }

    private static IEnumerable<AgentUsageRecord> Parse(string file, string relative)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception)
        {
            yield break; // unreadable mid-scan: skip the file this pass
        }

        var session = System.IO.Path.GetFileNameWithoutExtension(file);
        var project = System.IO.Path.GetFileName(Path.GetDirectoryName(file));
        project = string.IsNullOrWhiteSpace(project) ? null : project;

        var identified = new Dictionary<string, Snapshot>();
        var order = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!Str(row, "type")?.Equals("assistant", StringComparison.Ordinal) ?? true) continue;
                if (!row.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;

                int input = IntOf(usage, "input_tokens");
                int cacheRead = IntOf(usage, "cache_read_input_tokens");
                int cacheWrite = IntOf(usage, "cache_creation_input_tokens");
                int output = IntOf(usage, "output_tokens");
                if (input + cacheRead + cacheWrite + output <= 0) continue;

                // The timestamp may sit on the record, on its message, or be
                // absent; absent is not newer evidence.
                var timestamp = TimestampOf(row.TryGetProperty("timestamp", out var t1) ? t1 : default)
                    ?? TimestampOf(message.TryGetProperty("timestamp", out var t2) ? t2 : default);

                var identity = NonBlank(Str(row, "requestId"))
                    ?? NonBlank(Str(message, "requestId"))
                    ?? NonBlank(Str(message, "id"))
                    ?? NonBlank(Str(row, "uuid"));

                var model = NonBlank(Str(message, "model"));
                if (model is null) continue;

                if (identity is null)
                {
                    if (timestamp is null) continue;
                    yield return new AgentUsageRecord
                    {
                        Timestamp = timestamp.Value,
                        Model = model,
                        Tally = new TokenTally(input, cacheWrite, cacheRead, output),
                        SessionID = session,
                        Project = project,
                    };
                    continue;
                }

                if (!identified.TryGetValue(identity, out var snapshot))
                {
                    identified[identity] = snapshot = new Snapshot();
                    order.Add(identity);
                }
                // Field-wise maximum: a stream of growing snapshots.
                snapshot.Input = Math.Max(snapshot.Input, input);
                snapshot.CacheWrite = Math.Max(snapshot.CacheWrite, cacheWrite);
                snapshot.CacheRead = Math.Max(snapshot.CacheRead, cacheRead);
                snapshot.Output = Math.Max(snapshot.Output, output);
                snapshot.Model ??= model;
                if (timestamp is { } stamp && stamp > (snapshot.Timestamp ?? DateTimeOffset.MinValue))
                    snapshot.Timestamp = stamp;
            }
        }

        foreach (var identity in order)
        {
            var snapshot = identified[identity];
            if (snapshot.Timestamp is not { } stamp || snapshot.Model is not { } model) continue;
            yield return new AgentUsageRecord
            {
                Timestamp = stamp,
                Model = model,
                Tally = new TokenTally(snapshot.Input, snapshot.CacheWrite, snapshot.CacheRead, snapshot.Output),
                SessionID = session,
                Project = project,
                DeduplicationID = $"cherrystudio:{relative}:{identity}",
            };
        }
    }

    private sealed class Snapshot
    {
        public int Input;
        public int CacheWrite;
        public int CacheRead;
        public int Output;
        public DateTimeOffset? Timestamp;
        public string? Model;
    }

    private static DateTimeOffset? TimestampOf(JsonElement element) =>
        element.ValueKind == JsonValueKind.String &&
        element.GetString() is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

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


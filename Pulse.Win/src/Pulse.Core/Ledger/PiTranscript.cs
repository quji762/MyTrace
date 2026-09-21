using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// The Pi transcript, shared by Pi, omp, Senpi and Kimchi; port of upstream
/// PiTranscript. One JSONL file per session: an optional `title` record, a
/// `session` header carrying the session id and working directory, then
/// `message` records whose `message.usage` holds four independent token
/// buckets. A branch or fork copies prior assistant records into a new file
/// verbatim, which is why the record identity is session-independent — a copy
/// has to fold onto its original.
///
/// Reasoning is INSIDE output: the format documents `reasoning` as a subset of
/// `output`, so it is never a bucket of its own and never added to output a
/// second time.
/// </summary>
public static class PiTranscript
{
    public sealed record Header(string? Id, string? Cwd, string? ParentSession, int? RlmDepth);

    public sealed record Message(string? Id, string? ResponseId, DateTimeOffset? Timestamp, string? Provider, string? Model, TokenTally Tally, int Unclassified);

    public sealed record Attribution(string? Id, string? TargetId, TokenTally ChildUsage, TokenTally AggregateUsage);

    public sealed record ParsedFile(string Path, Header Header, string? SessionInfoName, IReadOnlyList<Message> Messages, IReadOnlyList<Attribution> Attributions, bool IsValid);

    /// <summary>Parses one file. A malformed session header is fatal to the
    /// WHOLE file: without a header there is no session id or working
    /// directory, and a record built from the remainder would be attributed to
    /// nobody.</summary>
    public static ParsedFile Parse(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (IOException)
        {
            return new ParsedFile(path, new Header(null, null, null, null), null,
                Array.Empty<Message>(), Array.Empty<Attribution>(), false);
        }

        Header? header = null;
        string? sessionInfoName = null;
        var messages = new List<Message>();
        var attributions = new List<Attribution>();
        var malformed = false;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(raw); }
            catch (JsonException) { continue; }

            using (document)
            {
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object) continue;
                var type = Str(row, "type");

                if (header is null)
                {
                    // A descendant may open with title metadata; anything else
                    // is the session header or the file is malformed.
                    if (type == "title") continue;
                    if (type != "session" || Str(row, "id") is not { } id)
                    {
                        malformed = true;
                        break;
                    }
                    header = new Header(
                        Id: id,
                        Cwd: Str(row, "cwd"),
                        ParentSession: Str(row, "parentSession"),
                        RlmDepth: OptInt(row, "rlmDepth"));
                    continue;
                }

                switch (type)
                {
                    case "session_info":
                        sessionInfoName = Str(row, "name") ?? sessionInfoName;
                        break;
                    case "message":
                        if (ParseMessage(row) is { } message) messages.Add(message);
                        break;
                    case "child_usage_attributed":
                        if (ParseAttribution(row) is { } attribution) attributions.Add(attribution);
                        break;
                }
            }
        }

        return new ParsedFile(
            Path: System.IO.Path.GetFullPath(path),
            Header: header ?? new Header(null, null, null, null),
            SessionInfoName: sessionInfoName,
            Messages: messages,
            Attributions: attributions,
            IsValid: !malformed && header is not null);
    }

    /// <summary>The four buckets, plus any reported total not explained by them.
    /// A total the known buckets already account for yields nothing
    /// unclassified; a total standing in for a kind nobody named is carried as
    /// `unclassified` rather than poured into input, because a fabricated kind
    /// is a number nobody can check.</summary>
    public static (TokenTally Tally, int Unclassified) Usage(JsonElement usageObject)
    {
        int? input = OptInt(usageObject, "input");
        int? output = OptInt(usageObject, "output");
        int? cacheRead = OptInt(usageObject, "cacheRead");
        int? cacheWrite = OptInt(usageObject, "cacheWrite");
        int? total = OptInt(usageObject, "totalTokens");

        var tally = new TokenTally(
            Input: input ?? 0,
            CacheWrite: cacheWrite ?? 0,
            CacheRead: cacheRead ?? 0,
            Output: output ?? 0);
        var anyKnown = input is not null || output is not null || cacheRead is not null || cacheWrite is not null;
        if (total is not { } totalCount) return (tally, 0);
        if (!anyKnown) return (new TokenTally(), totalCount);
        var remainder = totalCount - tally.Total;
        return (tally, remainder > 0 ? remainder : 0);
    }

    private static Message? ParseMessage(JsonElement row)
    {
        if (!row.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
        if (Str(message, "role") != "assistant") return null;
        if (!message.TryGetProperty("usage", out var usageObject) || usageObject.ValueKind != JsonValueKind.Object) return null;

        var counts = Usage(usageObject);
        if (counts.Tally.Total <= 0 && counts.Unclassified <= 0) return null;

        DateTimeOffset? timestamp = null;
        if (Str(row, "timestamp") is { } rowStamp && DateTimeOffset.TryParse(rowStamp, CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var rowParsed))
            timestamp = rowParsed;
        if (timestamp is null && Str(message, "timestamp") is { } msgStamp && DateTimeOffset.TryParse(msgStamp, CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var msgParsed))
            timestamp = msgParsed;

        return new Message(
            Id: Str(row, "id"),
            ResponseId: Str(message, "responseId"),
            Timestamp: timestamp,
            Provider: Str(message, "provider"),
            Model: Str(message, "model"),
            Tally: counts.Tally,
            Unclassified: counts.Unclassified);
    }

    private static Attribution? ParseAttribution(JsonElement row)
    {
        if (!row.TryGetProperty("childUsage", out var child) || child.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("aggregateUsage", out var aggregate) || aggregate.ValueKind != JsonValueKind.Object)
            return null;
        var childUsage = Usage(child).Tally;
        var aggregateUsage = Usage(aggregate).Tally;
        if (childUsage.Total <= 0 && aggregateUsage.Total <= 0) return null;
        return new Attribution(
            Id: Str(row, "id"),
            TargetId: Str(row, "targetId"),
            ChildUsage: childUsage,
            AggregateUsage: aggregateUsage);
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
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
}

using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Copilot's file-exported OpenTelemetry JSONL (`~/.copilot/otel/`); port of the
/// core of upstream CopilotLogReaderOTEL. One line per span, log record or event;
/// a usage line does not always carry its own model, session or agent — those
/// often arrive on another line sharing the trace_id, sometimes after the usage
/// line — so the file is read once and the context resolved afterwards.
///
/// Four record kinds in priority order: chat span &gt; inference log &gt; agent-turn
/// log &gt; agent-summary span. A higher lane suppresses a lower one sharing a
/// trace or response id. Tokens are made DISJOINT before they leave: input
/// includes the cache read (removed once), reasoning is a subset of output (only
/// stands in when output was not reported), and a bare total with no split is
/// unclassified — counted, never priced, never invented into input.
/// </summary>
public static class CopilotOtelReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".copilot", "otel");
        return RecordsFromRoots(Directory.Exists(root) ? new[] { root } : Array.Empty<string>());
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var candidates = new List<Candidate>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (Exception) { continue; }

                var parsed = new List<(JsonElement? Root, string Raw)>();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { parsed.Add((JsonDocument.Parse(line).RootElement.Clone(), line)); }
                    catch (JsonException) { parsed.Add((null, line)); }
                }

                var context = TraceContext(parsed);
                foreach (var (row, _) in parsed)
                {
                    if (row is { } r &&
                        CandidateFrom(r, context, candidates.Count) is { } candidate)
                    {
                        candidates.Add(candidate);
                    }
                }
            }
        }

        return Resolve(candidates);
    }

    // --- lanes --------------------------------------------------------------------

    private enum Lane
    {
        ChatSpan = 0,
        InferenceLog = 1,
        AgentTurnLog = 2,
        AgentSummarySpan = 3,
    }

    private static readonly Lane[] LaneOrder = [Lane.ChatSpan, Lane.InferenceLog, Lane.AgentTurnLog, Lane.AgentSummarySpan];

    private static Lane? LaneOf(JsonElement row)
    {
        var attributes = Attributes(row);
        var type = Str(row, "type");
        var name = Str(row, "name");
        var operation = Str(attributes, "gen_ai.operation.name");

        var isSpan = type == "span" || (type is null && name is not null && HasSpanShape(row));
        if (!isSpan)
        {
            if (Str(attributes, "event.name") == "gen_ai.client.inference.operation.details" ||
                BodyStartsWith(row, "GenAI inference:")) return Lane.InferenceLog;
            if (Str(attributes, "event.name") == "copilot_chat.agent.turn" ||
                BodyStartsWith(row, "copilot_chat.agent.turn")) return Lane.AgentTurnLog;
            return null;
        }

        if (operation == "chat" || (name?.StartsWith("chat ", StringComparison.Ordinal) ?? false))
            return Lane.ChatSpan;
        if (operation == "invoke_agent" || (name?.StartsWith("invoke_agent ", StringComparison.Ordinal) ?? false))
            return Lane.AgentSummarySpan;
        return null;
    }

    private static bool HasSpanShape(JsonElement row)
    {
        var (trace, span) = Identification(row);
        if (trace is not null || span is not null) return true;
        foreach (var key in new[] { "startTime", "endTime", "duration", "kind" })
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out _)) return true;
        return false;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool BodyStartsWith(JsonElement row, string prefix)
    {
        foreach (var key in new[] { "body", "_body" })
        {
            if (Str(row, key) is { } text && text.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // --- attributes & identity ------------------------------------------------------

    private static JsonElement Attributes(JsonElement row)
    {
        if (row.ValueKind == JsonValueKind.Object &&
            row.TryGetProperty("attributes", out var attributes) &&
            attributes.ValueKind == JsonValueKind.Object)
            return attributes;
        return default;
    }

    private static (string? Trace, string? Span) Identification(JsonElement row)
    {
        row.TryGetProperty("spanContext", out var nested);
        var nestedObject = nested.ValueKind == JsonValueKind.Object ? nested : default;
        var trace = ValidID(Str(row, "traceId") ?? Str(nestedObject, "traceId"));
        var span = ValidID(Str(row, "spanId") ?? Str(nestedObject, "spanId"));
        return (trace, span);
    }

    /// <summary>The W3C non-recording sentinel — empty, or all zeroes — is absent.</summary>
    private static string? ValidID(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.All(c => c == '0') ? null : value;
    }

    private static string? ModelOf(JsonElement attributes) =>
        Str(attributes, "gen_ai.response.model") ?? Str(attributes, "gen_ai.request.model");

    private static string? SessionOf(JsonElement attributes)
    {
        foreach (var key in new[]
                 {
                     "gen_ai.conversation.id", "copilot_chat.session_id", "copilot_chat.chat_session_id",
                     "session.id", "github.copilot.interaction_id", "gen_ai.response.id",
                 })
        {
            if (Str(attributes, key) is { } value) return value;
        }
        return null;
    }

    private sealed record TraceContextInfo(string? Model, string? Session, string? Response, string? Agent);

    private static Dictionary<string, TraceContextInfo> TraceContext(IEnumerable<(JsonElement? Root, string Raw)> rows)
    {
        var contexts = new Dictionary<string, TraceContextInfo>();
        foreach (var (rowMaybe, _) in rows)
        {
            if (rowMaybe is not { } row) continue;
            var (trace, _) = Identification(row);
            if (trace is null) continue;
            var attributes = Attributes(row);
            var existing = contexts.GetValueOrDefault(trace, new TraceContextInfo(null, null, null, null));
            contexts[trace] = new TraceContextInfo(
                existing.Model ?? ModelOf(attributes),
                existing.Session ?? SessionOf(attributes),
                existing.Response ?? Str(attributes, "gen_ai.response.id"),
                existing.Agent ?? Str(attributes, "gen_ai.agent.id"));
        }
        return contexts;
    }

    // --- candidates ------------------------------------------------------------------

    private sealed class Candidate
    {
        public Lane Lane;
        public string Key = "";
        public DateTimeOffset Timestamp;
        public string Model = "";
        public string? SessionID;
        public TokenTally Tally;
        public int Unclassified;
        public string? Trace;
        public string? Response;
        public bool IsPartial;

        /// <summary>The same event seen twice is one event; a later copy can only
        /// add detail, so each bucket takes the larger of the two.</summary>
        public Candidate MergedWith(Candidate other) => new()
        {
            Lane = Lane,
            Key = Key,
            Timestamp = Timestamp,
            Model = Model,
            SessionID = SessionID,
            Tally = new TokenTally(
                Math.Max(Tally.Input, other.Tally.Input),
                Math.Max(Tally.CacheWrite, other.Tally.CacheWrite),
                Math.Max(Tally.CacheRead, other.Tally.CacheRead),
                Math.Max(Tally.Output, other.Tally.Output)),
            Unclassified = Math.Max(Unclassified, other.Unclassified),
            Trace = Trace,
            Response = Response,
            IsPartial = IsPartial || other.IsPartial,
        };
    }

    private static Candidate? CandidateFrom(JsonElement row, Dictionary<string, TraceContextInfo> context, int ordinal)
    {
        if (LaneOf(row) is not { } lane) return null;
        var attributes = Attributes(row);
        var (trace, span) = Identification(row);
        var shared = trace is null ? null : context.GetValueOrDefault(trace!);

        // A record with no usable time is not dated at all: the clock is never
        // used to fill the gap.
        if (TimestampOf(row) is not { } timestamp) return null;

        var session = SessionOf(attributes) ?? shared?.Session;
        var response = Str(attributes, "gen_ai.response.id") ?? shared?.Response;
        var model = ModelOf(attributes) ?? shared?.Model;
        if (model is null) return null;

        var (tally, unclassified) = Counts(attributes);
        if (tally.Total <= 0 && unclassified <= 0) return null;
        var turn = TextValue(attributes, "turn.index") ?? TextValue(attributes, "copilot_chat.turn.index");

        var key = DedupKey(lane, (trace, span), session, response, ordinal, turn);
        return new Candidate
        {
            Lane = lane,
            Key = key,
            Timestamp = timestamp,
            Model = model,
            SessionID = session,
            Tally = tally,
            Unclassified = unclassified,
            Trace = trace,
            Response = response,
            // Neither a trace nor a response id: it cannot be matched against
            // another lane, so it is carried as a possible subset.
            IsPartial = trace is null && response is null,
        };
    }

    /// <summary>
    /// The product's own identity for a record, never a clock reading. A
    /// timestamp is NOT an identity: two different requests can share one, and
    /// folding by an instant would delete a different request. A response id is
    /// an identity only for an inference log (one record per response); a chat
    /// span is not keyed by response id because several spans can belong to one
    /// response — folding them would delete work.
    /// </summary>
    private static string DedupKey(Lane lane, (string? Trace, string? Span) identity, string? session, string? response, int ordinal, string? turn)
    {
        switch (lane)
        {
            case Lane.ChatSpan:
            case Lane.AgentSummarySpan:
                if (identity.Trace is { } t && identity.Span is { } s) return $"{t}:{s}";
                if (session is { } sess && identity.Span is { } span) return $"span:{sess}:{span}";
                return $"span-unnamed:{ordinal}";
            case Lane.InferenceLog:
                if (identity.Trace is { } lt && identity.Span is { } ls) return $"log:{lt}:{ls}";
                if (response is { } r) return $"log-response:{r}";
                return $"log-unnamed:{ordinal}";
            case Lane.AgentTurnLog:
                if (identity.Trace is { } trace) return $"agent-turn:{trace}:{turn ?? $"idx-{ordinal}"}";
                return $"agent-turn-unnamed:{ordinal}";
            default:
                return $"unnamed:{ordinal}";
        }
    }

    // --- tokens -----------------------------------------------------------------------

    /// <summary>The four disjoint kinds, plus a bare total that named no kind.</summary>
    private static (TokenTally Tally, int Unclassified) Counts(JsonElement attributes)
    {
        var input = IntOf(attributes, "gen_ai.usage.input_tokens");
        var output = IntOf(attributes, "gen_ai.usage.output_tokens");
        var cacheRead = FirstPositive(attributes,
            "gen_ai.usage.cache_read.input_tokens", "gen_ai.usage.cache_read_input_tokens");
        var cacheWrite = FirstPositive(attributes,
            "gen_ai.usage.cache_write.input_tokens", "gen_ai.usage.cache_creation.input_tokens",
            "gen_ai.usage.cache_write_input_tokens", "gen_ai.usage.cache_creation_input_tokens");
        var reasoning = FirstPositive(attributes,
            "gen_ai.usage.reasoning.output_tokens", "gen_ai.usage.reasoning_tokens");

        if (input <= 0 && output <= 0 && cacheRead <= 0 && cacheWrite <= 0 && reasoning <= 0)
        {
            // A total with no split is real work we cannot place; counted as
            // unclassified, never put into the input bucket.
            var total = IntOf(attributes, "gen_ai.usage.total_tokens");
            total = total != 0 ? total : IntOf(attributes, "gen_ai.usage.total.tokens");
            total = total != 0 ? total : IntOf(attributes, "total_tokens");
            return (new TokenTally(), Math.Max(total, 0));
        }

        // Input includes the cache read; remove it once. Reasoning is a subset
        // of output: only stands in when output was not reported.
        var freshInput = Math.Max(input - Math.Min(cacheRead, input), 0);
        var foldedOutput = output > 0 ? output : reasoning;
        return (new TokenTally(freshInput, cacheWrite, cacheRead, foldedOutput), 0);
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

    private static int FirstPositive(JsonElement attributes, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (IntOf(attributes, key) is { } value && value > 0) return value;
        }
        return 0;
    }

    private static string? TextValue(JsonElement attributes, string name) =>
        Str(attributes, name) ??
        (attributes.ValueKind == JsonValueKind.Object &&
         attributes.TryGetProperty(name, out var property) &&
         property.ValueKind == JsonValueKind.Number
            ? property.GetRawText()
            : null);

    // --- time ---------------------------------------------------------------------------

    /// <summary>The first usable timing key, in the format's own order. A numeric
    /// duration is milliseconds and only back-calculates a start from an endTime.</summary>
    private static DateTimeOffset? TimestampOf(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        if (TryTime(row, "startTime", out var start)) return start;

        if (TryTime(row, "endTime", out var end))
        {
            if (row.TryGetProperty("duration", out var durationElement) &&
                durationElement.ValueKind == JsonValueKind.Number &&
                durationElement.TryGetDouble(out var milliseconds))
                return end.AddMilliseconds(-milliseconds);
            return end;
        }

        foreach (var key in new[] { "time", "timestamp", "observedTimestamp" })
        {
            if (row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
                return parsed;
        }

        if (row.TryGetProperty("timeUnixNano", out var nanos) && nanos.ValueKind == JsonValueKind.Number &&
            nanos.TryGetDouble(out var nanoDouble))
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(nanoDouble / 1_000_000));
        return null;
    }

    private static bool TryTime(JsonElement row, string key, out DateTimeOffset value)
    {
        value = default;
        if (!row.TryGetProperty(key, out var element)) return false;
        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out value))
            return true;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var seconds) && seconds > 0)
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
            return true;
        }
        return false;
    }

    // --- resolving ------------------------------------------------------------------------

    private static IReadOnlyList<AgentUsageRecord> Resolve(List<Candidate> candidates)
    {
        // Same-key copies collapse first, then a higher lane drops a lower one
        // that shares a trace or response id.
        var byKey = new Dictionary<string, Candidate>();
        foreach (var candidate in candidates)
        {
            byKey[candidate.Key] = byKey.TryGetValue(candidate.Key, out var existing)
                ? existing.MergedWith(candidate)
                : candidate;
        }

        var deduped = byKey.Values.OrderBy(c => c.Key, StringComparer.Ordinal).ToList();

        var traces = new Dictionary<Lane, HashSet<string>>();
        var responses = new Dictionary<Lane, HashSet<string>>();
        foreach (var candidate in deduped)
        {
            if (candidate.Trace is { } trace)
            {
                if (!traces.TryGetValue(candidate.Lane, out var tset))
                    traces[candidate.Lane] = tset = new HashSet<string>();
                tset.Add(trace);
            }
            if (candidate.Response is { } response)
            {
                if (!responses.TryGetValue(candidate.Lane, out var rset))
                    responses[candidate.Lane] = rset = new HashSet<string>();
                rset.Add(response);
            }
        }

        var kept = new List<Candidate>();
        foreach (var candidate in deduped)
        {
            var suppressed = LaneOrder.Any(higher =>
            {
                if (higher >= candidate.Lane) return false;
                if (candidate.Trace is { } ct && traces.GetValueOrDefault(higher)?.Contains(ct) == true) return true;
                if (candidate.Response is { } cr && responses.GetValueOrDefault(higher)?.Contains(cr) == true) return true;
                return false;
            });
            if (!suppressed) kept.Add(candidate);
        }

        return kept.Select(candidate => new AgentUsageRecord
        {
            Timestamp = candidate.Timestamp,
            Model = candidate.Model,
            Tally = candidate.Tally,
            UnclassifiedTokens = candidate.Unclassified,
            IsPartial = candidate.IsPartial,
            SessionID = candidate.SessionID,
            DeduplicationID = $"copilot-otel:{candidate.Key}",
        }).ToList();
    }
}

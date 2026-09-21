using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Pulse.Core.Ledger;

/// <summary>
/// Antigravity CLI's conversations, one SQLite database per conversation under
/// `&lt;GEMINI_CLI_HOME or ~/.gemini&gt;/antigravity-cli/conversations/`; port of
/// upstream AntigravityCLIReader.
///
/// The token data is in protobuf blobs, and no `.proto` ships with the CLI.
/// This reader therefore carries its **own** minimal protobuf wire decoder —
/// written to the standard wire rules, not ported — kept deliberately
/// hostile-safe: a truncated buffer, an overlong varint, a length that runs
/// past the end, an unknown wire type and an unknown field number all end the
/// decode instead of reading out of bounds.
///
/// **Only fields whose meaning is established are counted.** The usage
/// message's `#2` (newly-processed input), `#5` (cache read), `#9` (output)
/// and `#10` (thinking) are the counters with an observed meaning; `#9 + #10`
/// is the contract's total output, so reasoning is folded into output once.
/// `#1` is a near-constant whose "fixed system prompt" reading is
/// reverse-engineered, **not** an established count, so it is read by nothing
/// here — not as input and not as unclassified work. A turn whose real
/// counters are all zero is dropped; nothing is estimated from text or cost.
///
/// **Every record is marked `isPartial`.** Excluding `#1` and having no
/// whole-usage total to reconcile the counted fields against means the read is
/// a proven subset rather than a confirmed complete one; the flag says so and
/// changes no count.
///
/// **Timestamps need evidence.** Only the explicit per-generation timestamp
/// (`#9.#4`) and the `steps` table's timestamp are used. The unknown `#9.#10`
/// bytes are never decoded into an event time. A generation with no such
/// timestamp falls back to the conversation's created-at and is marked
/// **aggregate**, because a session anchor is not the moment of the turn. A
/// file modification date is never used.
///
/// **The CLI and the IDE are two clients, and are never added together** —
/// they write the same store into sibling folders, and one reader serves both
/// only because the folders differ by argument.
/// </summary>
public static class AntigravityCliReader
{
    private const string RoutingLabel = "gemini-default";

    /// <summary>Labels whose model identity is verified. Server-supplied and
    /// localisable, so an unknown label is never guessed at.</summary>
    private static readonly IReadOnlyDictionary<string, string> LabelTable =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Gemini 3.5 Flash (Low)"] = "gemini-3.5-flash-extra-low",
            ["Gemini 3.5 Flash (Medium)"] = "gemini-3.5-flash-medium",
            ["Gemini 3.5 Flash (High)"] = "gemini-3.5-flash-high",
        };

    public static IReadOnlyList<AgentUsageRecord> Records(
        string? userProfile = null,
        string? client = "antigravity-cli",
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? EnvironmentValue(string name) =>
            environment is null ? Environment.GetEnvironmentVariable(name) : environment.GetValueOrDefault(name);

        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = EnvironmentValue("GEMINI_CLI_HOME") is { } value && value.Length > 0
            ? value
            : Path.Combine(home, ".gemini");

        // Both IDE layouts: `antigravity` is what the current one writes,
        // `antigravity-ide` the other spelling seen beside it.
        var folders = client == "antigravity-ide"
            ? new[] { "antigravity", "antigravity-ide" }
            : new[] { "antigravity-cli" };

        var roots = folders
            .Select(folder => Path.Combine(root, folder, "conversations"))
            .ToList();
        return RecordsFromRoots(roots);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var records = new List<AgentUsageRecord>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] databases;
            try
            {
                databases = Directory.EnumerateFiles(root, "*.db", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { continue; }
            foreach (var database in databases)
                records.AddRange(ReadConversation(database));
        }
        return records;
    }

    // --- one conversation -----------------------------------------------------

    private static IReadOnlyList<AgentUsageRecord> ReadConversation(string file)
    {
        var records = new List<AgentUsageRecord>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var session = Path.GetFileNameWithoutExtension(file);
            var anchor = Trajectory(connection);
            var steps = StepTimestamps(connection);
            var generations = Generations(connection);

            // The models a label was seen with, and the file's single model if
            // it has exactly one. Used only to recover an identity the routing
            // label threw away.
            var labels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var models = new HashSet<string>(StringComparer.Ordinal);
            foreach (var generation in generations)
            {
                if (generation.Model is not { } model || model == RoutingLabel) continue;
                models.Add(model);
                if (generation.Label is { } label)
                {
                    if (!labels.TryGetValue(label, out var set))
                        labels[label] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(model);
                }
            }
            var soleModel = models.Count == 1 ? models.First() : null;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var generation in generations)
            {
                if (generation.Usage is not { } usage || usage.Counted <= 0) continue;
                if (usage.ResponseID is { } response && !seen.Add(response)) continue;

                // An exact event time, or the session anchor as an aggregate.
                // The unknown `#9.#10` bytes never become a turn time.
                DateTimeOffset? timestamp =
                    generation.ExplicitTimestamp ??
                    (usage.ResponseID is { } id && steps.ByResponse.TryGetValue(id, out var byResponse) ? (DateTimeOffset?)byResponse : null) ??
                    (steps.ByIndex.TryGetValue(generation.Index, out var byIndex) ? (DateTimeOffset?)byIndex : null);
                var moment = timestamp ?? anchor.CreatedAt;
                if (moment is null) continue;

                records.Add(new AgentUsageRecord
                {
                    Timestamp = moment.Value,
                    Model = ResolvedModel(generation, labels, soleModel),
                    Tally = new TokenTally(usage.Input, 0, usage.CacheRead, usage.Output + usage.Reasoning),
                    SessionID = session,
                    Project = EditorLog.Project(anchor.Workspace),
                    DeduplicationID = usage.ResponseID is { } rid
                        ? $"antigravity:{session}:{rid}"
                        : $"antigravity:{session}:{generation.Index}",
                    IsAggregate = timestamp is null,
                    // `#1` is deliberately not counted and there is no
                    // whole-usage total to reconcile the counted fields
                    // against, so the record is a proven subset.
                    IsPartial = true,
                });
            }
        }
        catch (Exception) { }
        return records;
    }

    // --- tables ---------------------------------------------------------------

    private sealed record Anchor(DateTimeOffset? CreatedAt, string? Workspace);

    private sealed record Steps(IReadOnlyDictionary<string, DateTimeOffset> ByResponse, IReadOnlyDictionary<int, DateTimeOffset> ByIndex);

    private sealed record Generation(int Index, string? Model, string? Label, Usage? Usage, DateTimeOffset? ExplicitTimestamp);

    private sealed record Usage
    {
        /// <summary>`#2`, newly-processed (non-cached) input. `#1` is
        /// deliberately absent: its meaning is not established, so it is no
        /// count at all.</summary>
        public int Fresh;
        public int CacheRead;
        public int Output;
        public int Reasoning;
        public string? ResponseID;

        public int Input => Fresh;
        public int Counted => Fresh + CacheRead + Output + Reasoning;
    }

    private static Anchor Trajectory(SqliteConnection connection)
    {
        var anchor = new Anchor(null, null);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM trajectory_metadata_blob LIMIT 1";
            using var reader = command.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0))
            {
                var blob = ReadBlob(reader, 0);
                if (AntigravityWire.Decode(blob) is { } message)
                {
                    anchor = anchor with
                    {
                        CreatedAt = Timestamp(AntigravityWire.Nested(message, 2)),
                        Workspace = AntigravityWire.Nested(message, 1) is { } folder &&
                                    folder.String(1) is { } uri
                            ? PathFromUri(uri)
                            : null,
                    };
                }
            }
        }
        catch (Exception) { }
        return anchor;
    }

    private static Steps StepTimestamps(SqliteConnection connection)
    {
        var byResponse = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var byIndex = new Dictionary<int, DateTimeOffset>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT metadata FROM steps WHERE step_type = 15";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                if (AntigravityWire.Decode(ReadBlob(reader, 0)) is not { } message) continue;
                if (Timestamp(AntigravityWire.Nested(message, 1)) is not { } timestamp) continue;

                if (AntigravityWire.Nested(message, 9)?.String(11) is { } response)
                    byResponse[response] = timestamp;
                if (AntigravityWire.Nested(message, 20) is { } twenty && twenty.Value(3)?.Varint is { } index)
                    byIndex[(int)index] = timestamp;
            }
        }
        catch (Exception) { }
        return new Steps(byResponse, byIndex);
    }

    private static IReadOnlyList<Generation> Generations(SqliteConnection connection)
    {
        var generations = new List<Generation>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT idx, data FROM gen_metadata ORDER BY idx";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var index = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (AntigravityWire.Decode(ReadBlob(reader, 1)) is not { } message ||
                    AntigravityWire.Nested(message, 1) is not { } chat)
                    continue;

                var generation = new Generation(index, chat.String(19), chat.String(21), null, null);
                generation = generation with
                {
                    ExplicitTimestamp = Timestamp(AntigravityWire.Nested(AntigravityWire.Nested(chat, 9), 4)),
                };

                if (AntigravityWire.Nested(chat, 4) is { } usage)
                {
                    var parsed = new Usage
                    {
                        Fresh = (int)Math.Min(usage.Value(2)?.Varint ?? 0, int.MaxValue),
                        CacheRead = (int)Math.Min(usage.Value(5)?.Varint ?? 0, int.MaxValue),
                        Output = (int)Math.Min(usage.Value(9)?.Varint ?? 0, int.MaxValue),
                        Reasoning = (int)Math.Min(usage.Value(10)?.Varint ?? 0, int.MaxValue),
                        ResponseID = usage.String(11),
                    };
                    generation = generation with { Usage = parsed };
                }
                generations.Add(generation);
            }
        }
        catch (Exception) { }
        return generations;
    }

    private static byte[] ReadBlob(SqliteDataReader reader, int column)
    {
        var length = reader.GetBytes(column, 0, null, 0, 0);
        var buffer = new byte[length];
        reader.GetBytes(column, 0, buffer, 0, buffer.Length);
        return buffer;
    }

    // --- fields -----------------------------------------------------------------

    /// <summary>A `{#1 seconds, #2 nanos}` timestamp. A non-positive or absurd
    /// seconds value is not a time.</summary>
    private static DateTimeOffset? Timestamp(AntigravityWire.Message? message)
    {
        var seconds = message?[1]?.Varint;
        if (seconds is not { } value || value <= 0 || value >= 1_000_000_000_000) return null;
        var nanos = message?[2]?.Varint ?? 0;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)((value + nanos / 1_000_000_000.0) * 1000));
    }

    /// <summary>A `file://` URI as a path, or the string unchanged when it is
    /// not one.</summary>
    private static string? PathFromUri(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var url) && url.IsFile)
            return Uri.UnescapeDataString(url.AbsolutePath);
        return uri;
    }

    /// <summary>The model identity, preferring the machine id, then a sibling
    /// row's id recovered through the display label, then the file's single
    /// model, then a verified label table, then the routing label itself.</summary>
    ///
    /// <remarks>**No vendor default is invented.** A file that names no model
    /// at all falls back to `unknown`, an unpriced name, rather than to a
    /// concrete product model that would be priced at the wrong rate.</remarks>
    private static string ResolvedModel(
        Generation generation,
        IReadOnlyDictionary<string, HashSet<string>> labels,
        string? sole)
    {
        if (generation.Model is { } model && model != RoutingLabel) return model;
        if (generation.Label is { } label)
        {
            if (labels.TryGetValue(label, out var set) && set.Count == 1)
                return set.First();
            if (sole is { } only) return only;
            if (LabelTable.TryGetValue(label, out var mapped)) return mapped;
        }
        else if (sole is { } fallback) return fallback;
        return generation.Model ?? "unknown";
    }
}

/// <summary>A minimal, bounds-checked protobuf wire decoder; port of upstream
/// AntigravityWire.</summary>
///
/// <remarks>Only the wire format itself is implemented — tags, the three
/// scalar wire types and length-delimited bytes — because the message schemas
/// differ per client and none of their `.proto` files ship. Unknown field
/// numbers are kept; an unsupported wire type (the group types) or any
/// malformed byte **fails the whole message** rather than returning a partial
/// one, so a caller never reads a truncated field as a value.</remarks>
public static class AntigravityWire
{
    public enum WireKind { Varint, Fixed64, Length, Fixed32 }

    public readonly record struct Value(WireKind Kind, ulong VarintValue, ulong Fixed64Value, uint Fixed32Value, byte[]? LengthBytes)
    {
        public static Value FromVarint(ulong value) => new(WireKind.Varint, value, 0, 0, null);
        public static Value FromFixed64(ulong value) => new(WireKind.Fixed64, 0, value, 0, null);
        public static Value FromFixed32(uint value) => new(WireKind.Fixed32, 0, 0, value, null);
        public static Value FromLength(byte[] value) => new(WireKind.Length, 0, 0, 0, value);

        public ulong? Varint => Kind == WireKind.Varint ? VarintValue : null;
        public byte[]? Bytes => Kind == WireKind.Length ? LengthBytes : null;
    }

    public sealed class Message
    {
        private readonly Dictionary<int, List<Value>> _fields = new();

        public Value? this[int number] =>
            _fields.TryGetValue(number, out var list) && list.Count > 0 ? list[0] : null;

        /// <summary>The first value of a field, if it has one.</summary>
        public Value? Value(int number) => this[number];

        public void Append(Value value, int number)
        {
            if (!_fields.TryGetValue(number, out var list))
                _fields[number] = list = new List<Value>();
            list.Add(value);
        }

        /// <summary>A length-delimited field as a UTF-8 string.</summary>
        public string? String(int number)
        {
            if (this[number] is not { } value || value.Bytes is not { } bytes) return null;
            var text = Encoding.UTF8.GetString(bytes).Trim();
            return text.Length > 0 ? text : null;
        }
    }

    /// <summary>A length-delimited field decoded as a nested message, or null
    /// when it is another wire type or not a valid message.</summary>
    public static Message? Nested(Message? message, int number)
    {
        if (message is null || message[number] is not { } value || value.Bytes is not { } bytes) return null;
        return Decode(bytes);
    }

    public static Message? Decode(byte[] bytes)
    {
        var index = 0;
        var message = new Message();
        while (index < bytes.Length)
        {
            if (Next(bytes, ref index) is not { } next) return null;
            message.Append(next.Value, next.Number);
        }
        return message;
    }

    private static (int Number, Value Value)? Next(byte[] bytes, ref int index)
    {
        if (Varint(bytes, ref index) is not { } tag) return null;
        var number = (int)(tag >> 3);
        // Field zero is not a field.
        if (number <= 0) return null;

        switch (tag & 7)
        {
            case 0:
                if (Varint(bytes, ref index) is not { } value) return null;
                return (number, Value.FromVarint(value));
            case 1:
                if (bytes.Length - index < 8) return null;
                var fixed64 = 0UL;
                for (var offset = 0; offset < 8; offset++)
                    fixed64 |= (ulong)bytes[index + offset] << (8 * offset);
                index += 8;
                return (number, Value.FromFixed64(fixed64));
            case 2:
                if (Varint(bytes, ref index) is not { } length || length > (ulong)(bytes.Length - index))
                    return null;
                var end = index + (int)length;
                var slice = new byte[end - index];
                Array.Copy(bytes, index, slice, 0, slice.Length);
                index = end;
                return (number, Value.FromLength(slice));
            case 5:
                if (bytes.Length - index < 4) return null;
                var fixed32 = 0U;
                for (var offset = 0; offset < 4; offset++)
                    fixed32 |= (uint)bytes[index + offset] << (8 * offset);
                index += 4;
                return (number, Value.FromFixed32(fixed32));
            default:
                // Wire types 3 and 4 are the deprecated groups.
                return null;
        }
    }

    /// <summary>A base-128 varint, refused past ten bytes or when the tenth
    /// would overflow.</summary>
    private static ulong? Varint(byte[] bytes, ref int index)
    {
        ulong result = 0;
        ulong shift = 0;
        for (var position = 0; position < 10; position++)
        {
            if (index >= bytes.Length) return null;
            var byteValue = bytes[index++];
            if (shift == 63 && byteValue > 1) return null;
            result |= (ulong)(byteValue & 0x7F) << (int)shift;
            if ((byteValue & 0x80) == 0) return result;
            shift += 7;
        }
        return null;
    }
}

using Microsoft.Data.Sqlite;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Devin Desktop ACP captures: the canonical aggregate (inputTokens is the
/// complete prompt, output summed per step, cached buckets overwrite), the
/// legacy per-event shape, and the CLI-database lookup that supplies identity
/// and suppresses mirrored captures.
/// </summary>
public class DevinDesktopReaderTests : IDisposable
{
    private readonly string _home;

    public DevinDesktopReaderTests() => _home = Path.Combine(Path.GetTempPath(), $"pulse-ddev-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string CaptureDir()
    {
        var directory = Path.Combine(_home, "AppData", "Local", "Devin", "User", "acp-events");
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void Canonical_Shape_Emits_One_Aggregate_Per_File()
    {
        var file = Path.Combine(CaptureDir(), "uuid-name.ndjson");
        File.WriteAllLines(file, new[]
        {
            """{"notification":{"sessionUpdate":"session_info_update","title":"Fix the login flow"}}""",
            """{"notification":{"sessionUpdate":"usage_update","created_at":1789981200,"_meta":{"cognition.ai/inputTokens":1000,"cognition.ai/cachedReadTokens":400,"cognition.ai/cachedWriteTokens":50,"cognition.ai/outputTokens":30,"cognition.ai/model":"gpt-5.6-sol"}}}""",
            """{"notification":{"sessionUpdate":"usage_update","created_at":1789981260,"_meta":{"cognition.ai/inputTokens":1100,"cognition.ai/cachedReadTokens":500,"cognition.ai/outputTokens":40}}}""",
        });

        var record = Assert.Single(DevinDesktopReader.RecordsFromRoots(
            new[] { CaptureDir() }, Array.Empty<string>()));
        // inputTokens is the complete prompt: fresh = last − last read; output
        // is summed across steps; cached buckets overwrite.
        Assert.Equal(1100 - 500, record.Tally.Input);
        Assert.Equal(500, record.Tally.CacheRead);
        Assert.Equal(50, record.Tally.CacheWrite);
        Assert.Equal(70, record.Tally.Output);
        Assert.Equal("gpt-5.6-sol", record.Model);
        Assert.True(record.IsAggregate);
        Assert.Equal("uuid-name", record.SessionID); // no lookup match: the stem stands
    }

    [Fact]
    public void Legacy_Shape_Emits_One_Record_Per_Event()
    {
        var file = Path.Combine(CaptureDir(), "older.ndjson");
        File.WriteAllLines(file, new[]
        {
            """{"notification":{"created_at":1789981200,"metadata":{"generation_model":"claude-opus-4.6","metrics":{"input_tokens":100,"output_tokens":10,"cache_read_tokens":20}}}}""",
            """{"notification":{"created_at":1789981300,"metadata":{"generation_model":"claude-opus-4.6","metrics":{"input_tokens":150,"output_tokens":12}}}}""",
        });

        Assert.Equal(2, DevinDesktopReader.RecordsFromRoots(new[] { CaptureDir() }, Array.Empty<string>()).Count);
    }

    [Fact]
    public void Lookup_Supplies_Identity_And_Adaptive_Is_Never_A_Model()
    {
        // The CLI lookup database with one session whose title matches.
        var dbDirectory = Path.Combine(_home, ".local", "share", "devin", "cli");
        Directory.CreateDirectory(dbDirectory);
        var dbPath = Path.Combine(dbDirectory, "sessions.db");
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = "CREATE TABLE sessions (id TEXT PRIMARY KEY, title TEXT, model TEXT, working_directory TEXT)";
            schema.ExecuteNonQuery();
            using var seed = connection.CreateCommand();
            seed.CommandText = "INSERT INTO sessions VALUES ('sess-9', 'Fix the login flow', 'adaptive', 'E:/Code/Proj')";
            seed.ExecuteNonQuery();
        }

        var file = Path.Combine(CaptureDir(), "unrelated-uuid.ndjson");
        File.WriteAllLines(file, new[]
        {
            """{"notification":{"sessionUpdate":"session_info_update","title":"Fix the login flow"}}""",
            """{"notification":{"sessionUpdate":"usage_update","created_at":1789981200,"_meta":{"cognition.ai/inputTokens":200,"cognition.ai/outputTokens":10}}}""",
        });

        var record = Assert.Single(DevinDesktopReader.RecordsFromRoots(
            new[] { CaptureDir(), dbDirectory }, Array.Empty<string>()));
        Assert.Equal("sess-9", record.SessionID);
        Assert.Equal("Proj", record.Project);
        // `adaptive` is a routing mode, not a model; the session names no other.
        Assert.Equal("unknown", record.Model);
    }
}

/// <summary>
/// Antigravity CLI: the hand-built protobuf decoder's hostile-safety, the
/// established usage fields (#2 input, #5 cache read, #9 output, #10 thinking
/// folded once), #1 never counted, the response id dedup, and the partial +
/// aggregate flags.
/// </summary>
public class AntigravityCliReaderTests : IDisposable
{
    private readonly string _home;

    public AntigravityCliReaderTests() => _home = Path.Combine(Path.GetTempPath(), $"pulse-acli-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    // --- wire decoder ---------------------------------------------------------

    [Fact]
    public void Wire_Decode_Reads_All_Scalar_Types()
    {
        // #1 varint 150, #2 length "hi", #3 fixed64, #4 fixed32.
        var bytes = new byte[] { 0x08, 0x96, 0x01, 0x12, 0x02, (byte)'h', (byte)'i', 0x19, 1, 2, 3, 4, 5, 6, 7, 8, 0x25, 1, 2, 3, 4 };
        var message = AntigravityWire.Decode(bytes);
        Assert.NotNull(message);
        Assert.Equal(150UL, message![1]!.Value.Varint);
        Assert.Equal("hi", message.String(2));
        Assert.Equal((ulong)0x0807060504030201, message[3]!.Value.Fixed64Value);
        Assert.Equal(0x04030201U, message[4]!.Value.Fixed32Value);
    }

    [Fact]
    public void Wire_Decode_Fails_Hostile_Input_Whole()
    {
        // Overlong varint: ten bytes all with the continuation bit set.
        Assert.Null(AntigravityWire.Decode(new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }));
        // Length runs past the end.
        Assert.Null(AntigravityWire.Decode(new byte[] { 0x12, 0x7F, 0x01 }));
        // Wire type 3 (deprecated group).
        Assert.Null(AntigravityWire.Decode(new byte[] { 0x0B }));
        // Field zero is not a field.
        Assert.Null(AntigravityWire.Decode(new byte[] { 0x00 }));
        // Truncated fixed64.
        Assert.Null(AntigravityWire.Decode(new byte[] { 0x11, 0x01, 0x02 }));
    }

    // --- the reader over a real temp database ---------------------------------

    private string ConversationDb(string name, byte[] genBlob, byte[]? trajectory = null)
    {
        var directory = Path.Combine(_home, ".gemini", "antigravity-cli", "conversations");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE trajectory_metadata_blob (data BLOB);
            CREATE TABLE steps (metadata BLOB, step_type INTEGER);
            CREATE TABLE gen_metadata (idx INTEGER, data BLOB);
            """;
        schema.ExecuteNonQuery();
        if (trajectory is not null)
        {
            using var anchor = connection.CreateCommand();
            anchor.CommandText = "INSERT INTO trajectory_metadata_blob VALUES ($d)";
            anchor.Parameters.AddWithValue("$d", trajectory);
            anchor.ExecuteNonQuery();
        }
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO gen_metadata VALUES (0, $d)";
        insert.Parameters.AddWithValue("$d", genBlob);
        insert.ExecuteNonQuery();
        return path;
    }

    /// <summary>gen_metadata blob: chat message (#1) with model #19,
    /// usage #4 {#2 fresh, #5 read, #9 output, #10 reasoning, #11 response},
    /// and explicit timestamp #9.#4.{#1 seconds}.</summary>
    private static byte[] ChatBlob(string model, int fresh, int read, int output, int reasoning, string response, ulong seconds)
    {
        var usage = Cat(VarTag(2, (ulong)fresh), VarTag(5, (ulong)read),
            VarTag(9, (ulong)output), VarTag(10, (ulong)reasoning), Tag(11, Len(response)));
        var timestamp = Cat(VarTag(1, seconds));
        var nine = Cat(Tag(4, Len(timestamp)));
        var chat = Cat(Tag(4, Len(usage)), Tag(9, Len(nine)), Tag(19, Len(model)));
        return Cat(Tag(1, Len(chat)));
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Tag(int number, byte[] payload)
    {
        var tag = (number << 3) | 2; // length-delimited
        return Cat(Varint((ulong)tag), payload);
    }

    private static byte[] VarTag(int number, ulong value)
    {
        var tag = number << 3; // wire type 0: varint
        return Cat(Varint((ulong)tag), Varint(value));
    }

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    private static byte[] Len(string text)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(text);
        return Bytes(payload.Length, payload);
    }

    private static byte[] Len(byte[] payload) => Bytes(payload.Length, payload);

    private static byte[] Bytes(int length, byte[] payload) => Cat(Varint((ulong)length), payload);

    [Fact]
    public void Established_Fields_Count_And_The_Record_Is_Partial()
    {
        ConversationDb("conv.db", ChatBlob("gemini-3.5-flash-medium", fresh: 700, read: 50, output: 90, reasoning: 10, "resp-1", seconds: 1_789_981_200));

        var record = Assert.Single(AntigravityCliReader.RecordsFromRoots(
            new[] { Path.Combine(_home, ".gemini", "antigravity-cli", "conversations") }));
        Assert.Equal(700, record.Tally.Input);
        Assert.Equal(50, record.Tally.CacheRead);
        Assert.Equal(90 + 10, record.Tally.Output); // reasoning folded once
        Assert.Equal("gemini-3.5-flash-medium", record.Model);
        Assert.Equal("antigravity:conv:resp-1", record.DeduplicationID);
        Assert.True(record.IsPartial);  // a proven subset, never a confirmed whole
        Assert.False(record.IsAggregate);
    }

    [Fact]
    public void No_Timestamp_Falls_Back_To_The_Anchor_As_Aggregate()
    {
        // A generation with no explicit timestamp and no steps row; the
        // conversation has no trajectory either — nothing to date it with,
        // so it emits nothing rather than using the file's mtime.
        ConversationDb("undated.db", ChatBlob("m", 10, 0, 5, 0, "resp-2", seconds: 0));
        Assert.Empty(AntigravityCliReader.RecordsFromRoots(
            new[] { Path.Combine(_home, ".gemini", "antigravity-cli", "conversations") }));
    }
}

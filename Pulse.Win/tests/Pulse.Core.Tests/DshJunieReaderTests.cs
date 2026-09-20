using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// DSH reader tests: forked prefix skipping (seq below seedLength), reasoning
/// contained in output (kept whole), the zstd magic check, and the composite
/// dedup identity that makes a call copied into a fork collapse with its
/// original.
/// </summary>
public class DshUsageReaderTests
{
    private static readonly string[] Header =
    [
        """{"type":"session","id":"sess-1","cwd":"E:/Code/MyTrace","seq":0,"seedLength":2}""",
        """{"type":"request/header","seq":1,"data":{"header":{"config":{"provider":"deepseek","model":"dsh-model"}}}}""",
    ];

    private static string Assistant(int seq, int input, int output, int reasoning = 0, string id = "m1")
    {
        return string.Concat(
            "{\"type\":\"assistant/message\",\"seq\":", seq.ToString(),
            ",\"time\":1789981200000,\"data\":{\"usage\":{\"inputTokens\":", input.ToString(),
            ",\"outputTokens\":", output.ToString(),
            ",\"reasoningTokens\":", reasoning.ToString(),
            "},\"message\":{\"id\":\"", id,
            "\",\"source\":{\"provider\":\"deepseek\"}}}}");
    }

    [Fact]
    public void Forked_Prefix_Below_SeedLength_Is_Skipped()
    {
        // seq 0 and 1 were inherited verbatim from the fork parent; only seq >=
        // seedLength is this session's work.
        var lines = new[]
        {
            Header[0],
            Header[1],
            Assistant(0, input: 999, output: 99, id: "inherited"),
            Assistant(2, input: 100, output: 20),
        };
        var record = Assert.Single(DshUsageReader.ParseLines(lines));
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal("sess-1", record.SessionID);
    }

    [Fact]
    public void Reasoning_Is_Kept_Inside_Output_Not_Added()
    {
        // The format states reasoningTokens is contained in outputTokens; the
        // reported output stays whole.
        var lines = new[]
        {
            Header[0],
            Header[1],
            """{"type":"assistant/message","seq":5,"time":1789981200000,"data":{"usage":{"inputTokens":100,"outputTokens":80,"reasoningTokens":30},"message":{"id":"m9"}}}""",
        };
        var record = Assert.Single(DshUsageReader.ParseLines(lines));
        Assert.Equal(80, record.Tally.Output);
        Assert.Equal(100, record.Tally.Input);
    }

    [Fact]
    public void Compaction_Summary_Is_Also_A_Provider_Call()
    {
        var lines = new[]
        {
            Header[0],
            Header[1],
            """{"type":"compaction/summary","seq":5,"time":1789981300000,"data":{"compactionId":"cmp-1","usage":{"inputTokens":50,"outputTokens":5}}}""",
        };
        var record = Assert.Single(DshUsageReader.ParseLines(lines));
        Assert.Contains("summary:cmp:cmp-1", record.DeduplicationID);
    }

    [Fact]
    public void Model_Falls_Back_To_Request_Header()
    {
        var lines = new[]
        {
            Header[0],
            Header[1],
            """{"type":"assistant/message","seq":5,"time":1789981200000,"data":{"usage":{"inputTokens":10,"outputTokens":1},"message":{"id":"m2"}}}""",
        };
        var record = Assert.Single(DshUsageReader.ParseLines(lines));
        Assert.Equal("dsh-model", record.Model); // from the header config
    }

    [Fact]
    public void Zstd_Magic_Is_The_Truth_Not_The_Suffix()
    {
        // The frame magic (28 B5 2F FD) identifies compressed bytes regardless
        // of the file name.
        var magic = new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x01 };
        Assert.True(DshUsageReader.IsZstd(magic));
        Assert.False(DshUsageReader.IsZstd("{ \"json\": true }"u8.ToArray()));
    }
}

/// <summary>
/// Junie reader tests: latency-anchored start times, session-id date fallback,
/// reasoning left out with a partial mark, and the multi-model modelUsage array.
/// </summary>
public class JunieUsageReaderTests : IDisposable
{
    private readonly string _home;

    public JunieUsageReaderTests() => _home = Path.Combine(Path.GetTempPath(), $"pulse-junie-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private void WriteEvents(string sessionId, params string[] events)
    {
        var dir = Path.Combine(_home, ".junie", "sessions", sessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "events.jsonl"), events);
    }

    private static string MetadataEvent(long timestampMs, string model, int input, int output, int latency = 0, int reasoning = 0)
    {
        return string.Concat(
            "{\"timestampMs\":", timestampMs.ToString(),
            ",\"event\":{\"agentEvent\":{\"kind\":\"LlmResponseMetadataEvent\",\"agent\":{\"name\":\"plan\"},\"modelUsage\":[{\"model\":\"", model,
            "\",\"inputTokens\":", input.ToString(),
            ",\"outputTokens\":", output.ToString(),
            ",\"time\":", latency.ToString(),
            ",\"reasoningTokens\":", reasoning.ToString(), "}]}}}");
    }

    [Fact]
    public void Positive_Latency_Anchors_The_Call_At_Its_Start()
    {
        // timestampMs is the response END; usage.time is the latency, so the
        // quarter-hour belongs to when the call began.
        WriteEvents("session-260921-100000", MetadataEvent(1789981200000, "m", input: 100, output: 10, latency: 30000));
        var record = Assert.Single(JunieUsageReader.Records(_home));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789981200000 - 30000), record.Timestamp);
    }

    [Fact]
    public void Zero_Timestamp_Falls_Back_To_The_Session_Id_Date()
    {
        // A zero millisecond time is "unset", not 1970: the session id's own
        // YYMMDD-HHMMSS date is the fallback.
        WriteEvents("session-260921-100000", MetadataEvent(0, "m", input: 100, output: 10));
        var record = Assert.Single(JunieUsageReader.Records(_home));
        var expected = DateTimeOffset.Parse("2026-09-21T10:00:00");
        Assert.Equal(expected.ToUnixTimeMilliseconds(), record.Timestamp.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Reasoning_Is_Left_Out_And_Marks_Partial()
    {
        // Containment between reasoningTokens and outputTokens is undeclared:
        // output stays whole, the ambiguous count is unplaced, run is partial.
        WriteEvents("session-260921-100000", MetadataEvent(1789981200000, "m", input: 100, output: 50, reasoning: 30));
        var record = Assert.Single(JunieUsageReader.Records(_home));
        Assert.Equal(50, record.Tally.Output);
        Assert.True(record.IsPartial);
    }

    [Fact]
    public void Multi_Model_Usage_Array_Is_One_Record_Per_Entry()
    {
        WriteEvents("session-260921-100000",
            """{"timestampMs":1789981200000,"event":{"agentEvent":{"kind":"LlmResponseMetadataEvent","modelUsage":[{"model":"router","inputTokens":10,"outputTokens":1},{"model":"worker","inputTokens":90,"outputTokens":9}]}}}""");
        var records = JunieUsageReader.Records(_home);
        Assert.Equal(2, records.Count);
        Assert.Equal("router", records[0].Model);
        Assert.Equal("worker", records[1].Model);
    }

    [Fact]
    public void SessionIDTime_Parses_The_Encoded_Start()
    {
        var parsed = JunieUsageReader.SessionIDTime("session-260921-100000");
        Assert.NotNull(parsed);
        Assert.Equal(10, parsed!.Value.ToLocalTime().Hour);
        Assert.Null(JunieUsageReader.SessionIDTime("no-marker"));
    }
}

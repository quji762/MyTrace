using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Copilot OTEL reader contract tests over hand-built JSONL lines. Locks the
/// upstream semantics: four lanes in priority order, cross-lane suppression by
/// trace/response id, disjoint token kinds (cache read removed from input once,
/// reasoning only standing in when output is absent), and the bare-total
/// unclassified lane.
/// </summary>
public class CopilotOtelReaderTests : IDisposable
{
    private readonly string _root;

    public CopilotOtelReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-otel-{Guid.NewGuid():N}", "otel");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch (IOException) { }
    }

    private void WriteFile(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_root, name), lines);

    private IReadOnlyList<AgentUsageRecord> Run() => CopilotOtelReader.RecordsFromRoots(
        new[] { Directory.GetParent(_root)!.FullName });

    [Fact]
    public void Chat_Span_With_Cache_Read_Makes_Kinds_Disjoint()
    {
        // Input includes the cache read; remove it once. Reasoning is a subset
        // of output and does not stack.
        WriteFile("a.jsonl",
            """{"type":"span","name":"chat request","traceId":"abc123","spanId":"span01","startTime":"2026-09-21T10:00:00Z","attributes":{"gen_ai.operation.name":"chat","gen_ai.request.model":"gpt-5","gen_ai.usage.input_tokens":1000,"gen_ai.usage.output_tokens":200,"gen_ai.usage.cache_read.input_tokens":400,"gen_ai.usage.reasoning_tokens":150}}""");

        var record = Assert.Single(Run());
        Assert.Equal(600, record.Tally.Input);      // 1000 - 400
        Assert.Equal(400, record.Tally.CacheRead);
        Assert.Equal(200, record.Tally.Output);     // reasoning stays inside output
        Assert.Equal(0, record.UnclassifiedTokens);
    }

    [Fact]
    public void Higher_Lane_Suppresses_Lower_Lane_Sharing_A_Trace()
    {
        // An inference log describing the same trace as a chat span is dropped
        // whole: emitting both would double the call.
        WriteFile("a.jsonl",
            """{"type":"span","name":"chat request","traceId":"trace9","spanId":"s1","startTime":"2026-09-21T10:00:00Z","attributes":{"gen_ai.operation.name":"chat","gen_ai.request.model":"gpt-5","gen_ai.usage.input_tokens":100,"gen_ai.usage.output_tokens":10}}""",
            """{"type":"event","traceId":"trace9","timestamp":"2026-09-21T10:00:02Z","attributes":{"event.name":"gen_ai.client.inference.operation.details","gen_ai.request.model":"gpt-5","gen_ai.usage.input_tokens":100,"gen_ai.usage.output_tokens":10}}""");

        var records = Run();
        var record = Assert.Single(records);
        Assert.Contains("copilot-otel:", record.DeduplicationID);
        Assert.DoesNotContain("log:", record.DeduplicationID); // the log lane lost
    }

    [Fact]
    public void Reasoning_Only_Stands_In_When_Output_Absent()
    {
        WriteFile("a.jsonl",
            """{"type":"span","name":"chat x","traceId":"t1","spanId":"s1","startTime":"2026-09-21T10:00:00Z","attributes":{"gen_ai.operation.name":"chat","gen_ai.request.model":"m","gen_ai.usage.input_tokens":50,"gen_ai.usage.reasoning_tokens":30}}""");
        var record = Assert.Single(Run());
        Assert.Equal(30, record.Tally.Output); // stood in
        Assert.Equal(50, record.Tally.Input);
    }

    [Fact]
    public void Bare_Total_With_No_Split_Is_Unclassified()
    {
        // A total with no split is real work we cannot place: counted as
        // unclassified, never put into the input bucket, never priced.
        WriteFile("a.jsonl",
            """{"type":"span","name":"chat x","traceId":"t2","spanId":"s2","startTime":"2026-09-21T10:00:00Z","attributes":{"gen_ai.operation.name":"chat","gen_ai.request.model":"m","gen_ai.usage.total_tokens":777}}""");
        var record = Assert.Single(Run());
        Assert.Equal(777, record.UnclassifiedTokens);
        Assert.Equal(0, record.Tally.Total);
        Assert.True(record.KnownTotal == 0);
    }

    [Fact]
    public void No_Trace_Nor_Response_Id_Is_Marked_Partial()
    {
        WriteFile("a.jsonl",
            """{"body":"GenAI inference: something","timestamp":"2026-09-21T10:00:00Z","attributes":{"event.name":"gen_ai.client.inference.operation.details","gen_ai.request.model":"m","gen_ai.usage.input_tokens":10,"gen_ai.usage.output_tokens":1}}""");
        var record = Assert.Single(Run());
        Assert.True(record.IsPartial); // the schema cannot prove another lane covers it
    }

    [Fact]
    public void Usage_Line_Gets_Model_From_A_Later_Line_Sharing_The_Trace()
    {
        // The usage line lands before its model arrives on another line with
        // the same trace id; the file is resolved as a whole.
        WriteFile("a.jsonl",
            """{"type":"span","name":"chat request","traceId":"late","spanId":"s9","startTime":"2026-09-21T10:00:00Z","attributes":{"gen_ai.operation.name":"chat","gen_ai.usage.input_tokens":10,"gen_ai.usage.output_tokens":1}}""",
            """{"type":"event","traceId":"late","attributes":{"gen_ai.response.model":"gpt-5-mini"}}""");
        var record = Assert.Single(Run());
        Assert.Equal("gpt-5-mini", record.Model);
    }

    [Fact]
    public void Zero_Sentinel_Ids_Are_Absent()
    {
        // W3C's non-recording sentinel (all zeroes) must not act as an identity.
        WriteFile("a.jsonl",
            """{"type":"span","name":"chat x","traceId":"00000000000000000000000000000000","spanId":"s","startTime":"2026-09-21T10:00:00Z","attributes":{"gen_ai.operation.name":"chat","gen_ai.request.model":"m","gen_ai.usage.input_tokens":1,"gen_ai.usage.output_tokens":1}}""");
        var record = Assert.Single(Run());
        Assert.True(record.IsPartial); // no real trace: cannot be matched
    }

    [Fact]
    public void Missing_Timestamp_Is_Not_Dated_By_The_Clock()
    {
        WriteFile("a.jsonl",
            """{"type":"span","name":"chat x","traceId":"t3","spanId":"s3","attributes":{"gen_ai.operation.name":"chat","gen_ai.request.model":"m","gen_ai.usage.input_tokens":10,"gen_ai.usage.output_tokens":1}}""");
        Assert.Empty(Run()); // a record with no usable time is not dated at all
    }
}

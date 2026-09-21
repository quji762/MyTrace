using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// OpenCodeReview reader tests: the total-driven cache relation (disjoint /
/// contained / neither / unprovable), uuid and fragment identities, the
/// duration-anchored start time, and the run-wide partial mark when an
/// unprovable usage was dropped.
/// </summary>
public class OpenCodeReviewReaderTests : IDisposable
{
    private readonly string _root;

    public OpenCodeReviewReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-ocr-{Guid.NewGuid():N}", ".opencodereview", "sessions", "repo");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_root))!, recursive: true); }
        catch (IOException) { }
    }

    private const string Header = """{"type":"session_start","sessionId":"s1","cwd":"E:/Code/MyTrace","model":"p/gjc-model"}""";

    private static string Response(string usageJson, long durationMs = 0, string? uuid = null) =>
        (uuid is null)
            ? $"{{\"type\":\"llm_response\",\"timestamp\":1789981200000,\"duration_ms\":{durationMs},\"model\":\"p/m1\",\"usage\":{usageJson}}}"
            : $"{{\"type\":\"llm_response\",\"uuid\":\"{uuid}\",\"timestamp\":1789981200000,\"duration_ms\":{durationMs},\"model\":\"p/m1\",\"usage\":{usageJson}}}";

    [Fact]
    public void Disjoint_Total_Counts_Four_Kinds_As_Reported()
    {
        // total 1150 = 1000 + 50 + 30 + 20: the four kinds are disjoint.
        WriteSession("s1.jsonl", Header, Response(
            """{"prompt_tokens":1000,"completion_tokens":50,"cache_read_tokens":30,"cache_write_tokens":20,"total_tokens":1100}""", uuid: "u1"));

        var record = Assert.Single(OpenCodeReviewReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(1000, record.Tally.Input);
        Assert.Equal(50, record.Tally.Output);
        Assert.Equal(30, record.Tally.CacheRead);
        Assert.Equal(20, record.Tally.CacheWrite);
        Assert.Equal("opencodereview:s1:uuid:u1", record.DeduplicationID);
    }

    [Fact]
    public void Total_Equal_To_Prompt_Plus_Completion_Subtracts_The_Cache()
    {
        // total 1050 = 1000 + 50, and != disjoint: prompt already contains the cache.
        WriteSession("s1.jsonl", Header, Response(
            """{"prompt_tokens":1000,"completion_tokens":50,"cache_read_tokens":300,"cache_write_tokens":100,"total_tokens":1050}""", uuid: "u2"));

        var record = Assert.Single(OpenCodeReviewReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(600, record.Tally.Input); // 1000 - 300 - 100
        Assert.Equal(300, record.Tally.CacheRead);
        Assert.Equal(100, record.Tally.CacheWrite);
        Assert.Equal(50, record.Tally.Output);
    }

    [Fact]
    public void Total_Fitting_Neither_Shape_Is_Unclassified()
    {
        WriteSession("s1.jsonl", Header, Response(
            """{"prompt_tokens":100,"completion_tokens":50,"total_tokens":9999}""", uuid: "u3"));
        var record = Assert.Single(OpenCodeReviewReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(9999, record.UnclassifiedTokens);
        Assert.Equal(0, record.Tally.Total);
        Assert.False(record.IsPartial); // the total is complete, only its kinds unknown
    }

    [Fact]
    public void Positive_Cache_With_No_Total_Is_Unprovable_And_Dropped()
    {
        // The ordinary persisted shape: no total, positive cache. No
        // complete-looking number may be produced.
        WriteSession("s1.jsonl", Header, Response(
            """{"prompt_tokens":1000,"completion_tokens":50,"cache_read_tokens":300}""", uuid: "u4"));
        Assert.Empty(OpenCodeReviewReader.RecordsFromRoots(new[] { _root }));
    }

    [Fact]
    public void Unprovable_Drop_Marks_The_Run_Partial()
    {
        WriteSession("s1.jsonl", Header,
            Response("""{"prompt_tokens":1000,"completion_tokens":50,"cache_read_tokens":300}""", uuid: "u4"),
            Response("""{"prompt_tokens":10,"completion_tokens":1}""", uuid: "u5"));
        var record = Assert.Single(OpenCodeReviewReader.RecordsFromRoots(new[] { _root }));
        Assert.True(record.IsPartial); // the source said more than can be proven
    }

    [Fact]
    public void Duration_Anchors_The_Start()
    {
        WriteSession("s1.jsonl", Header, Response(
            """{"prompt_tokens":10,"completion_tokens":1}""", durationMs: 30000, uuid: "u6"));
        var record = Assert.Single(OpenCodeReviewReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789981200000 - 30000), record.Timestamp);
    }

    [Fact]
    public void No_Cache_No_Total_Counts_As_Reported()
    {
        WriteSession("s1.jsonl", Header, Response(
            """{"prompt_tokens":100,"completion_tokens":10}""", uuid: "u7"));
        var record = Assert.Single(OpenCodeReviewReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(10, record.Tally.Output);
    }

    private void WriteSession(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_root, name), lines);
}

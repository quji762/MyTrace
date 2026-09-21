using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Reasonix daily-stats tests: only real non-empty rows count, the turn
/// marker is not a call, cache hit is the read / cache miss the fresh input,
/// reasoning is a subset of completion and stays inside it, a bare total is
/// carried unclassified, and the provider-prefixed model is kept as written.
/// </summary>
public class ReasonixUsageReaderTests : IDisposable
{
    private readonly string _root;

    public ReasonixUsageReaderTests() =>
        _root = Path.Combine(Path.GetTempPath(), $"pulse-reasonix-{Guid.NewGuid():N}", "stats");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch (IOException) { }
    }

    private void WriteStats(string name, params string[] lines) =>
        Write(_root, name, lines);

    private static void Write(string root, string name, string[] lines)
    {
        Directory.CreateDirectory(root);
        File.WriteAllLines(Path.Combine(root, name), lines);
    }

    [Fact]
    public void A_Real_Row_Is_Split_With_CacheMiss_As_Fresh_Input()
    {
        WriteStats("2026-09-21.jsonl",
            """{"ts":1789981200,"model":"zhipu/glm-5.3","prompt":100,"completion":40,"reasoning":15,"cache_hit":30,"cache_miss":70,"total":140,"requests":2}""");

        var record = Assert.Single(ReasonixUsageReader.RecordsFromRoots(new[] { _root }));
        // cache_miss is the fresh input; completion kept whole (reasoning inside).
        Assert.Equal(70, record.Tally.Input);
        Assert.Equal(40, record.Tally.Output);
        Assert.Equal(30, record.Tally.CacheRead);
        Assert.Equal(0, record.Tally.CacheWrite);
        // The provider prefix is kept exactly as reported.
        Assert.Equal("zhipu/glm-5.3", record.Model);
        Assert.StartsWith("reasonix:2026-09-21.jsonl:1:", record.DeduplicationID);
    }

    [Fact]
    public void Without_CacheMiss_The_Input_Is_Prompt_Minus_CacheHit()
    {
        WriteStats("2026-09-21.jsonl",
            """{"ts":1789981200,"model":"m","prompt":100,"completion":40,"cache_hit":30,"total":110,"requests":1}""");

        var record = Assert.Single(ReasonixUsageReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(70, record.Tally.Input);
    }

    [Fact]
    public void Turn_Marker_Zero_Totals_And_Blank_Models_Are_Skipped()
    {
        WriteStats("2026-09-21.jsonl",
            """{"ts":1789981200,"turn":true,"model":"m","prompt":10,"completion":1,"total":11,"requests":1}""",
            """{"ts":1789981200,"model":"m","total":0,"requests":0}""",
            """{"ts":1789981200,"model":"","prompt":10,"completion":1,"total":11,"requests":1}""");
        Assert.Empty(ReasonixUsageReader.RecordsFromRoots(new[] { _root }));
    }

    [Fact]
    public void A_Bare_Total_Is_Carried_Unclassified_Never_Guessed()
    {
        WriteStats("2026-09-21.jsonl",
            """{"ts":1789981200,"model":"m","total":500,"requests":3}""");

        var record = Assert.Single(ReasonixUsageReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(0, record.Tally.Total);
        Assert.Equal(500, record.UnclassifiedTokens);
    }

    [Fact]
    public void No_Ts_No_Row_And_Environment_Homes_Are_Honoured()
    {
        WriteStats("2026-09-21.jsonl", """{"model":"m","total":10,"requests":1}""");
        Assert.Empty(ReasonixUsageReader.RecordsFromRoots(new[] { _root }));

        // REASONIX_STATE_HOME names the state directory itself.
        var env = new Dictionary<string, string?> { ["REASONIX_STATE_HOME"] = _root };
        WriteStats("2026-09-22.jsonl", """{"ts":1789981200,"model":"m","total":10,"requests":1}""");
        Assert.Single(ReasonixUsageReader.Records(environment: env));

        // REASONIX_HOME names its parent, with stats beneath it.
        var env2 = new Dictionary<string, string?> { ["REASONIX_HOME"] = Path.GetDirectoryName(_root) };
        Assert.Single(ReasonixUsageReader.Records(environment: env2));
    }
}

/// <summary>
/// Augment session tests: only completed turns count, the last non-empty
/// token_usage wins (streamed nodes are cumulative), finishedAt anchors the
/// timing at the end, and input/cache are independent as reported.
/// </summary>
public class AugmentUsageReaderTests : IDisposable
{
    private readonly string _root;

    public AugmentUsageReaderTests() =>
        _root = Path.Combine(Path.GetTempPath(), $"pulse-augment-{Guid.NewGuid():N}", "sessions");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch (IOException) { }
    }

    private void WriteSession(string name, string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, name), json);
    }

    private const string Turn = """
        {"finishedAt":1789981200,"completed":true,"sequenceId":"seq-1","exchange":{"model_id":"claude-opus-4.6","request_id":"req-1","response_nodes":[
            {"token_usage":{"input_tokens":50,"output_tokens":10}},
            {"token_usage":{"input_tokens":100,"cache_read_input_tokens":20,"cache_creation_input_tokens":5,"output_tokens":30}}
        ]}}
        """;

    [Fact]
    public void Only_Completed_Turns_Count_And_Last_Node_Wins()
    {
        WriteSession("s1.json", string.Concat(
            "{\"sessionId\":\"sess-1\",\"agentState\":{\"modelId\":\"fallback-model\"},\"chatHistory\":[",
            Turn,
            ",{\"finishedAt\":1789981201,\"completed\":false,\"exchange\":{\"model_id\":\"m\",\"response_nodes\":[{\"token_usage\":{\"input_tokens\":999,\"output_tokens\":999}}]}}",
            "]}"));

        var record = Assert.Single(AugmentUsageReader.RecordsFromRoots(new[] { _root }));
        // The last non-empty node is the turn's own total, never the sum.
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(5, record.Tally.CacheWrite);
        Assert.Equal(20, record.Tally.CacheRead);
        Assert.Equal(30, record.Tally.Output);
        Assert.Equal("claude-opus-4.6", record.Model);
        Assert.Equal("sess-1", record.SessionID);
        Assert.Equal("augment:sess-1:req-1", record.DeduplicationID);
    }

    [Fact]
    public void Session_Id_And_Model_Fall_Back_When_Untagged()
    {
        WriteSession("abc.json", string.Concat(
            "{\"agentState\":{\"modelId\":\"fallback-model\"},\"chatHistory\":[",
            "{\"finishedAt\":1789981200,\"completed\":true,\"exchange\":{\"response_nodes\":[{\"token_usage\":{\"input_tokens\":10,\"output_tokens\":1}}]}}",
            "]}"));

        var record = Assert.Single(AugmentUsageReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal("abc", record.SessionID);
        Assert.Equal("fallback-model", record.Model);
        Assert.Equal("augment:abc:0", record.DeduplicationID);
    }

    [Fact]
    public void Zero_Timestamps_And_Empty_Histories_Contribute_Nothing()
    {
        WriteSession("s1.json", """
            {"sessionId":"s","chatHistory":[{"finishedAt":0,"completed":true,"exchange":{"response_nodes":[{"token_usage":{"input_tokens":10,"output_tokens":1}}]}}]}
            """);
        WriteSession("s2.json", """{"sessionId":"s","chatHistory":[]}""");
        Assert.Empty(AugmentUsageReader.RecordsFromRoots(new[] { _root }));
    }
}

/// <summary>
/// Warp is a real source with no tokens: the reader is always empty, the pane
/// shows recognised-but-without-token-counts, and nothing is fabricated.
/// </summary>
public class CapturedWarpReaderTests
{
    [Fact]
    public void Warp_Always_Returns_No_Records()
    {
        Assert.Empty(CapturedWarpReader.Records());
    }
}

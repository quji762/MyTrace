using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// CapturedAntigravity reader tests: the session_meta fallback model, the
/// placeholder-id skip, reasoning left out with a partial mark, negative count
/// clamping, and the response-id dedup identity.
/// </summary>
public class CapturedAntigravityReaderTests
{
    private static IReadOnlyList<AgentUsageRecord> Parse(params string[] lines) =>
        CapturedAntigravityReader.ParseLines(lines);

    private const string Meta = """{"type":"session_meta","modelId":"gemini-3-pro"}""";

    private static string Usage(string modelId = "", int input = 100, int output = 20, int cacheRead = 10, int cacheWrite = 5, int reasoning = 0, long ts = 1789981200000, string responseId = "resp-1") =>
        string.Concat(
            "{\"type\":\"usage\",\"sessionId\":\"sess1\",\"timestamp\":", ts.ToString(),
            ",\"modelId\":\"", modelId, "\",\"responseId\":\"", responseId,
            "\",\"input\":", input.ToString(),
            ",\"output\":", output.ToString(),
            ",\"cacheRead\":", cacheRead.ToString(),
            ",\"cacheWrite\":", cacheWrite.ToString(),
            ",\"reasoning\":", reasoning.ToString(), "}");

    [Fact]
    public void Usage_Row_Parses_With_Session_Meta_Fallback()
    {
        var record = Assert.Single(Parse(Meta, Usage("", 100, 20, 10, 5)));
        Assert.Equal("gemini-3-pro", record.Model); // from session_meta fallback
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(5, record.Tally.CacheWrite);
        Assert.Equal("sess1", record.SessionID);
        Assert.Equal("antigravity:resp-1", record.DeduplicationID);
    }

    [Fact]
    public void Reasoning_Left_Out_And_Marks_Partial()
    {
        // The schema does not state whether reasoning is inside output: output
        // stays whole, reasoning is unplaced, the ambiguity is visible.
        var record = Assert.Single(Parse(Meta, Usage("", reasoning: 30)));
        Assert.Equal(20, record.Tally.Output);
        Assert.Equal(0, record.UnclassifiedTokens); // not carried as unknown either
        Assert.True(record.IsPartial);
    }

    [Fact]
    public void Placeholder_Model_Is_Skipped()
    {
        var records = Parse(Meta, Usage(modelId: "model_placeholder_abc"));
        Assert.Empty(records); // no invented name, no colliding price key
    }

    [Fact]
    public void Negative_Counts_Clamp_To_Zero()
    {
        var record = Assert.Single(Parse(Meta, Usage("", input: -5, output: 10)));
        Assert.Equal(0, record.Tally.Input); // clamped, never negative
        Assert.Equal(10, record.Tally.Output);
    }

    [Fact]
    public void All_Zero_Row_Is_Dropped()
    {
        var records = Parse(Meta, Usage("", input: 0, output: 0, cacheRead: 0, cacheWrite: 0));
        Assert.Empty(records);
    }

    [Fact]
    public void Row_With_Own_Model_Overrides_Fallback()
    {
        var records = Parse(Meta, Usage(modelId: "claude-opus-4.5"));
        Assert.Equal("claude-opus-4.5", Assert.Single(records).Model);
    }

    [Fact]
    public void No_Model_And_No_Fallback_Is_Skipped()
    {
        var records = Parse(
            """{"type":"session_meta"}""",
            Usage("", input: 10, output: 1));
        Assert.Empty(records); // a token count with no model cannot be priced
    }
}

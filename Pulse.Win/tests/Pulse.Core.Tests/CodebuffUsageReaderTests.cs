using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Codebuff reader tests over a hand-built chat-messages.json tree. Locks the
/// merge-not-sum rule (each field from the first non-zero source), the
/// Copilot-origin filter, chat-id timestamp restoration, and the nested
/// cache-read detail spellings.
/// </summary>
public class CodebuffUsageReaderTests : IDisposable
{
    private readonly string _root;

    public CodebuffUsageReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-codebuff-{Guid.NewGuid():N}",
            "manicode", "projects", "proj", "chats", "2026-09-21T10-00-00");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_root))))!, recursive: true); }
        catch (IOException) { }
    }

    private void WriteMessages(params string[] messages) =>
        File.WriteAllText(Path.Combine(_root, "chat-messages.json"),
            "[" + string.Join(",", messages) + "]");

    [Fact]
    public void Merged_Usage_Takes_First_Nonzero_Source_Per_Field()
    {
        // metadata.usage has input but a zero output; metadata.codebuff.usage
        // has the real output. A zero in the higher-priority copy must not mask
        // the real count, and neither field is summed.
        WriteMessages(
            """{"variant":"ai","id":"m1","timestamp":1789981200000,"metadata":{"usage":{"inputTokens":100,"outputTokens":0},"codebuff":{"usage":{"inputTokens":0,"outputTokens":50}},"model":"gpt-5"}}""");

        var record = Assert.Single(CodebuffUsageReader.RecordsFromRoots(new[] { Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_root)))! }));
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(50, record.Tally.Output);
        Assert.Equal(150, record.Tally.Total); // merged once, not 150+50
        Assert.Equal("manicode/proj/2026-09-21T10-00-00", record.SessionID);
    }

    [Fact]
    public void Nested_Prompt_Tokens_Details_Supply_Cache_Read()
    {
        WriteMessages(
            """{"variant":"ai","id":"m1","timestamp":1789981200000,"metadata":{"model":"m","usage":{"inputTokens":100,"outputTokens":10,"promptTokensDetails":{"cachedTokens":40}}}}""");

        var record = Assert.Single(CodebuffUsageReader.RecordsFromRoots(new[] { Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_root)))! }));
        Assert.Equal(40, record.Tally.CacheRead); // the nested detail spelling
    }

    [Fact]
    public void Chat_Id_Restores_To_The_Timestamp()
    {
        // A message with no time fields of its own falls back to the chat id,
        // whose time separators were written as dashes.
        WriteMessages(
            """{"variant":"ai","id":"m1","metadata":{"model":"m","usage":{"inputTokens":10,"outputTokens":1}}}""");

        var record = Assert.Single(CodebuffUsageReader.RecordsFromRoots(new[] { Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_root)))! }));
        Assert.Equal(DateTimeOffset.Parse("2026-09-21T10:00:00").ToUnixTimeMilliseconds(),
            record.Timestamp.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void User_Rows_And_Zero_Usage_Are_Skipped()
    {
        WriteMessages(
            """{"role":"user","content":"hello"}""",
            """{"variant":"ai","metadata":{}}""");
        Assert.Empty(CodebuffUsageReader.RecordsFromRoots(new[] { Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_root)))! }));
    }

    [Fact]
    public void Zero_Timestamp_Falls_Through_To_Created_At()
    {
        // Zero is "unset", not 1970: it falls through to the next real field.
        WriteMessages(
            """{"variant":"ai","timestamp":0,"createdAt":1789981200000,"metadata":{"model":"m","usage":{"inputTokens":10,"outputTokens":1}}}""");

        var record = Assert.Single(CodebuffUsageReader.RecordsFromRoots(new[] { Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_root)))! }));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789981200000), record.Timestamp);
    }
}

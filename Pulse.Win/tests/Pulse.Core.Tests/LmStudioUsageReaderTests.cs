using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// LM Studio reader tests: the balanced usage block extraction from
/// pretty-printed logs, the cache/reasoning clamps, the log-line-only
/// timestamp, and the FNV fallback identity.
/// </summary>
public class LmStudioUsageReaderTests
{
    private const string LogTemplate =
        "2026-09-21 17:59:30  [INFO] Starting response.\n" +
        "{\n  \"id\": \"chatcmpl-1\",\n  \"model\": \"qwen3-coder\",\n  \"usage\": {\"prompt_tokens\": 1000, \"completion_tokens\": 500, \"total_tokens\": 1600}\n}\n";

    [Fact]
    public void Usage_Block_Is_Extracted_From_Pretty_Printed_Log()
    {
        var records = LmStudioUsageReader.RecordsFromText(LogTemplate, "/logs/server.log");
        var record = Assert.Single(records);

        // Prompt cache-inclusive, completion includes reasoning: fresh input is
        // the total minus everything already accounted for.
        Assert.Equal(1100, record.Tally.Input);  // 1600 - 500
        Assert.Equal(500, record.Tally.Output);
        Assert.Equal("qwen3-coder", record.Model);
        Assert.Equal("lmstudio:/logs/server.log", record.SessionID);
        Assert.StartsWith("lmstudio:chatcmpl-1", record.DeduplicationID);
    }

    [Fact]
    public void Cache_Read_And_Write_Are_Clamped_To_The_Prompt()
    {
        const string log =
            "2026-09-21 17:59:30 log\n" +
            "{\"id\":\"c2\",\"model\":\"m\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":300,\"cache_creation_input_tokens\":200},\"total_tokens\":300}}";
        var record = Assert.Single(LmStudioUsageReader.RecordsFromText(log, "/logs/x.log"));

        Assert.Equal(100, record.Tally.CacheRead);   // clamped to prompt (100)
        Assert.Equal(0, record.Tally.CacheWrite);    // prompt 100 - read 100 = 0 left
        Assert.Equal(180, record.Tally.Input);       // total 300 - completion 20 - read 100 - write 0
    }

    [Fact]
    public void Bare_Total_Without_Prompt_Or_Completion_Is_Unclassified()
    {
        const string log =
            "2026-09-21 17:59:30 log\n" +
            "{\"id\":\"c3\",\"model\":\"m\",\"usage\":{\"total_tokens\":777}}";
        var record = Assert.Single(LmStudioUsageReader.RecordsFromText(log, "/logs/x.log"));
        Assert.Equal(777, record.UnclassifiedTokens);
        Assert.Equal(0, record.Tally.Total);
    }

    [Fact]
    public void No_Local_Log_Timestamp_Skips_The_Block()
    {
        // The timestamp is the log line's own and nothing else — never the
        // file's mtime or the clock.
        const string log = "{\"id\":\"c4\",\"model\":\"m\",\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":1}}";
        Assert.Empty(LmStudioUsageReader.RecordsFromText(log, "/logs/x.log"));
    }

    [Fact]
    public void Reasoning_Already_In_Output_Is_Not_Double_Counted()
    {
        const string log =
            "2026-09-21 17:59:30 log\n" +
            "{\"id\":\"c5\",\"model\":\"m\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":60,\"output_tokens_details\":{\"reasoning_tokens\":40}}}";
        var record = Assert.Single(LmStudioUsageReader.RecordsFromText(log, "/logs/x.log"));
        Assert.Equal(60, record.Tally.Output); // reasoning stays inside
        Assert.Equal(100, record.Tally.Input);
    }

    [Fact]
    public void Fnv_Hash_Is_Stable_Across_Calls()
    {
        // The fallback identity for a block with no id must be deterministic:
        // two runs over the same log must produce the same dedup key.
        var r1 = Assert.Single(LmStudioUsageReader.RecordsFromText(
            "2026-09-21 17:59:30 log\n" +
            "{\"model\":\"m\",\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":1}}", "/logs/a.log"));
        var r2 = Assert.Single(LmStudioUsageReader.RecordsFromText(
            "2026-09-21 17:59:30 log\n" +
            "{\"model\":\"m\",\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":1}}", "/logs/a.log"));
        Assert.Equal(r1.DeduplicationID, r2.DeduplicationID);
    }
}

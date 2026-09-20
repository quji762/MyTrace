using System.Text.Json;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Mux reader tests: bucket extraction, provider-prefix stripping, the shared
/// lastRequest timestamp as an aggregate, reasoning left out with a partial
/// mark, and a snapshot with no timestamp being skipped entirely.
/// </summary>
public class MuxUsageReaderTests : IDisposable
{
    private readonly string _root;

    public MuxUsageReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-mux-{Guid.NewGuid():N}", ".mux", "sessions", "ws1");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(_root)))!), recursive: true); }
        catch (Exception) { }
    }

    private string SessionsRoot => Path.GetDirectoryName(_root)!;

    private void WriteSnapshot(string json) =>
        File.WriteAllText(Path.Combine(_root, "session-usage.json"), json);

    [Fact]
    public void Buckets_Are_Read_And_The_Provider_Prefix_Is_Stripped()
    {
        WriteSnapshot("""
            { "version": 1,
              "byModel": { "anthropic:claude": {
                "input": { "tokens": 1000, "cost_usd": 0.01 },
                "cached": { "tokens": 200, "cost_usd": 0.00 },
                "cacheCreate": { "tokens": 50, "cost_usd": 0.01 },
                "output": { "tokens": 300, "cost_usd": 0.10 },
                "reasoning": { "tokens": 70, "cost_usd": 0.01 } } },
              "lastRequest": { "model": "claude", "timestamp": 1789981200000 } }
            """);

        var record = Assert.Single(MuxUsageReader.RecordsFromRoot(SessionsRoot));
        Assert.Equal("claude", record.Model); // "anthropic:claude" minus routing
        Assert.Equal(1000, record.Tally.Input);
        Assert.Equal(200, record.Tally.CacheRead);
        Assert.Equal(50, record.Tally.CacheWrite);
        Assert.Equal(300, record.Tally.Output);
        Assert.True(record.IsAggregate); // one shared session timestamp: no hourly profile
        Assert.Equal("mux:ws1:anthropic:claude", record.DeduplicationID);
    }

    [Fact]
    public void Reasoning_Is_Left_Out_And_Marks_Partial()
    {
        // No total exists to decide containment: output kept whole, reasoning
        // placed nowhere, the record says it is a possible undercount.
        WriteSnapshot("""
            { "byModel": { "m": {
                "input": { "tokens": 100 }, "output": { "tokens": 50 },
                "reasoning": { "tokens": 30 } } },
              "lastRequest": { "timestamp": 1789981200000 } }
            """);
        var record = Assert.Single(MuxUsageReader.RecordsFromRoot(SessionsRoot));
        Assert.Equal(50, record.Tally.Output);
        Assert.True(record.IsPartial);
    }

    [Fact]
    public void No_Timestamp_Skips_The_Session()
    {
        // Dated from the file's own mtime would be a fabricated instant.
        WriteSnapshot("""{ "byModel": { "m": { "input": { "tokens": 10 } } } }""");
        Assert.Empty(MuxUsageReader.RecordsFromRoot(SessionsRoot));
    }

    [Fact]
    public void Multi_Model_Snapshot_Emits_One_Record_Per_Model()
    {
        WriteSnapshot("""
            { "byModel": {
                "anthropic:claude": { "input": { "tokens": 100 }, "output": { "tokens": 10 } },
                "openai:gpt": { "input": { "tokens": 200 }, "output": { "tokens": 20 } } },
              "lastRequest": { "timestamp": 1789981200000 } }
            """);
        Assert.Equal(2, MuxUsageReader.RecordsFromRoot(SessionsRoot).Count);
    }

    [Fact]
    public void ModelName_Strips_Up_To_The_First_Colon_Only()
    {
        Assert.Equal("claude", MuxUsageReader.ModelName("anthropic:claude"));
        Assert.Equal("a:b", MuxUsageReader.ModelName("provider:a:b")); // only the first colon splits
        Assert.Equal("bare", MuxUsageReader.ModelName("bare"));
        Assert.Null(MuxUsageReader.ModelName("anthropic:"));
    }
}

/// <summary>
/// Freebuff: recognised by its agent type, but no records — character-count
/// estimates are not reported as usage, and a fabricated zero is worse.
/// </summary>
public class FreebuffUsageReaderTests
{
    [Fact]
    public void Freebuff_Agent_Type_Is_Recognised()
    {
        var message = JsonDocument.Parse(
            """{"metadata":{"runState":{"sessionState":{"mainAgentState":{"agentType":"base2-free-1"}}}}}""").RootElement;
        Assert.True(FreebuffUsageReader.IsFreebuff(message));
    }

    [Fact]
    public void Codebuff_Agent_Types_Are_Not_Freebuff()
    {
        foreach (var agentType in new[] { "base2", "base2-lite", "base2-max", "base2-plan" })
        {
            var json = string.Concat(
                "{\"metadata\":{\"runState\":{\"sessionState\":{\"mainAgentState\":{\"agentType\":\"",
                agentType, "\"}}}}}");
            using var document = JsonDocument.Parse(json);
            Assert.False(FreebuffUsageReader.IsFreebuff(document.RootElement));
        }
    }

    [Fact]
    public void Records_Are_Always_Empty_By_Design()
    {
        // Character-count estimates are not reported as usage; a fabricated
        // zero would be worse than an honest absence.
        Assert.Empty(FreebuffUsageReader.Records());
        Assert.Contains("no token counters", FreebuffUsageReader.Limitation);
    }
}

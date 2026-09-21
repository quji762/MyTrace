using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Droid reader tests: the four-identity total reconciliation, the no-total
/// ambiguous-cache case (input carried as unknown, record partial), thinking of
/// undocumented relation, the model fallback chain, and provider placeholders.
/// </summary>
public class DroidSessionReaderTests : IDisposable
{
    private readonly string _root;

    public DroidSessionReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-droid-{Guid.NewGuid():N}", ".factory", "sessions");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_root))!, recursive: true); }
        catch (IOException) { }
    }

    private void WriteSettings(string session, string json, string? transcript = null)
    {
        File.WriteAllText(Path.Combine(_root, $"{session}.settings.json"), json);
        if (transcript is not null)
            File.WriteAllText(Path.Combine(_root, $"{session}.jsonl"), transcript);
    }

    [Fact]
    public void Total_Reconciles_The_Reported_Input_As_Wholesale()
    {
        // total 1900 = input 1000 + cacheWrite 500 + cacheRead 300 + output 100:
        // variant three matches exactly, proving the reported input is the
        // wholesale figure (cache counted beside it), and it is kept as-is.
        WriteSettings("s1",
            """{"model":"claude-opus-4.5","providerLockTimestamp":1789981200000,"tokenUsage":{"inputTokens":1000,"outputTokens":100,"cacheReadTokens":300,"cacheCreationTokens":500,"totalTokens":1900}}""");

        var record = Assert.Single(DroidSessionReader.RecordsFromRoot(_root));
        Assert.Equal(1000, record.Tally.Input);
        Assert.Equal(500, record.Tally.CacheWrite);
        Assert.Equal(300, record.Tally.CacheRead);
        Assert.Equal(100, record.Tally.Output);
        Assert.Equal("claude-opus-4.5", record.Model); // case and dots kept
        Assert.True(record.IsAggregate);         // a session total, not a turn
        Assert.False(record.IsPartial);
    }

    [Fact]
    public void Total_Proving_Cache_Inside_Input_Subtracts()
    {
        // total 1100 = (input 1000 - 300 read - 500 write = 200) + 500 + 300 + 100:
        // variant two matches, proving the cache sat inside the reported input.
        WriteSettings("s2",
            """{"model":"m","providerLockTimestamp":1789981200000,"tokenUsage":{"inputTokens":1000,"outputTokens":100,"cacheReadTokens":300,"cacheCreationTokens":500,"totalTokens":1100}}""");

        var record = Assert.Single(DroidSessionReader.RecordsFromRoot(_root).Where(r => r.SessionID == "s2"));
        Assert.Equal(200, record.Tally.Input); // subtracted, proven by the total
    }

    [Fact]
    public void Total_Matching_Neither_Identity_Is_Unclassified()
    {
        WriteSettings("s1",
            """{"model":"m","providerLockTimestamp":1789981200000,"tokenUsage":{"inputTokens":100,"outputTokens":10,"totalTokens":9999}}""");
        var record = Assert.Single(DroidSessionReader.RecordsFromRoot(_root));
        Assert.Equal(9999, record.UnclassifiedTokens);
        Assert.False(record.IsPartial); // the total is complete, only its kinds unknown
    }

    [Fact]
    public void Positive_Cache_With_No_Total_Is_Partial_Input_Unknown()
    {
        // The cache relation is not provable: output kept priced, input carried
        // as a known unknown, nothing added for the cache.
        WriteSettings("s1",
            """{"model":"m","providerLockTimestamp":1789981200000,"tokenUsage":{"inputTokens":1000,"outputTokens":50,"cacheReadTokens":400}}""");
        var record = Assert.Single(DroidSessionReader.RecordsFromRoot(_root));
        Assert.Equal(50, record.Tally.Output);
        Assert.Equal(1000, record.UnclassifiedTokens);
        Assert.Equal(0, record.Tally.Input);
        Assert.True(record.IsPartial);
    }

    [Fact]
    public void Thinking_With_No_Total_Marks_Partial()
    {
        WriteSettings("s1",
            """{"model":"m","providerLockTimestamp":1789981200000,"tokenUsage":{"inputTokens":100,"thinkingTokens":40,"outputTokens":20}}""");
        var record = Assert.Single(DroidSessionReader.RecordsFromRoot(_root));
        Assert.Equal(20, record.Tally.Output); // output kept; thinking not added
        Assert.True(record.IsPartial);
    }

    [Fact]
    public void Model_Falls_Back_To_Transcript_Then_Provider_Placeholder()
    {
        WriteSettings("s1",
            """{"providerLock":"anthropic","providerLockTimestamp":1789981200000,"tokenUsage":{"inputTokens":10,"outputTokens":1}}""",
            transcript: """system reminder Model: claude-opus-4.5 more text""");
        var record = Assert.Single(DroidSessionReader.RecordsFromRoot(_root));
        Assert.Equal("claude-opus-4.5", record.Model); // from the transcript reminder
    }

    [Fact]
    public void Provider_With_No_Model_Gets_A_Valueless_Placeholder()
    {
        WriteSettings("s1",
            """{"providerLock":"anthropic","providerLockTimestamp":1789981200000,"tokenUsage":{"inputTokens":10,"outputTokens":1}}""");
        var record = Assert.Single(DroidSessionReader.RecordsFromRoot(_root));
        Assert.Equal("claude-unknown", record.Model); // never a concrete model's rates
    }

    [Fact]
    public void No_Locatable_Time_Marks_The_Run_Partial()
    {
        WriteSettings("s1",
            """{"model":"m","tokenUsage":{"inputTokens":10,"outputTokens":1}}""");
        WriteSettings("s2",
            """{"model":"m","providerLockTimestamp":1789981200000,"tokenUsage":{"inputTokens":20,"outputTokens":2}}""");
        var records = DroidSessionReader.RecordsFromRoot(_root);
        Assert.Single(records);                       // s1 has no time: not emitted
        Assert.True(records[0].IsPartial);            // and the survivor says the run was partial
    }

    [Fact]
    public void Normalize_Removes_Custom_Prefix_And_Brackets_Only()
    {
        Assert.Equal("claude-opus-4.5", DroidSessionReader.Normalize("custom:claude-opus-4.5"));
        Assert.Equal("m", DroidSessionReader.Normalize("m [via proxy]"));
        Assert.Equal("claude-opus-4.5", DroidSessionReader.Normalize("claude-opus-4.5")); // dots kept
        Assert.Null(DroidSessionReader.Normalize("  "));
    }
}

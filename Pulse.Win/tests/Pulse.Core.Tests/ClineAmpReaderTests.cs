using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Cline CLI reader tests over hand-built session stores in a temp profile.
/// Cache-inclusive input clamped at zero; env roots precede the home default.
/// </summary>
public class ClineCliReaderTests : IDisposable
{
    private readonly string _home;

    public ClineCliReaderTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"pulse-cline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string DefaultRoot => Path.Combine(_home, ".cline", "data", "sessions");

    private void WriteSession(string dir, string stem, string messages, string? manifest = null)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{stem}.messages.json"), messages);
        if (manifest is not null)
            File.WriteAllText(Path.Combine(dir, $"{stem}.json"), manifest);
    }

    [Fact]
    public void Cache_Inclusive_Input_Is_Split_Back_Out()
    {
        WriteSession(DefaultRoot, "s1",
            """{"sessionId":"s1","messages":[{"role":"assistant","id":"m1","ts":1789981200000,"metrics":{"inputTokens":1000,"cacheReadTokens":300,"cacheWriteTokens":100,"outputTokens":50},"modelInfo":{"id":"claude-sonnet-4-5"}}]}""",
            """{"sessionId":"s1","workspace_root":"E:\\Code\\MyTrace","metadata":{"title":"fix login"}}""");

        var record = Assert.Single(ClineCliReader.Records([DefaultRoot]));
        // Fresh input = 1000 - 300 read - 100 write = 600.
        Assert.Equal(600, record.Tally.Input);
        Assert.Equal(100, record.Tally.CacheWrite);
        Assert.Equal(300, record.Tally.CacheRead);
        Assert.Equal(50, record.Tally.Output);
        Assert.Equal("claude-sonnet-4-5", record.Model);
        Assert.Equal("fix login", record.Title);
        Assert.Equal("MyTrace", record.Project);
        Assert.Equal("cline:s1:m1", record.DeduplicationID);
    }

    [Fact]
    public void Over_Subtracted_Input_Clamps_To_Zero()
    {
        WriteSession(DefaultRoot, "s1",
            """{"sessionId":"s1","messages":[{"role":"assistant","id":"m1","ts":1789981200000,"metrics":{"inputTokens":50,"cacheReadTokens":300,"cacheWriteTokens":100,"outputTokens":10},"modelInfo":{"id":"m"}}]}""");

        var record = Assert.Single(ClineCliReader.Records([DefaultRoot]));
        Assert.Equal(0, record.Tally.Input); // clamped, never negative
        Assert.Equal(300, record.Tally.CacheRead);
    }

    [Fact]
    public void Env_Roots_Precede_The_Home_Default_In_CLI_Order()
    {
        var alt = Path.Combine(_home, "alt");
        Directory.CreateDirectory(alt);
        File.WriteAllText(Path.Combine(alt, "s.messages.json"),
            """{"sessionId":"alt-s","messages":[{"role":"assistant","id":"m","ts":1789981200000,"metrics":{"inputTokens":5,"outputTokens":1},"modelInfo":{"id":"m"}}]}""");

        var roots = ClineCliReader.Roots(_home, new Dictionary<string, string?>
        {
            ["CLINE_SESSION_DATA_DIR"] = alt,
            ["CLINE_DATA_DIR"] = null,
        });
        var record = Assert.Single(ClineCliReader.Records(roots));
        Assert.Equal("alt-s", record.SessionID);

        // Blank env values are ignored rather than treated as a path.
        var blankRoots = ClineCliReader.Roots(_home, new Dictionary<string, string?> { ["CLINE_SESSION_DATA_DIR"] = "  " });
        Assert.Equal(Path.Combine(_home, ".cline", "data", "sessions"), Assert.Single(blankRoots));
    }

    [Fact]
    public void Message_Without_Ts_Is_Not_Backfilled()
    {
        WriteSession(DefaultRoot, "s1",
            """{"sessionId":"s1","messages":[{"role":"assistant","id":"m1","metrics":{"inputTokens":10,"outputTokens":1},"modelInfo":{"id":"m"}}]}""");
        Assert.Empty(ClineCliReader.Records([DefaultRoot]));
    }

    [Fact]
    public void Non_Assistant_Or_Metrics_Less_Messages_Contribute_Nothing()
    {
        WriteSession(DefaultRoot, "s1",
            """{"sessionId":"s1","messages":[{"role":"user","id":"u1","ts":1789981200000,"metrics":{"inputTokens":99,"outputTokens":9},"modelInfo":{"id":"m"}},{"role":"assistant","id":"m2","ts":1789981200000}]}""");
        Assert.Empty(ClineCliReader.Records([DefaultRoot]));
    }
}

/// <summary>
/// Amp thread reconciliation tests: the ledger is the primary record and a
/// matched message is NOT emitted again — emitting both sides would double
/// every call.
/// </summary>
public class AmpSessionReaderTests : IDisposable
{
    private readonly string _root;

    public AmpSessionReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-amp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string WriteThread(string json)
    {
        var path = Path.Combine(_root, "T-abc.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Matched_Message_Is_Not_Emitted_Again()
    {
        // Ledger event carries the truth with a time; the message describing the
        // same call is matched by toMessageId and dropped.
        WriteThread("""
            {
              "id": "T-abc", "created": 1789981200000,
              "messages": [
                { "role": "assistant", "messageId": 7,
                  "usage": { "model": "claude-opus-4-1", "inputTokens": 100, "outputTokens": 10 } }
              ],
              "usageLedger": { "events": [
                { "model": "claude-opus-4-1", "timestamp": "2026-09-21T10:00:00Z",
                  "toMessageId": 7,
                  "tokens": { "input": 100, "output": 10 } }
              ] }
            }
            """);
        var (records, incomplete) = AmpSessionReader.Thread(Path.Combine(_root, "T-abc.json"));

        Assert.False(incomplete);
        var record = Assert.Single(records);
        Assert.Contains(":event:7", record.DeduplicationID);
        Assert.False(record.IsAggregate);
    }

    [Fact]
    public void Unmatched_Message_Stands_Alone_As_Aggregate_At_Thread_Time()
    {
        WriteThread("""
            {
              "id": "T-abc", "created": 1789981200000,
              "messages": [
                { "role": "assistant", "messageId": 9,
                  "usage": { "model": "m", "inputTokens": 50, "outputTokens": 5 } }
              ]
            }
            """);
        var (records, incomplete) = AmpSessionReader.Thread(Path.Combine(_root, "T-abc.json"));

        Assert.False(incomplete);
        var record = Assert.Single(records);
        Assert.Contains(":message:9", record.DeduplicationID);
        Assert.True(record.IsAggregate); // no event stamp: placed at thread time, hourly profile excluded
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789981200000), record.Timestamp);
    }

    [Fact]
    public void Second_Match_Falls_To_Equal_Model_And_Tokens()
    {
        WriteThread("""
            {
              "id": "T-abc", "created": 1789981200000,
              "messages": [
                { "role": "assistant", "messageId": 7,
                  "usage": { "model": "m", "inputTokens": 100, "outputTokens": 10 } }
              ],
              "usageLedger": { "events": [
                { "model": "m", "tokens": { "input": 100, "output": 10 } }
              ] }
            }
            """);
        var (records, _) = AmpSessionReader.Thread(Path.Combine(_root, "T-abc.json"));
        Assert.Single(records); // folded by the model+tokens fallback match
    }

    [Fact]
    public void No_Time_At_All_Is_Not_Emitted_And_Marks_Incomplete()
    {
        WriteThread("""
            {
              "id": "T-abc",
              "messages": [
                { "role": "assistant", "messageId": 3,
                  "usage": { "model": "m", "inputTokens": 10, "outputTokens": 1 } }
              ],
              "usageLedger": { "events": [
                { "model": "m", "toMessageId": 3, "tokens": { "input": 10, "output": 1 } }
              ] }
            }
            """);
        var (records, incomplete) = AmpSessionReader.Thread(Path.Combine(_root, "T-abc.json"));
        Assert.Empty(records);
        Assert.True(incomplete); // inventing a timestamp from the message id is not a reading
    }

    [Fact]
    public void Dropped_Usage_Marks_All_Records_Partial()
    {
        WriteThread("""
            {
              "id": "T-abc", "created": 1789981200000,
              "messages": [
                { "role": "assistant", "messageId": 7,
                  "usage": { "inputTokens": 100, "outputTokens": 10 } }
              ],
              "usageLedger": { "events": [
                { "toMessageId": 7, "tokens": { "input": 100, "output": 10 } }
              ] }
            }
            """);
        var (records, incomplete) = AmpSessionReader.Thread(Path.Combine(_root, "T-abc.json"));
        // A usage object with no model field is real work the reader cannot
        // attribute: whatever survives is only a subset.
        Assert.True(incomplete);
        Assert.All(records, r => Assert.True(r.IsPartial));
    }
}

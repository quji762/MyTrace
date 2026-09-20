using System.Text.Json;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

public class KiroReaderTests : IDisposable
{
    private readonly string _home;

    public KiroReaderTests() => _home = Path.Combine(Path.GetTempPath(), $"pulse-kiro-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string WriteSession(string stem, string header, string? sidecar = null)
    {
        var root = Path.Combine(_home, ".kiro", "sessions", "cli");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, $"{stem}.json"), header);
        if (sidecar is not null)
            File.WriteAllText(Path.Combine(root, $"{stem}.jsonl"), sidecar);
        return root;
    }

    private const string HeaderTemplate = """
        {{"session_id":"{0}","cwd":"E:\\Code\\MyTrace",
          "session_state":{{"rts_model_state":{{"model_info":{{"model_id":"claude-sonnet-4-5"}}}},
                           "conversation_metadata":{{"user_turn_metadatas":[{1}]}}}}}}
        """;

    [Fact]
    public void Real_Counters_Produce_A_Record()
    {
        var root = WriteSession("s1", string.Format(HeaderTemplate, "s1",
            """{"input_token_count":100,"output_token_count":30,"end_timestamp":1789981200,"message_ids":["m1"]}"""));
        File.WriteAllText(Path.Combine(root, "s1.jsonl"),
            """{"kind":"Prompt","data":{"message_id":"m1","meta":{"timestamp":1789981100}}}""");

        var record = Assert.Single(KiroReader.Records(_home));
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(30, record.Tally.Output);
        Assert.Equal("claude-sonnet-4-5", record.Model);
        Assert.Equal("MyTrace", record.Project);
        Assert.Equal("s1:0", record.DeduplicationID);
        // The prompt's own sidecar time outranks end_timestamp.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789981100000), record.Timestamp);
    }

    [Fact]
    public void Both_Counters_Zero_Emits_Nothing()
    {
        // A zero is not a measurement; estimating one from text would be inventing it.
        var root = WriteSession("s1", string.Format(HeaderTemplate, "s1",
            """{"input_token_count":0,"output_token_count":0,"end_timestamp":1789981200}"""));
        Assert.Empty(KiroReader.Records(_home));
    }

    [Fact]
    public void Missing_Prompt_Falls_Back_To_End_Timestamp()
    {
        var root = WriteSession("s1", string.Format(HeaderTemplate, "s1",
            """{"input_token_count":50,"output_token_count":5,"end_timestamp":1789981200}"""));
        var record = Assert.Single(KiroReader.Records(_home));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789981200000), record.Timestamp);
    }

    [Fact]
    public void No_Time_At_All_Is_Skipped()
    {
        var root = WriteSession("s1", string.Format(HeaderTemplate, "s1",
            """{"input_token_count":50,"output_token_count":5}"""));
        Assert.Empty(KiroReader.Records(_home));
    }
}

public class QwenSessionReaderTests : IDisposable
{
    private readonly string _home;

    public QwenSessionReaderTests() => _home = Path.Combine(Path.GetTempPath(), $"pulse-qwen-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string WriteChat(string project, string stem, params string[] lines)
    {
        var chats = Path.Combine(_home, ".qwen", "projects", project, "chats");
        Directory.CreateDirectory(chats);
        File.WriteAllLines(Path.Combine(chats, $"{stem}.jsonl"), lines);
        return Path.Combine(chats, $"{stem}.jsonl");
    }

    [Fact]
    public void Total_Proves_Cache_Inside_Prompt()
    {
        // prompt 1000 includes 400 cached; total 1100 = 1000 + 100 output proves it.
        var file = WriteChat("proj", "c1",
            """{"type":"assistant","model":"qwen3-coder","sessionId":"s1","id":"m1","timestamp":"2026-09-21T10:00:00Z","usageMetadata":{"promptTokenCount":1000,"candidatesTokenCount":100,"cachedContentTokenCount":400,"totalTokenCount":1100}}""");

        var record = Assert.Single(QwenSessionReader.Records(_home));
        Assert.Equal(600, record.Tally.Input);   // 1000 - 400
        Assert.Equal(400, record.Tally.CacheRead);
        Assert.Equal(100, record.Tally.Output);
        Assert.Equal(0, record.UnclassifiedTokens);
        Assert.Equal("proj", record.Project);
    }

    [Fact]
    public void Total_Proves_Cache_Beside_Prompt()
    {
        // total 1500 = 1000 prompt + 400 cached + 100 output: disjoint.
        var file = WriteChat("proj", "c1",
            """{"type":"assistant","model":"qwen3-coder","sessionId":"s1","id":"m1","timestamp":"2026-09-21T10:00:00Z","usageMetadata":{"promptTokenCount":1000,"candidatesTokenCount":100,"cachedContentTokenCount":400,"totalTokenCount":1500}}""");
        var record = Assert.Single(QwenSessionReader.Records(_home));
        Assert.Equal(1000, record.Tally.Input); // prompt kept whole
        Assert.Equal(400, record.Tally.CacheRead);
    }

    [Fact]
    public void Total_Matching_Neither_Identity_Is_Unclassified()
    {
        // A total that proves nothing is kept whole and names no kind.
        var file = WriteChat("proj", "c1",
            """{"type":"assistant","model":"qwen3-coder","sessionId":"s1","id":"m1","timestamp":"2026-09-21T10:00:00Z","usageMetadata":{"promptTokenCount":1000,"candidatesTokenCount":100,"cachedContentTokenCount":400,"totalTokenCount":12345}}""");
        var record = Assert.Single(QwenSessionReader.Records(_home));
        Assert.Equal(12345, record.UnclassifiedTokens);
        Assert.Equal(0, record.Tally.Total);
    }

    [Fact]
    public void No_Total_Uses_Documented_Semantics()
    {
        // prompt includes cached content per Google's documentation.
        var file = WriteChat("proj", "c1",
            """{"type":"assistant","model":"qwen3-coder","sessionId":"s1","id":"m1","timestamp":"2026-09-21T10:00:00Z","usageMetadata":{"promptTokenCount":1000,"candidatesTokenCount":100,"cachedContentTokenCount":400}}""");
        var record = Assert.Single(QwenSessionReader.Records(_home));
        Assert.Equal(600, record.Tally.Input);
        Assert.Equal(400, record.Tally.CacheRead);
        Assert.Equal(100, record.Tally.Output);
    }

    [Fact]
    public void Usage_Without_Model_Is_Skipped_And_Marks_Run_Partial()
    {
        // Real usage with no model cannot be attributed: the line is skipped
        // and the run's survivors are marked partial. With no survivor there
        // is nothing to emit — an empty ledger is the honest answer.
        WriteChat("proj", "c1",
            """{"type":"assistant","sessionId":"s1","id":"m1","timestamp":"2026-09-21T10:00:00Z","usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":10}}""");
        Assert.Empty(QwenSessionReader.Records(_home));

        // A second, well-formed line does emit; it carries the partial mark.
        WriteChat("proj", "c2",
            """{"type":"assistant","model":"qwen3-coder","sessionId":"s2","id":"m2","timestamp":"2026-09-21T10:05:00Z","usageMetadata":{"promptTokenCount":50,"candidatesTokenCount":5}}""");
        var records = QwenSessionReader.Records(_home);
        Assert.Single(records); // the c1 file contributes nothing
        Assert.Equal("s2", records[0].SessionID);
        Assert.True(records[0].IsPartial); // the run was incomplete
    }

    [Fact]
    public void Thinking_Tokens_Bill_As_Output()
    {
        WriteChat("proj", "c1",
            """{"type":"assistant","model":"qwen3-coder","sessionId":"s1","id":"m1","timestamp":"2026-09-21T10:00:00Z","usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":60,"thoughtsTokenCount":40}}""");
        var record = Assert.Single(QwenSessionReader.Records(_home));
        Assert.Equal(100, record.Tally.Output); // candidates + thoughts, held once
    }
}

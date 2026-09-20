using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Unsloth reader tests over a real temp studio.db: chat normalization (cache
/// bounded by prompt, remainder to input, reasoning folded into output once) and
/// the API path with no cache/reasoning columns.
/// </summary>
public class UnslothReaderTests : IDisposable
{
    private readonly string _dbPath;

    public UnslothReaderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pulse-unsloth-{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE chat_threads (id TEXT PRIMARY KEY, model_id TEXT);
            CREATE TABLE chat_messages (id TEXT PRIMARY KEY, thread_id TEXT, role TEXT, metadata_json TEXT, created_at INTEGER);
            CREATE TABLE api_usage_events (id TEXT PRIMARY KEY, endpoint TEXT, model TEXT,
              prompt_tokens INTEGER, completion_tokens INTEGER, total_tokens INTEGER, created_at INTEGER);
            INSERT INTO chat_threads VALUES ('t1', 'thread-model');
            """;
        schema.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private void InsertMessage(string id, string metadataJson, long createdAt = 1789981200000) =>
        InsertMessage(id, "t1", metadataJson, createdAt);

    private void InsertMessage(string id, string threadId, string metadataJson, long createdAt)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO chat_messages VALUES ($id, $thread, 'assistant', $meta, $created)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$thread", threadId);
        insert.Parameters.AddWithValue("$meta", metadataJson);
        insert.Parameters.AddWithValue("$created", createdAt);
        insert.ExecuteNonQuery();
    }

    [Fact]
    public void Chat_Kinds_Are_Normalized_To_The_Reported_Total()
    {
        // prompt 1000 (includes 300 cached, plus 100 cache write); completion 500
        // with reasoning 150 folded in already; total 1600 = 1000 + 500 + 100.
        InsertMessage("m1", """
            {"contextUsage":{"promptTokens":1000,"completionTokens":500,"cachedTokens":300,
                             "cacheWriteTokens":100,"totalTokens":1600,"reasoningTokens":150},
             "responseDetails":{"responseModelId":"glm-5"}}
            """);

        var records = UnslothReader.Read(_dbPath);
        var record = Assert.Single(records.Where(r => r.SessionID == "t1"));
        Assert.Equal("glm-5", record.Model);
        Assert.Equal(700, record.Tally.Input);    // total 1600 - completion 500 - read 300 - write 100
        Assert.Equal(300, record.Tally.CacheRead); // bounded by prompt
        Assert.Equal(100, record.Tally.CacheWrite);
        Assert.Equal(500, record.Tally.Output);   // reasoning folded once, inside completion
    }

    [Fact]
    public void Api_Path_Has_No_Cache_And_No_Reasoning_Buckets()
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO api_usage_events VALUES ('a1', '/v1/chat', 'z-model', 400, 100, 500, 1789981200000)";
        insert.ExecuteNonQuery();

        var record = Assert.Single(UnslothReader.Read(_dbPath).Where(r => r.SessionID == "unsloth:api"));
        Assert.Equal(400, record.Tally.Input);
        Assert.Equal(100, record.Tally.Output);
        Assert.Equal(0, record.Tally.CacheRead);
        Assert.Equal(0, record.Tally.CacheWrite);
    }

    [Fact]
    public void Row_With_Zero_Timestamp_Is_Skipped()
    {
        InsertMessage("m-bad", """{"contextUsage":{"promptTokens":10,"completionTokens":5,"totalTokens":15}}""", createdAt: 0);
        Assert.Empty(UnslothReader.Read(_dbPath));
    }
}

/// <summary>
/// JCode reader tests: explicit cache-shape markers, the ambiguous case carrying
/// input as unclassified, reasoning left out, and journal replay.
/// </summary>
public class JcodeUsageReaderTests : IDisposable
{
    private readonly string _root;

    public JcodeUsageReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-jcode-{Guid.NewGuid():N}", ".jcode", "sessions");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_root))!, recursive: true); }
        catch (IOException) { }
    }

    private void WriteSession(string stem, string session, string? journal = null)
    {
        File.WriteAllText(Path.Combine(_root, $"{stem}.json"), session);
        if (journal is not null)
            File.WriteAllText(Path.Combine(_root, $"{stem}.journal.jsonl"), journal);
    }

    [Fact]
    public void Anthropic_Marker_Means_Input_Is_Cache_Exclusive()
    {
        WriteSession("session_abc",
            """{"id":"s1","provider_key":"anthropic","model":"m","working_dir":"E:/Code/P","messages":[{"id":"m1","timestamp":"2026-09-21T10:00:00Z","token_usage":{"input_tokens":700,"output_tokens":50,"cache_read_input_tokens":200,"cache_creation_input_tokens":100}}]}""");

        var record = Assert.Single(JcodeUsageReader.RecordsFromRoot(_root));
        Assert.Equal(700, record.Tally.Input);   // input kept whole: schema says exclusive
        Assert.Equal(200, record.Tally.CacheRead);
        Assert.Equal(100, record.Tally.CacheWrite);
        Assert.False(record.IsPartial);
    }

    [Fact]
    public void OpenAi_Details_Marker_Means_Cache_Inside_Input()
    {
        WriteSession("session_abc",
            """{"id":"s1","model":"m","messages":[{"id":"m1","timestamp":"2026-09-21T10:00:00Z","token_usage":{"input_tokens":1000,"output_tokens":50,"cache_read_input_tokens":400,"prompt_tokens_details":{"cached_tokens":400}}}]}""");

        var record = Assert.Single(JcodeUsageReader.RecordsFromRoot(_root));
        Assert.Equal(600, record.Tally.Input);   // 1000 - 400 contained
        Assert.Equal(400, record.Tally.CacheRead);
    }

    [Fact]
    public void Ambiguous_Positive_Cache_Is_Unclassified_And_Partial()
    {
        // A positive cache with neither marker: the input may or may not
        // already contain it, so it cannot be priced as fresh.
        WriteSession("session_abc",
            """{"id":"s1","model":"m","messages":[{"id":"m1","timestamp":"2026-09-21T10:00:00Z","token_usage":{"input_tokens":1000,"output_tokens":50,"cache_read_input_tokens":400}}]}""");

        var record = Assert.Single(JcodeUsageReader.RecordsFromRoot(_root));
        Assert.Equal(0, record.Tally.Input);
        Assert.Equal(1000, record.UnclassifiedTokens);
        Assert.Equal(50, record.Tally.Output);
        Assert.True(record.IsPartial);
    }

    [Fact]
    public void Reasoning_Left_Out_And_Marks_Partial()
    {
        WriteSession("session_abc",
            """{"id":"s1","model":"m","messages":[{"id":"m1","timestamp":"2026-09-21T10:00:00Z","token_usage":{"input_tokens":100,"output_tokens":50,"reasoning_output_tokens":30}}]}""");
        var record = Assert.Single(JcodeUsageReader.RecordsFromRoot(_root));
        Assert.Equal(50, record.Tally.Output);
        Assert.Equal(0, record.UnclassifiedTokens);
        Assert.True(record.IsPartial);
    }

    [Fact]
    public void Journal_Replay_Appends_Messages_With_Current_Meta()
    {
        WriteSession("session_abc",
            """{"id":"s1","model":"old-model","messages":[]}""",
            journal: """{"append_messages":[{"id":"j1","timestamp":"2026-09-21T10:00:00Z","token_usage":{"input_tokens":10,"output_tokens":1}}]}""");
        // Note: meta.model replay updates the model for later appends.
        var record = Assert.Single(JcodeUsageReader.RecordsFromRoot(_root));
        Assert.Equal(10, record.Tally.Input);
    }

    [Fact]
    public void Untimed_Message_Is_Skipped()
    {
        WriteSession("session_abc",
            """{"id":"s1","model":"m","messages":[{"id":"m1","token_usage":{"input_tokens":10,"output_tokens":1}}]}""");
        Assert.Empty(JcodeUsageReader.RecordsFromRoot(_root));
    }
}

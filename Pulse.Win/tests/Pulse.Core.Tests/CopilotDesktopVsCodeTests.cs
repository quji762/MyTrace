using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Copilot desktop reader tests over a real temp SQLite sessions table plus
/// sidecar events.jsonl files. The row is lifetime authority; sidecar shutdown
/// snapshots are differenced per model and budgeted against the row; reasoning
/// is never added to output and marks the run partial.
/// </summary>
public class CopilotDesktopReaderTests : IDisposable
{
    private readonly string _home;

    public CopilotDesktopReaderTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"pulse-copilot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string DbPath => Path.Combine(_home, ".copilot", "data.db");
    private string SidecarRoot => Path.Combine(_home, ".copilot", "session-state");

    private void CreateDatabase(params (string Id, string Title, string Model, long Input, long Output, long Cached, long Reasoning, long CreatedMs)[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE sessions (
              id TEXT PRIMARY KEY, title TEXT, model TEXT,
              total_input_tokens INTEGER, total_output_tokens INTEGER,
              total_cached_tokens INTEGER, total_reasoning_tokens INTEGER, created_at INTEGER);
            """;
        schema.ExecuteNonQuery();
        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions VALUES ($id, $title, $model, $input, $output, $cached, $reasoning, $created)
                """;
            insert.Parameters.AddWithValue("$id", row.Id);
            insert.Parameters.AddWithValue("$title", (object?)row.Title ?? DBNull.Value);
            insert.Parameters.AddWithValue("$model", (object?)row.Model ?? DBNull.Value);
            insert.Parameters.AddWithValue("$input", row.Input);
            insert.Parameters.AddWithValue("$output", row.Output);
            insert.Parameters.AddWithValue("$cached", row.Cached);
            insert.Parameters.AddWithValue("$reasoning", row.Reasoning);
            insert.Parameters.AddWithValue("$created", row.CreatedMs);
            insert.ExecuteNonQuery();
        }
    }

    private void WriteSidecar(string sessionId, params string[] events)
    {
        var dir = Path.Combine(SidecarRoot, sessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "events.jsonl"), events);
    }

    [Fact]
    public void Shutdown_Snapshots_Are_Differenced_And_Budgeted()
    {
        // Row lifetime: input 1000, output 500. Two shutdown runs of 600/300 each
        // exceed the row: each is budgeted, the leftover lands in the remainder.
        CreateDatabase(("s1", "session one", "gpt-5", 1000, 500, 0, 0, 1789981200000));
        WriteSidecar("s1",
            """{"type":"session.start","data":{"context":{"cwd":"E:\\Code\\MyTrace"}},"id":"e0"}""",
            """{"type":"session.shutdown","timestamp":"2026-09-21T10:00:00Z","id":"e1","data":{"modelMetrics":{"gpt-5":{"usage":{"inputTokens":600,"outputTokens":300}}}}}""",
            """{"type":"session.shutdown","timestamp":"2026-09-21T11:00:00Z","id":"e2","data":{"modelMetrics":{"gpt-5":{"usage":{"inputTokens":600,"outputTokens":300}}}}}""");

        var records = CopilotDesktopReader.Records(DbPath, SidecarRoot);
        Assert.Equal(2, records.Count);

        // First run: full 600/300 (no prior snapshot).
        var first = records[0];
        Assert.Equal(600, first.Tally.Input);
        Assert.Equal(300, first.Tally.Output);
        Assert.Equal("copilot-desktop:s1:shutdown:e1:gpt-5", first.DeduplicationID);

        // The second shutdown repeats the same running total: its delta is zero,
        // so nothing is emitted for it. The row's leftover 400/200 (what the
        // runs never explained) lands on the created_at remainder.
        var rowRecord = records[1];
        Assert.EndsWith(":row", rowRecord.DeduplicationID);
        Assert.Equal(400, rowRecord.Tally.Input);
        Assert.Equal(200, rowRecord.Tally.Output);
    }

    [Fact]
    public void Missing_Head_Makes_First_Snapshot_A_Baseline()
    {
        // Without session.start, the earliest snapshot is an unknown baseline,
        // used only to difference later ones; its tokens fall to the remainder.
        CreateDatabase(("s1", null, "gpt-5", 1000, 500, 0, 0, 1789981200000));
        WriteSidecar("s1",
            """{"type":"session.shutdown","timestamp":"2026-09-21T10:00:00Z","id":"e1","data":{"modelMetrics":{"gpt-5":{"usage":{"inputTokens":700,"outputTokens":300}}}}}""",
            """{"type":"session.shutdown","timestamp":"2026-09-21T11:00:00Z","id":"e2","data":{"modelMetrics":{"gpt-5":{"usage":{"inputTokens":900,"outputTokens":400}}}}}""");

        var records = CopilotDesktopReader.Records(DbPath, SidecarRoot);

        // The first snapshot contributed nothing (unknown baseline); the second
        // is the 200/100 difference.
        Assert.DoesNotContain(records, r => r.Tally.Input == 700);
        var second = records.Single(r => r.DeduplicationID!.EndsWith("e2:gpt-5"));
        Assert.Equal(200, second.Tally.Input);
        Assert.Equal(100, second.Tally.Output);
    }

    [Fact]
    public void Reasoning_Is_Never_Added_To_Output_And_Marks_Partial()
    {
        // Neither source states reasoning containment: output stays whole, the
        // ambiguous count is unplaced, and the run is marked partial.
        CreateDatabase(("s1", null, "gpt-5", 100, 50, 0, 0, 1789981200000));
        WriteSidecar("s1",
            """{"type":"session.start","id":"e0"}""",
            """{"type":"session.shutdown","timestamp":"2026-09-21T10:00:00Z","id":"e1","data":{"modelMetrics":{"gpt-5":{"usage":{"inputTokens":100,"outputTokens":50,"reasoningTokens":30}}}}}""");

        var records = CopilotDesktopReader.Records(DbPath, SidecarRoot);
        var record = Assert.Single(records);
        Assert.Equal(50, record.Tally.Output); // not 80
        Assert.True(record.IsPartial);
    }

    [Fact]
    public void Cache_Write_Lives_Only_In_The_Sidecar()
    {
        CreateDatabase(("s1", null, "gpt-5", 100, 50, 0, 0, 1789981200000));
        WriteSidecar("s1",
            """{"type":"session.start","id":"e0"}""",
            """{"type":"session.shutdown","timestamp":"2026-09-21T10:00:00Z","id":"e1","data":{"modelMetrics":{"gpt-5":{"usage":{"inputTokens":100,"outputTokens":50,"cacheWriteTokens":25}}}}}""");

        var record = Assert.Single(CopilotDesktopReader.Records(DbPath, SidecarRoot));
        Assert.Equal(25, record.Tally.CacheWrite); // not budgeted against any row column
    }

    [Fact]
    public void Untouched_Row_Falls_To_The_Created_At_Remainder()
    {
        CreateDatabase(("s1", "t", "gpt-5", 100, 50, 20, 0, 1789981200000));
        var records = CopilotDesktopReader.Records(DbPath, SidecarRoot);

        var remainder = Assert.Single(records);
        Assert.EndsWith(":row", remainder.DeduplicationID);
        Assert.Equal(100, remainder.Tally.Input);
        Assert.Equal(50, remainder.Tally.Output);
        Assert.Equal(20, remainder.Tally.CacheRead);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789981200000), remainder.Timestamp);
    }
}

/// <summary>
/// VS Code reader tests: append/patch log reconstruction, Copilot-origin filter,
/// thinking tokens folded into output, untimed requests skipped, instant
/// collisions suffixed.
/// </summary>
public class CopilotVsCodeReaderTests : IDisposable
{
    private readonly string _storageRoot;

    public CopilotVsCodeReaderTests()
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), $"pulse-vscode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_storageRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Append_Then_Patch_Reconstructs_The_Request_Array()
    {
        var hash = Path.Combine(_storageRoot, "hash1");
        var chat = Path.Combine(hash, "chatSessions");
        Directory.CreateDirectory(chat);
        File.WriteAllText(Path.Combine(hash, "workspace.json"),
            """{"folder":"file:///e%3A/Code/MyTrace"}""");

        File.WriteAllLines(Path.Combine(chat, "u1.jsonl"),
        [
            """{"kind":0,"v":{"requests":[{"modelId":"copilot/gpt-5","timestamp":1789981200000,"promptTokens":100,"completionTokens":10}]}}""",
            """{"kind":1,"k":["requests",0,"result","metadata","resolvedModel"],"v":"gpt-5-mini"}""",
            """{"kind":2,"k":["requests"],"v":[{"modelId":"copilot/gpt-5","timestamp":1789981210000,"promptTokens":50,"completionTokens":5}]}""",
        ]);

        var records = CopilotVsCodeReader.RecordsFromRoot(_storageRoot);
        Assert.Equal(2, records.Count);

        // The patch resolved the first request's model.
        Assert.Equal("gpt-5-mini", records[0].Model);
        Assert.Equal(100, records[0].Tally.Input);
        Assert.Equal("MyTrace", records[0].Project);
    }

    [Fact]
    public void Non_Copilot_Request_Is_Skipped()
    {
        var hash = Path.Combine(_storageRoot, "hash2");
        var chat = Path.Combine(hash, "chatSessions");
        Directory.CreateDirectory(chat);
        File.WriteAllLines(Path.Combine(chat, "u1.jsonl"),
        [
            """{"kind":0,"v":{"requests":[{"modelId":"other-model","timestamp":1789981200000,"promptTokens":100,"completionTokens":10}]}}""",
        ]);
        Assert.Empty(CopilotVsCodeReader.RecordsFromRoot(_storageRoot));
    }

    [Fact]
    public void Untimed_Request_Is_Skipped_Not_Dated_At_Epoch()
    {
        var hash = Path.Combine(_storageRoot, "hash3");
        var chat = Path.Combine(hash, "chatSessions");
        Directory.CreateDirectory(chat);
        File.WriteAllLines(Path.Combine(chat, "u1.jsonl"),
        [
            """{"kind":0,"v":{"requests":[{"modelId":"copilot/gpt-5","promptTokens":100,"completionTokens":10}]}}""",
        ]);
        Assert.Empty(CopilotVsCodeReader.RecordsFromRoot(_storageRoot));
    }

    [Fact]
    public void Two_Requests_At_The_Same_Instant_Are_Both_Counted()
    {
        var hash = Path.Combine(_storageRoot, "hash4");
        var chat = Path.Combine(hash, "chatSessions");
        Directory.CreateDirectory(chat);
        File.WriteAllLines(Path.Combine(chat, "u1.jsonl"),
        [
            """{"kind":0,"v":{"requests":[{"modelId":"copilot/gpt-5","timestamp":1789981200000,"promptTokens":10,"completionTokens":1},{"modelId":"copilot/gpt-5","timestamp":1789981200000,"promptTokens":20,"completionTokens":2}]}}""",
        ]);
        var records = CopilotVsCodeReader.RecordsFromRoot(_storageRoot);
        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].DeduplicationID, records[1].DeduplicationID); // #n suffix
    }

    [Fact]
    public void Thinking_Tokens_Fold_Into_Output()
    {
        var hash = Path.Combine(_storageRoot, "hash5");
        var chat = Path.Combine(hash, "chatSessions");
        Directory.CreateDirectory(chat);
        File.WriteAllLines(Path.Combine(chat, "u1.jsonl"),
        [
            """{"kind":0,"v":{"requests":[{"modelId":"copilot/gpt-5","timestamp":1789981200000,"promptTokens":10,"completionTokens":5,"result":{"metadata":{"toolCallRounds":[{"thinking":{"tokens":7}},{"thinking":{"tokens":3}}]}}}]}}""",
        ]);
        var record = Assert.Single(CopilotVsCodeReader.RecordsFromRoot(_storageRoot));
        Assert.Equal(15, record.Tally.Output); // 5 completion + 10 thinking, folded once
    }
}

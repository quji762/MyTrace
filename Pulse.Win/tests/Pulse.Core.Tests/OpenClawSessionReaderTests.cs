using System.Text.Json;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// OpenClaw reader tests: the cross-store dedup identity (event id + timestamp +
/// counts), the camelCase usage object with bare-total unclassified, artifact
/// row skipping, model bookkeeping carried across lines, and the JSONL path
/// filter (zst and codex mirrors excluded).
/// </summary>
public class OpenClawSessionReaderTests : IDisposable
{
    private readonly string _root;

    public OpenClawSessionReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-openclaw-{Guid.NewGuid():N}", ".openclaw");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string AssistantEvent(string id, long timestampMs, int input, int output, string model = "m")
    {
        return string.Concat(
            "{\"type\":\"message\",\"id\":\"", id,
            "\",\"timestamp\":", timestampMs.ToString(),
            ",\"message\":{\"role\":\"assistant\",\"model\":\"", model,
            "\",\"timestamp\":", timestampMs.ToString(),
            ",\"usage\":{\"input\":", input.ToString(),
            ",\"output\":", output.ToString(), "}}}");
    }

    [Fact]
    public void Sqlite_Store_Emits_Records_With_Bookkeeping_Model()
    {
        var agentDir = Path.Combine(_root, "agent1", "agent");
        Directory.CreateDirectory(agentDir);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                   new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                   {
                       DataSource = Path.Combine(agentDir, "openclaw-agent.sqlite"),
                       Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                   }.ToString()))
        {
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE session_windows (session_id TEXT, model_provider TEXT, model TEXT);
                CREATE TABLE transcript_events (session_id TEXT, seq INTEGER, event_json TEXT);
                INSERT INTO session_windows VALUES ('sess1', 'anthropic', NULL);
                INSERT INTO transcript_events VALUES ('sess1', 0, '{"type":"model_change","modelId":"claude-4","provider":"anthropic"}');
                INSERT INTO transcript_events VALUES ('sess1', 1, '{"type":"message","id":"e1","timestamp":1789981200000,"message":{"role":"assistant","timestamp":1789981200000,"usage":{"input":100,"output":20}}}');
                """;
            schema.ExecuteNonQuery();
        }

        var records = OpenClawSessionReader.RecordsFromRoots(new[] { _root });
        var record = Assert.Single(records);
        // The model_change bookkeeping named the model; the event itself has none.
        Assert.Equal("claude-4", record.Model);
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(20, record.Tally.Output);
        Assert.StartsWith("openclaw:e1:1789981200000:100:20", record.DeduplicationID);
    }

    [Fact]
    public void Jsonl_Store_Reads_And_Registry_Names_The_Session()
    {
        var sessionsDir = Path.Combine(_root, "legacy", "sessions");
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllText(Path.Combine(sessionsDir, "sessions.json"),
            """{"s1":{"sessionId":"real-id","sessionFile":"ev.jsonl"}}""");
        File.WriteAllLines(Path.Combine(sessionsDir, "ev.jsonl"),
        [
            """{"type":"message","id":"e1","timestamp":1789981200000,"message":{"role":"assistant","model":"m","timestamp":1789981200000,"usage":{"input":50,"output":5}}}""",
        ]);

        var record = Assert.Single(OpenClawSessionReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal("real-id", record.SessionID); // the registry names it
    }

    [Fact]
    public void Cross_Store_Duplicate_Folds_Via_Dedup_Identity()
    {
        // The same call in both stores shares event id, timestamp and counts:
        // the builder's global dedup folds them; the reader itself emits both.
        var agentDir = Path.Combine(_root, "agent1", "agent");
        Directory.CreateDirectory(agentDir);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                   new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                   {
                       DataSource = Path.Combine(agentDir, "openclaw-agent.sqlite"),
                       Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                   }.ToString()))
        {
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE transcript_events (session_id TEXT, seq INTEGER, event_json TEXT);
                INSERT INTO transcript_events VALUES ('sess1', 0, '{"type":"message","id":"e1","timestamp":1789981200000,"message":{"role":"assistant","model":"m","timestamp":1789981200000,"usage":{"input":50,"output":5}}}');
                """;
            schema.ExecuteNonQuery();
        }
        var sessionsDir = Path.Combine(_root, "legacy", "sessions");
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllLines(Path.Combine(sessionsDir, "ev.jsonl"),
        [
            """{"type":"message","id":"e1","timestamp":1789981200000,"message":{"role":"assistant","model":"m","timestamp":1789981200000,"usage":{"input":50,"output":5}}}""",
        ]);

        var records = OpenClawSessionReader.RecordsFromRoots(new[] { _root });
        Assert.Equal(2, records.Count); // the reader emits both halves...
        Assert.Equal(records[0].DeduplicationID, records[1].DeduplicationID); // ...which fold to one by identity
    }

    [Fact]
    public void Bare_Total_With_No_Kind_Is_Unclassified()
    {
        var sessionsDir = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllLines(Path.Combine(sessionsDir, "ev.jsonl"),
        [
            """{"type":"message","id":"e1","timestamp":1789981200000,"message":{"role":"assistant","model":"m","timestamp":1789981200000,"usage":{"totalTokens":999}}}""",
        ]);
        var record = Assert.Single(OpenClawSessionReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(999, record.UnclassifiedTokens);
        Assert.Equal(0, record.Tally.Total);
    }

    [Fact]
    public void Artifact_Rows_Are_Skipped()
    {
        var sessionsDir = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllLines(Path.Combine(sessionsDir, "ev.jsonl"),
        [
            """{"type":"message","id":"m1","api":"openclaw-transcript","message":{"role":"assistant","model":"m","usage":{"input":10,"output":1}}}""",
            """{"type":"message","id":"m2","message":{"role":"assistant","provider":"openclaw","model":"delivery-mirror","usage":{"input":10,"output":1}}}""",
        ]);
        Assert.Empty(OpenClawSessionReader.RecordsFromRoots(new[] { _root }));
    }

    [Fact]
    public void Zst_And_Codex_Mirror_Files_Are_Excluded()
    {
        Assert.False(OpenClawSessionReader.IsOpenClawJsonl("/r/sessions/ev.jsonl.zst"));
        Assert.False(OpenClawSessionReader.IsOpenClawJsonl("/r/agent/codex-home/sessions/ev.jsonl"));
        Assert.True(OpenClawSessionReader.IsOpenClawJsonl("/r/sessions/ev.jsonl"));
        Assert.True(OpenClawSessionReader.IsOpenClawJsonl("/r/session-sqlite-import-archive/ev.jsonl"));
    }

    [Fact]
    public void Reasoning_Stands_In_Only_When_Output_Absent()
    {
        var sessionsDir = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllLines(Path.Combine(sessionsDir, "ev.jsonl"),
        [
            """{"type":"message","id":"e1","timestamp":1789981200000,"message":{"role":"assistant","model":"m","timestamp":1789981200000,"usage":{"input":10,"reasoningTokens":7}}}""",
        ]);
        var record = Assert.Single(OpenClawSessionReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(7, record.Tally.Output); // stood in
        Assert.Equal(10, record.Tally.Input);
    }
}

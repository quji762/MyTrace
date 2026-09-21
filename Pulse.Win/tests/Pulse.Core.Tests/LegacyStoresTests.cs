using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// The three pre-catalogue stores: Devin's CLI transcript database (assistant
/// metrics, not the quota route's plan), Grok Build's turn_completed updates
/// (modelUsage names the model), and Kimi's wire log (input_other is fresh
/// input, the cache beside it, and no model anywhere — counted, never costed).
/// </summary>
public class LegacyStoresTests : IDisposable
{
    private readonly string _home;

    public LegacyStoresTests() =>
        _home = Path.Combine(Path.GetTempPath(), $"pulse-legacy-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    // --- Devin CLI ----------------------------------------------------------

    private string DevinDb()
    {
        var directory = Path.Combine(_home, ".local", "share", "devin", "cli");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "sessions.db");
    }

    private void SeedDevin(string dbPath, params (string Session, string Message, long CreatedAt)[] rows)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE sessions (id TEXT PRIMARY KEY, working_directory TEXT, title TEXT);
            CREATE TABLE message_nodes (session_id TEXT, chat_message TEXT, created_at INTEGER);
            """;
        schema.ExecuteNonQuery();
        using var seed = connection.CreateCommand();
        seed.CommandText = "INSERT INTO sessions VALUES ('s1', 'E:/Code/Proj', 'Fix the login flow')";
        seed.ExecuteNonQuery();
        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO message_nodes VALUES ($s, $m, $c)";
            insert.Parameters.AddWithValue("$s", row.Session);
            insert.Parameters.AddWithValue("$m", row.Message);
            insert.Parameters.AddWithValue("$c", row.CreatedAt);
            insert.ExecuteNonQuery();
        }
    }

    private const string DevinAssistant = """
        {"role":"assistant","metadata":{"generation_model":"gpt-5.6-sol","created_at":1789981200,"metrics":{"input_tokens":11486,"output_tokens":254,"cache_read_tokens":6450,"cache_creation_tokens":null}}}
        """;

    [Fact]
    public void Devin_Assistant_Metrics_Are_Read_With_Session_Meta()
    {
        SeedDevin(DevinDb(), ("s1", DevinAssistant, 1789981200));

        var record = Assert.Single(LegacyStores.DevinCliRecords(_home));
        Assert.Equal(11486, record.Tally.Input);
        Assert.Equal(254, record.Tally.Output);
        Assert.Equal(6450, record.Tally.CacheRead);
        Assert.Equal(0, record.Tally.CacheWrite); // null is an absent figure
        Assert.Equal("gpt-5.6-sol", record.Model);
        Assert.Equal("s1", record.SessionID);
        Assert.Equal("Fix the login flow", record.Title);
        Assert.Equal("Proj", record.Project);
    }

    [Fact]
    public void Devin_Metadata_Rows_And_Empty_Metrics_Are_Not_Usage()
    {
        SeedDevin(DevinDb(),
            ("s1", """{"role":"user","content":"hello"}""", 1789981200),
            ("s1", """{"role":"assistant","metadata":{"metrics":{}}}""", 1789981201),
            ("s1", DevinAssistant, 0)); // undated
        Assert.Empty(LegacyStores.DevinCliRecords(_home));
    }

    [Fact]
    public void Devin_Missing_Model_Falls_Back_To_Devin()
    {
        SeedDevin(DevinDb(), ("s1", """{"role":"assistant","metadata":{"metrics":{"input_tokens":10,"output_tokens":1}}}""", 1789981200));
        var record = Assert.Single(LegacyStores.DevinCliRecords(_home));
        Assert.Equal("devin", record.Model);
    }

    // --- Grok Build -----------------------------------------------------------

    private string GrokRun()
    {
        // <encoded working directory>/<run>/updates.jsonl
        var encoded = Uri.EscapeDataString("E:/Code/Proj").Replace("/", "%2F");
        var run = Path.Combine(_home, ".grok", "sessions", encoded, "2026-09-21-run1");
        Directory.CreateDirectory(run);
        return Path.Combine(run, "updates.jsonl");
    }

    private const string GrokTurnCompleted = """
        {"timestamp":1789981200,"params":{"update":{"sessionUpdate":"turn_completed","usage":{"inputTokens":33379,"outputTokens":91,"cachedReadTokens":22272,"cacheCreationTokens":0,"reasoningTokens":42,"modelUsage":{"grok-4.6-build":{"inputTokens":33000,"outputTokens":80,"cachedReadTokens":22000,"cacheCreationTokens":0,"reasoningTokens":40}}}}}}
        """;

    [Fact]
    public void Grok_ModelUsage_Names_The_Model_And_Flat_Totals_Are_The_Fallback()
    {
        File.WriteAllLines(GrokRun(), GrokTurnCompleted.Split('\n'));

        var record = Assert.Single(LegacyStores.GrokRecords(_home));
        // Per-model counts, not the flat totals beside them.
        Assert.Equal(33000, record.Tally.Input);
        Assert.Equal(22000, record.Tally.CacheRead);
        // Reasoning folds into output.
        Assert.Equal(80 + 40, record.Tally.Output);
        Assert.Equal("grok-4.6-build", record.Model);
        Assert.Equal("2026-09-21-run1", record.SessionID);
        Assert.Equal("Proj", record.Project); // percent-decoded folder
    }

    [Fact]
    public void Grok_Without_ModelUsage_Uses_The_Flat_Totals()
    {
        var path = GrokRun();
        File.WriteAllLines(path, """
            {"timestamp":1789981200,"params":{"update":{"sessionUpdate":"user_message_chunk","content":{"text":"Fix the login flow"}}}}
            {"timestamp":1789981210,"params":{"update":{"sessionUpdate":"turn_completed","usage":{"inputTokens":100,"outputTokens":10,"reasoningTokens":5,"cachedReadTokens":20,"cacheCreationTokens":0}}}}
            """.Split('\n'));

        var record = Assert.Single(LegacyStores.GrokRecords(_home));
        Assert.Equal("grok", record.Model);
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(15, record.Tally.Output); // output + reasoning
        Assert.Equal("Fix the login flow", record.Title); // opening prompt
    }

    [Fact]
    public void Grok_Non_Turn_Events_Are_Skipped()
    {
        var path = GrokRun();
        File.WriteAllLines(path, """
            {"timestamp":1789981200,"params":{"update":{"sessionUpdate":"user_message_chunk","content":"hello"}}}
            {"timestamp":1789981210,"params":{"update":{"sessionUpdate":"agent_message_chunk","content":{"text":"partial"}}}}
            """.Split('\n'));
        Assert.Empty(LegacyStores.GrokRecords(_home));
    }

    // --- Kimi CLI -------------------------------------------------------------

    private string KimiWire()
    {
        var directory = Path.Combine(_home, ".kimi", "sessions", "sess-abc");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "wire.jsonl");
    }

    [Fact]
    public void Kimi_InputOther_Is_Fresh_Input_With_The_Cache_Beside_It()
    {
        var wire = KimiWire();
        File.WriteAllLines(wire, """
            {"timestamp":1789981200,"message":{"payload":{"token_usage":{"input_other":4340,"output":38,"input_cache_read":9216,"input_cache_creation":7}}}}
            """.Split('\n'));

        var record = Assert.Single(LegacyStores.KimiRecords(_home));
        Assert.Equal(4340, record.Tally.Input);
        Assert.Equal(9216, record.Tally.CacheRead);
        Assert.Equal(7, record.Tally.CacheWrite);
        Assert.Equal(38, record.Tally.Output);
        // No model anywhere: counted, never costed.
        Assert.Equal("kimi (unnamed)", record.Model);
        Assert.Equal("sess-abc", record.SessionID);
    }

    [Fact]
    public void Kimi_Title_Comes_From_The_Beside_Wire_State_File()
    {
        var wire = KimiWire();
        File.WriteAllLines(wire, """
            {"timestamp":1789981200,"message":{"payload":{"token_usage":{"input_other":10,"output":1}}}}
            """.Split('\n'));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(wire)!, "state.json"),
            """{"custom_title":"Refactor the parser"}""");

        var record = Assert.Single(LegacyStores.KimiRecords(_home));
        Assert.Equal("Refactor the parser", record.Title);
    }

    [Fact]
    public void Kimi_Undated_Or_Empty_Rows_Skip()
    {
        var wire = KimiWire();
        File.WriteAllLines(wire, """
            {"message":{"payload":{"token_usage":{"input_other":10,"output":1}}}}
            {"timestamp":0,"message":{"payload":{"token_usage":{"input_other":10,"output":1}}}}
            {"timestamp":1789981200,"message":{"payload":{"token_usage":{"input_other":0,"output":0}}}}
            """.Split('\n'));
        Assert.Empty(LegacyStores.KimiRecords(_home));
    }
}

using Microsoft.Data.Sqlite;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Hermes reader tests: the two-pass coverage (per-model table wins, session
/// row only for uncovered sessions), the input/cache ambiguity (input carried
/// as unclassified with cache present, never summed), reasoning left out, and
/// the null-provider distinct key.
/// </summary>
public class HermesReaderTests : IDisposable
{
    private readonly string _dbPath;

    public HermesReaderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pulse-hermes-{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE sessions (id TEXT PRIMARY KEY, model TEXT, started_at INTEGER,
              input_tokens INTEGER, output_tokens INTEGER, cache_read_tokens INTEGER,
              cache_write_tokens INTEGER, reasoning_tokens INTEGER);
            CREATE TABLE session_model_usage (session_id TEXT, model TEXT, billing_provider TEXT,
              input_tokens INTEGER, output_tokens INTEGER, cache_read_tokens INTEGER,
              cache_write_tokens INTEGER, reasoning_tokens INTEGER);
            """;
        schema.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private void Exec(string sql, params (string, object)[] parameters)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Session_Covered_By_PerModel_Table_Is_Not_Re_Emitted()
    {
        Exec("INSERT INTO sessions VALUES ('s1','m',1789980000,1000,200,0,0,0)");
        Exec("INSERT INTO session_model_usage VALUES ('s1','claude-4','anthropic',600,100,50,50,0)");
        Exec("INSERT INTO session_model_usage VALUES ('s1','gpt-5','openai',400,100,20,30,0)");

        var records = HermesReader.Read(_dbPath);
        Assert.Equal(2, records.Count); // two per-model groups; session row NOT re-emitted
        // Each group has cache counts, so input goes to unclassified and the
        // record is output-only + partial: 600/400 unplaced inputs each.
        Assert.Equal(700, records[0].UnclassifiedTokens + records[0].Tally.Output); // out 100 + input 600
        Assert.Equal(500, records[1].UnclassifiedTokens + records[1].Tally.Output); // out 100 + input 400
        Assert.All(records, r => Assert.True(r.IsPartial));
    }

    [Fact]
    public void Uncovered_Session_Emits_From_Its_Own_Row()
    {
        Exec("INSERT INTO sessions VALUES ('s1','m',1789980000,400,100,0,0,0)");
        var record = Assert.Single(HermesReader.Read(_dbPath));
        Assert.Equal(400, record.Tally.Input);
        Assert.Equal(100, record.Tally.Output);
        Assert.True(record.IsAggregate);
        Assert.Equal("s1", record.DeduplicationID);
    }

    [Fact]
    public void Cache_In_Input_Is_Never_Summed()
    {
        // The schema does not establish whether input_tokens holds the cache:
        // summing would assume a disjointness nobody proved. The reported input
        // goes to unclassified, the cache is not added, the record is partial.
        Exec("INSERT INTO sessions VALUES ('s1','m',1789980000,1000,200,300,100,0)");
        var record = Assert.Single(HermesReader.Read(_dbPath));
        Assert.Equal(0, record.Tally.Input);
        Assert.Equal(200, record.Tally.Output);
        Assert.Equal(1000, record.UnclassifiedTokens); // cacheRead/cacheWrite dropped
        Assert.True(record.IsPartial);
    }

    [Fact]
    public void Reasoning_Is_Not_Added_To_Output()
    {
        Exec("INSERT INTO sessions VALUES ('s1','m',1789980000,100,50,0,0,30)");
        var record = Assert.Single(HermesReader.Read(_dbPath));
        Assert.Equal(50, record.Tally.Output); // not 80
        Assert.True(record.IsPartial);         // reasoning unplaced: known subset
    }

    [Fact]
    public void Null_Provider_Is_A_Distinct_Key_From_Empty()
    {
        // NULL and '' must not collide, or two provider groups would be one.
        Exec("INSERT INTO sessions VALUES ('s1','m',1789980000,0,0,0,0,0)");
        Exec("INSERT INTO session_model_usage VALUES ('s1','m',NULL,100,10,0,0,0)");
        Exec("INSERT INTO session_model_usage VALUES ('s1','m','',200,20,0,0,0)");
        var records = HermesReader.Read(_dbPath);
        Assert.Equal(2, records.Count);
        Assert.Equal(100, records[0].Tally.Input);
        Assert.Equal(200, records[1].Tally.Input);
    }

    [Fact]
    public void No_Start_Or_Model_Row_Is_Skipped()
    {
        // Neither timestamp nor model is fabricated.
        Exec("INSERT INTO sessions VALUES ('s1',NULL,0,100,10,0,0,0)");
        Assert.Empty(HermesReader.Read(_dbPath));
    }
}

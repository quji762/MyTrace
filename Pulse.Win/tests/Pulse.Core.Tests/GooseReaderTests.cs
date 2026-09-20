using Microsoft.Data.Sqlite;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Goose reader tests over a real temporary SQLite database with the schema
/// documented upstream. Accumulated columns preferred; the unattributed
/// remainder stays unclassified, never reasoning; missing created_at skips.
/// </summary>
public class GooseReaderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public GooseReaderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pulse-goose-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE sessions (
              id TEXT PRIMARY KEY, model_config_json TEXT, provider_name TEXT, created_at TEXT,
              total_tokens INTEGER, input_tokens INTEGER, output_tokens INTEGER,
              accumulated_total_tokens INTEGER, accumulated_input_tokens INTEGER, accumulated_output_tokens INTEGER);
            """;
        schema.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private void Insert(string id, string modelConfig, string createdAt, long? total, long? input, long? output, long? accTotal, long? accInput, long? accOutput)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO sessions (id, model_config_json, created_at, total_tokens, input_tokens, output_tokens,
                                  accumulated_total_tokens, accumulated_input_tokens, accumulated_output_tokens)
            VALUES ($id, $config, $created, $total, $input, $output, $accTotal, $accInput, $accOutput)
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$config", modelConfig);
        insert.Parameters.AddWithValue("$created", createdAt);
        insert.Parameters.AddWithValue("$total", (object?)total ?? DBNull.Value);
        insert.Parameters.AddWithValue("$input", (object?)input ?? DBNull.Value);
        insert.Parameters.AddWithValue("$output", (object?)output ?? DBNull.Value);
        insert.Parameters.AddWithValue("$accTotal", (object?)accTotal ?? DBNull.Value);
        insert.Parameters.AddWithValue("$accInput", (object?)accInput ?? DBNull.Value);
        insert.Parameters.AddWithValue("$accOutput", (object?)accOutput ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    [Fact]
    public void Accumulated_Columns_Are_Preferred_Over_Plain()
    {
        Insert("s1", """{"model_name":"gpt-5.3"}""", "2026-09-21 10:00:00",
            total: 100, input: 60, output: 40,      // stale plain figures
            accTotal: 5000, accInput: 4000, accOutput: 1000);

        var record = Assert.Single(GooseReader.Read(_dbPath));
        Assert.Equal(4000, record.Tally.Input);
        Assert.Equal(1000, record.Tally.Output);
        Assert.Equal("s1", record.DeduplicationID);
        Assert.True(record.IsAggregate); // one record per session, not a turn
    }

    [Fact]
    public void Plain_Columns_Are_The_Fallback()
    {
        Insert("s1", """{"model_name":"gpt-5.3"}""", "2026-09-21 10:00:00",
            total: 150, input: 100, output: 50, accTotal: null, accInput: null, accOutput: null);

        var record = Assert.Single(GooseReader.Read(_dbPath));
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(50, record.Tally.Output);
    }

    [Fact]
    public void Unattributed_Remainder_Is_Unclassified_Never_Reasoning()
    {
        // total 1000 over input 400 + output 300: the 300 remainder is kept as
        // unclassified — counted, never priced, never shown as a kind.
        Insert("s1", """{"model_name":"m"}""", "2026-09-21 10:00:00",
            total: 1000, input: 400, output: 300, accTotal: null, accInput: null, accOutput: null);

        var record = Assert.Single(GooseReader.Read(_dbPath));
        Assert.Equal(300, record.UnclassifiedTokens);
        Assert.Equal(700, record.KnownTotal);
        Assert.Equal(1000, record.KnownTotal + record.UnclassifiedTokens);
    }

    [Fact]
    public void Unreadable_Created_At_Skips_The_Session()
    {
        Insert("bad", """{"model_name":"m"}""", "not a date", total: 10, input: 5, output: 5, accTotal: null, accInput: null, accOutput: null);
        Assert.Empty(GooseReader.Read(_dbPath));
    }

    [Fact]
    public void Blank_Or_Non_Object_Config_Skips_The_Session()
    {
        Insert("null-config", "NULL", "2026-09-21 10:00:00", total: 10, input: 5, output: 5, accTotal: null, accInput: null, accOutput: null);
        Insert("array-config", "[]", "2026-09-21 10:00:00", total: 10, input: 5, output: 5, accTotal: null, accInput: null, accOutput: null);
        Insert("blank-name", """{"model_name":"  "}""", "2026-09-21 10:00:00", total: 10, input: 5, output: 5, accTotal: null, accInput: null, accOutput: null);
        Assert.Empty(GooseReader.Read(_dbPath));
    }

    [Fact]
    public void Both_Stamps_And_Environment_Root_Resolve()
    {
        Insert("s1", """{"model_name":"m"}""", "2026-09-21 10:00:00",
            total: 100, input: 60, output: 40, accTotal: null, accInput: null, accOutput: null);

        Assert.Equal(DateTimeOffset.Parse("2026-09-21T10:00:00Z"), GooseReader.Utc("2026-09-21 10:00:00"));
        Assert.Equal(DateTimeOffset.Parse("2026-09-21T00:00:00Z"), GooseReader.Utc("2026-09-21"));
        Assert.Null(GooseReader.Utc(""));

        var root = Path.Combine(Path.GetTempPath(), $"pulse-goose-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "data", "sessions"));
        SqliteConnection.ClearAllPools();
        File.Copy(_dbPath, Path.Combine(root, "data", "sessions", "sessions.db"));
        try
        {
            var candidates = GooseReader.CandidateDatabases(
                userProfile: Path.Combine(_root_home(), "x"),
                environment: new Dictionary<string, string?> { ["GOOSE_PATH_ROOT"] = root });
            Assert.Equal(Path.Combine(root, "data", "sessions", "sessions.db"), candidates[0]);
            Assert.Single(GooseReader.Records(
                userProfile: Path.Combine(_root_home(), "x"),
                environment: new Dictionary<string, string?> { ["GOOSE_PATH_ROOT"] = root }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string _root_home() => Path.GetTempPath();

    [Fact]
    public void First_Existing_Candidate_Wins()
    {
        Insert("s1", """{"model_name":"m"}""", "2026-09-21 10:00:00",
            total: 100, input: 60, output: 40, accTotal: null, accInput: null, accOutput: null);

        var home = Path.Combine(Path.GetTempPath(), $"pulse-goose-home-{Guid.NewGuid():N}");
        var existing = Path.Combine(home, ".local", "share", "Block", "goose", "sessions");
        Directory.CreateDirectory(existing);
        SqliteConnection.ClearAllPools();
        File.Copy(_dbPath, Path.Combine(existing, "sessions.db"));
        try
        {
            var copied = Path.Combine(existing, "sessions.db");
            Assert.True(File.Exists(copied), "copy should exist");
            Assert.True(File.ReadAllBytes(copied).Length > 0, "copy should not be empty");
            Assert.Single(GooseReader.Read(copied)); // reading works directly

            var candidates = GooseReader.CandidateDatabases(userProfile: home, appDataRoaming: Path.Combine(home, "roaming"), appDataLocal: Path.Combine(home, "local"));
            var found = candidates.FirstOrDefault(File.Exists);
            Assert.NotNull(found);

            var records = GooseReader.Records(userProfile: home, appDataRoaming: Path.Combine(home, "roaming"), appDataLocal: Path.Combine(home, "local"));
            Assert.Single(records);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}

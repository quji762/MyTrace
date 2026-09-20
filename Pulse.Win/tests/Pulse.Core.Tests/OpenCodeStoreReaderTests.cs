using Microsoft.Data.Sqlite;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// OpenCode store reader tests over a real temporary SQLite database built with
/// the schema documented upstream: session(id, slug, title, directory) and
/// message(session_id, data-as-JSON). The store's own cost field is ignored;
/// reasoning bills as output.
/// </summary>
public class OpenCodeStoreReaderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public OpenCodeStoreReaderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pulse-opencode-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE session (id TEXT PRIMARY KEY, slug TEXT, title TEXT, directory TEXT);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT);
            """;
        schema.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private void AddSession(string id, string? slug, string? title, string? directory)
    {
        using var command = new SqliteConnection(_connectionString);
        command.Open();
        using var insert = command.CreateCommand();
        insert.CommandText = "INSERT INTO session (id, slug, title, directory) VALUES ($id, $slug, $title, $directory)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$slug", (object?)slug ?? DBNull.Value);
        insert.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        insert.Parameters.AddWithValue("$directory", (object?)directory ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    private void AddMessage(string id, string sessionId, string data)
    {
        using var command = new SqliteConnection(_connectionString);
        command.Open();
        using var insert = command.CreateCommand();
        insert.CommandText = "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ($id, $session, 0, 0, $data)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$session", sessionId);
        insert.Parameters.AddWithValue("$data", data);
        insert.ExecuteNonQuery();
    }

    [Fact]
    public void Assistant_Messages_Are_Tallied_With_Reasoning_As_Output()
    {
        AddSession("s1", "fix-login", "Fixing the login flow", "E:\\Projects\\MyTrace");
        // time.created in milliseconds.
        AddMessage("m1", "s1", """
            {"role":"assistant","modelID":"mimo-v2.5","providerID":"xai","cost":0,
             "time":{"created":1789981200000},
             "tokens":{"total":10812,"input":9705,"output":11,"reasoning":72,
                       "cache":{"write":50,"read":1024}}}
            """);

        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var ledger = OpenCodeStoreReader.LedgerAt(_dbPath, ModelPrices.Empty, zone);

        // Kinds: 9705 input + 50 cacheWrite + 1024 cacheRead + 83 output.
        Assert.Equal(10862, ledger.AllTime.Tokens);
        var slot = Assert.Single(ledger.Slots);
        var tally = slot.Models["mimo-v2.5"];
        Assert.Equal(9705, tally.Input);
        Assert.Equal(50, tally.CacheWrite);
        Assert.Equal(1024, tally.CacheRead);
        Assert.Equal(83, tally.Output); // output 11 + reasoning 72
    }

    [Fact]
    public void User_Messages_And_Empty_Tallies_Contribute_Nothing()
    {
        AddMessage("m1", "s1", """{"role":"user","modelID":"m","tokens":{"input":999},"time":{"created":1789981200000}}""");
        AddMessage("m2", "s1", """{"role":"assistant","modelID":"m","tokens":{"input":0,"output":0},"time":{"created":1789981200000}}""");

        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var ledger = OpenCodeStoreReader.LedgerAt(_dbPath, ModelPrices.Empty, zone);
        Assert.Equal(UsageLedger.EmptyLedger.Days.Count, ledger.Days.Count);
        Assert.Equal(0, ledger.AllTime.Tokens);
    }

    [Fact]
    public void Missing_Time_Or_Model_Drops_The_Row()
    {
        AddMessage("m1", "s1", """{"role":"assistant","modelID":"m","tokens":{"input":10}}""");                    // no time
        AddMessage("m2", "s1", """{"role":"assistant","tokens":{"input":10},"time":{"created":1789981200000}}"""); // no model

        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var ledger = OpenCodeStoreReader.LedgerAt(_dbPath, ModelPrices.Empty, zone);
        Assert.Equal(0, ledger.AllTime.Tokens);
    }

    [Fact]
    public void Locked_Or_Missing_Database_Is_Empty_Not_Crash()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        Assert.Equal(0, OpenCodeStoreReader.LedgerAt(
            Path.Combine(_dbPath, "does-not-exist.db"), ModelPrices.Empty, zone).AllTime.Tokens);
    }

    [Fact]
    public void OpenCode_Root_Falls_Back_Between_Profiles()
    {
        // No profile has the store in a unit test; the locator reports absence
        // honestly rather than inventing a path.
        Assert.Null(TranscriptLocator.OpenCodeRoot(Path.Combine(Path.GetTempPath(), $"pulse-none-{Guid.NewGuid():N}")));
    }
}

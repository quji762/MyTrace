using Microsoft.Data.Sqlite;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Micode reader tests over a real temp SQLite database in the OpenCode shape:
/// reasoning billed as output, the store's own cost ignored, embedded ids not
/// namespaced, seconds-or-milliseconds epochs tolerated.
/// </summary>
public class MicodeReaderTests : IDisposable
{
    private readonly string _dbPath;

    public MicodeReaderTests()
    {
        // The file name must match the reader's mimocode*.db filter.
        _dbPath = Path.Combine(Path.GetTempPath(), $"mimocode-{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE message (id TEXT, session_id TEXT, data TEXT);
            CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT);
            """;
        schema.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private void InsertMessage(string rowId, string sessionId, string data) =>
        InsertMessage(rowId, sessionId, data, data);

    private void InsertMessage(string rowId, string sessionId, string data, string? secondCopyData)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO message VALUES ($id, $session, $data)";
        insert.Parameters.AddWithValue("$id", rowId);
        insert.Parameters.AddWithValue("$session", sessionId);
        insert.Parameters.AddWithValue("$data", data);
        insert.ExecuteNonQuery();
        if (secondCopyData is { } copy && !ReferenceEquals(data, copy))
        {
            using var second = connection.CreateCommand();
            second.CommandText = "INSERT INTO message VALUES ($id2, $session2, $data2)";
            second.Parameters.AddWithValue("$id2", rowId + "-2");
            second.Parameters.AddWithValue("$session2", sessionId);
            second.Parameters.AddWithValue("$data2", copy);
            second.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Reasoning_Is_Billed_As_Output()
    {
        InsertMessage("r1", "s1",
            """{"id":"embedded-1","role":"assistant","modelID":"mimo-v2.5","tokens":{"input":9705,"output":11,"reasoning":72,"cache":{"read":1024,"write":50}},"time":{"created":1789981200000}}""");

        var record = Assert.Single(MicodeReader.RecordsFromRoots(new[] { Path.GetDirectoryName(_dbPath)! }));
        Assert.Equal(9705, record.Tally.Input);
        Assert.Equal(83, record.Tally.Output); // output 11 + reasoning 72
        Assert.Equal(1024, record.Tally.CacheRead);
        Assert.Equal(50, record.Tally.CacheWrite);
        Assert.Equal("mimo-v2.5", record.Model);
        // The embedded id is the product's own identity, NOT namespaced.
        Assert.Equal("embedded-1", record.DeduplicationID);
    }

    [Fact]
    public void User_Rows_And_Zero_Tallies_Contribute_Nothing()
    {
        InsertMessage("r1", "s1", """{"role":"user","modelID":"m","tokens":{"input":9},"time":{"created":1789981200000}}""");
        InsertMessage("r2", "s1", """{"role":"assistant","modelID":"m","tokens":{"input":0},"time":{"created":1789981200000}}""");
        Assert.Empty(MicodeReader.RecordsFromRoots(new[] { Path.GetDirectoryName(_dbPath)! }));
    }

    [Fact]
    public void No_Time_Or_No_Model_Drops_The_Row()
    {
        InsertMessage("r1", "s1", """{"role":"assistant","modelID":"m","tokens":{"input":10}}""");
        InsertMessage("r2", "s1", """{"role":"assistant","tokens":{"input":10},"time":{"created":1789981200000}}""");
        Assert.Empty(MicodeReader.RecordsFromRoots(new[] { Path.GetDirectoryName(_dbPath)! }));
    }

    [Fact]
    public void Seconds_Epoch_Is_Tolerated()
    {
        InsertMessage("r1", "s1",
            """{"role":"assistant","modelID":"m","tokens":{"input":10,"output":1},"time":{"created":1789981200}}""");
        var record = Assert.Single(MicodeReader.RecordsFromRoots(new[] { Path.GetDirectoryName(_dbPath)! }));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789981200), record.Timestamp);
    }

    [Fact]
    public void Non_Assistant_Rows_Skip()
    {
        InsertMessage("r1", "s1", """{"role":"user","modelID":"m","tokens":{"input":10,"output":1},"time":{"created":1789981200000}}""");
        Assert.Empty(MicodeReader.RecordsFromRoots(new[] { Path.GetDirectoryName(_dbPath)! }));
    }
}

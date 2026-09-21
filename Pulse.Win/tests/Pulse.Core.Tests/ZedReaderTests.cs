using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Zed reader tests: the zed.dev provider filter, request-vs-cumulative usage
/// selection, the imported-thread skip, the folder_paths workspace label, and
/// the created_at→updated_at timestamp chain.
/// </summary>
public class ZedReaderTests : IDisposable
{
    private readonly string _dbPath;

    public ZedReaderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pulse-zed-{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE threads (
              id TEXT PRIMARY KEY, updated_at TEXT, data_type TEXT, data BLOB,
              created_at TEXT, folder_paths TEXT, folder_paths_order TEXT);
            """;
        schema.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private void InsertThread(string id, string json, string? folderPaths = null,
        string? createdAt = null, string? updatedAt = "2026-09-21T12:00:00Z", string? dataType = null,
        string? folderOrder = null)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO threads VALUES ($id, $updated, $type, $data, $created, $paths, $order)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$updated", (object?)updatedAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("$type", (object?)dataType ?? DBNull.Value);
        insert.Parameters.AddWithValue("$data", System.Text.Encoding.UTF8.GetBytes(json));
        insert.Parameters.AddWithValue("$created", (object?)createdAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("$paths", (object?)folderPaths ?? DBNull.Value);
        insert.Parameters.AddWithValue("$order", (object?)folderOrder ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    private static string Thread(string provider, string model, string requestUsage, string? cumulative = null) =>
        cumulative is null
            ? $$"""{"model":{"provider":"{{provider}}","model":"{{model}}"},"request_token_usage":[{{requestUsage}}]}"""
            : $$"""{"model":{"provider":"{{provider}}","model":"{{model}}"},"request_token_usage":[],"cumulative_token_usage":{{cumulative}}}""";

    [Fact]
    public void Zed_Dev_Thread_With_Request_Usage_Is_Counted()
    {
        InsertThread("t1", Thread("zed.dev", "claude-sonnet-4-5",
            """{"input_tokens":100,"output_tokens":20,"cache_read_input_tokens":30,"cache_creation_input_tokens":10}"""),
            folderPaths: "E:/Code/MyTrace\ne:/other");
        var record = Assert.Single(ZedReader.Read(_dbPath));
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(10, record.Tally.CacheWrite);
        Assert.Equal(30, record.Tally.CacheRead);
        Assert.Equal(20, record.Tally.Output);
        Assert.Equal("zed:t1", record.DeduplicationID);
        Assert.True(record.IsAggregate); // a thread total, not a turn
        Assert.Equal("MyTrace", record.Project);
    }

    [Fact]
    public void Cumulative_Usage_Used_When_Requests_Empty()
    {
        InsertThread("t1", Thread("zed.dev", "m", "[]",
            cumulative: """{"input_tokens":500,"output_tokens":50}"""));
        var record = Assert.Single(ZedReader.Read(_dbPath));
        Assert.Equal(500, record.Tally.Input);
        Assert.Equal(50, record.Tally.Output);
    }

    [Fact]
    public void Non_Zed_Dev_Provider_Is_Skipped()
    {
        // An external ACP agent's thread: the provider behind it is counted
        // where it came from, not here.
        InsertThread("t1", Thread("acp-external", "m", """{"input_tokens":100,"output_tokens":10}"""));
        Assert.Empty(ZedReader.Read(_dbPath));
    }

    [Fact]
    public void Imported_Thread_Is_Skipped()
    {
        InsertThread("t1", """{"imported":true,"model":{"provider":"zed.dev","model":"m"},"request_token_usage":[{"input_tokens":100,"output_tokens":10}]}""");
        Assert.Empty(ZedReader.Read(_dbPath));
    }

    [Fact]
    public void Negative_Counts_Clamp_To_Zero()
    {
        InsertThread("t1", Thread("zed.dev", "m", """{"input_tokens":-5,"output_tokens":10}"""));
        var record = Assert.Single(ZedReader.Read(_dbPath));
        Assert.Equal(0, record.Tally.Input);
        Assert.Equal(10, record.Tally.Output);
    }

    [Fact]
    public void Zero_Total_Thread_Is_Skipped()
    {
        InsertThread("t1", Thread("zed.dev", "m", """{"input_tokens":0,"output_tokens":0}"""));
        Assert.Empty(ZedReader.Read(_dbPath));
    }

    [Fact]
    public void Created_At_Is_Tried_When_Updated_At_Missing()
    {
        InsertThread("t1", Thread("zed.dev", "m", """{"input_tokens":10,"output_tokens":1}"""),
            createdAt: "2026-09-21T09:00:00Z", updatedAt: null);
        var record = Assert.Single(ZedReader.Read(_dbPath));
        Assert.Equal(DateTimeOffset.Parse("2026-09-21T09:00:00Z"), record.Timestamp);
    }

    [Fact]
    public void Folder_Order_Picks_The_Named_Path()
    {
        InsertThread("t1", Thread("zed.dev", "m", """{"input_tokens":10,"output_tokens":1}"""),
            folderPaths: "E:/First\ne:/second", folderOrder: "1", updatedAt: null, createdAt: "2026-09-21T09:00:00Z");
        var record = Assert.Single(ZedReader.Read(_dbPath));
        Assert.Equal("second", record.Project);
    }
}

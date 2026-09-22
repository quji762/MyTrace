using System.IO;
using Microsoft.Data.Sqlite;
using Pulse.App.Spend;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.App.Tests;

public class SpendPaneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pulse-pane-{Guid.NewGuid():N}");

    public SpendPaneTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Refresh_Path_Attributes_Kilo_And_Not_OpenCode()
    {
        WriteStore(Path.Combine(_root, ".local", "share", "kilo", "kilo.db"), "m-kilo");
        var report = TokenSpendWindow.ReadSources(_root, enabled: true, prices: null, spanStart: null, spanEnd: null);

        Assert.Contains(report.Sources, source => source.SourceId == "kilo" && source.Tokens > 0);
        Assert.DoesNotContain(report.Sources, source => source.SourceId == "opencode" && source.Tokens > 0);
    }

    [Fact]
    public void Refresh_Path_Does_Not_Discover_While_Off()
    {
        var calls = 0;
        var report = SpendReading.Read(false, _root, ModelPrices.Empty, null, null, _ =>
        {
            calls++;
            return true;
        });
        var throughPane = TokenSpendWindow.ReadSources(_root, enabled: false, prices: null, spanStart: null, spanEnd: null);
        Assert.Equal(0, calls);
        Assert.Empty(throughPane.Sources);
        Assert.Empty(report.Sources);
    }

    private static void WriteStore(string path, string model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE session (id TEXT PRIMARY KEY, slug TEXT, title TEXT, directory TEXT);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT);
            """;
        schema.ExecuteNonQuery();
        using var session = connection.CreateCommand();
        session.CommandText = "INSERT INTO session (id, slug, title, directory) VALUES ('s', 'slug', 'title', 'E:/work')";
        session.ExecuteNonQuery();
        using var message = connection.CreateCommand();
        message.CommandText = "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('m', 's', 0, 0, $data)";
        message.Parameters.AddWithValue("$data",
            "{\"role\":\"assistant\",\"modelID\":\"" + model + "\",\"time\":{\"created\":1789981200000}," +
            "\"tokens\":{\"input\":100,\"output\":20,\"reasoning\":0,\"cache\":{\"write\":0,\"read\":0}}}");
        message.ExecuteNonQuery();
    }
}

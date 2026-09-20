using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Crush reader: the registry is watched and its databases located, but no
/// records are ever produced — Crush reports a session cost in dollars, and
/// converting cost back into tokens would invent a count nobody measured.
/// </summary>
public class CrushReaderTests : IDisposable
{
    private readonly string _home;

    public CrushReaderTests() => _home = Path.Combine(Path.GetTempPath(), $"pulse-crush-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Registry_Projects_Resolve_Their_Databases()
    {
        var registry = Path.Combine(_home, "crush", "projects.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(registry, """
            {"projects":[
              {"path":"E:\\Code\\A","data_dir":"data"},
              {"path":"E:\\Code\\B","data_dir":"E:\\Shared\\crush-data"}
            ]}
            """);

        var databases = CrushReader.Databases(registry);
        Assert.Equal(2, databases.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine("E:\\Code\\A", "data", "crush.db")), databases[0]);
        // An absolute data_dir wins over the project path.
        Assert.Equal(Path.GetFullPath("E:\\Shared\\crush-data\\crush.db"), databases[1]);
    }

    [Fact]
    public void Environment_Root_Comes_First()
    {
        var envRoot = Path.Combine(_home, "env-root");
        var candidates = CrushReader.RegistryCandidates(
            userProfile: _home,
            environment: new Dictionary<string, string?> { ["CRUSH_GLOBAL_DATA"] = envRoot });
        Assert.Equal(Path.Combine(envRoot, "projects.json"), candidates[0]);
    }

    [Fact]
    public void No_Records_Is_The_Honest_Result()
    {
        // Cost is not a token count; the empty answer is the whole point.
        Assert.Empty(CrushReader.Records(userProfile: _home));
    }

    [Fact]
    public void Unparsable_Registry_Yields_No_Databases()
    {
        var registry = Path.Combine(_home, "crush", "projects.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(registry, "{ broken");
        Assert.Empty(CrushReader.Databases(registry));
    }
}

/// <summary>
/// ZCode reader tests: JSONL transcripts with aliased usage keys (cache
/// split out of an inclusive input) and the v2 SQLite store whose schema
/// documents input as cache-inclusive and output as reasoning-inclusive.
/// </summary>
public class ZCodeReaderTests : IDisposable
{
    private readonly string _home;

    public ZCodeReaderTests() => _home = Path.Combine(Path.GetTempPath(), $"pulse-zcode-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Jsonl_Usage_Splits_Cache_From_Inclusive_Input()
    {
        var projects = Path.Combine(_home, ".zcode", "projects");
        Directory.CreateDirectory(projects);
        File.WriteAllLines(Path.Combine(projects, "s1.jsonl"),
        [
            """{"timestamp":1789981200,"model":"z-model","sessionId":"sess1","usage":{"input_tokens":1000,"output_tokens":100,"input_cache_read":300,"input_cache_creation":100}}""",
        ]);

        var record = Assert.Single(ZCodeReader.Records(_home));
        Assert.Equal(600, record.Tally.Input);   // 1000 - 300 read - 100 write
        Assert.Equal(300, record.Tally.CacheRead);
        Assert.Equal(100, record.Tally.CacheWrite);
        Assert.Equal(100, record.Tally.Output);
    }

    [Fact]
    public void Jsonl_Token_Usage_Alternate_Spelling_Is_Tried()
    {
        var projects = Path.Combine(_home, ".zcode", "projects");
        Directory.CreateDirectory(projects);
        File.WriteAllLines(Path.Combine(projects, "s1.jsonl"),
        [
            """{"timestamp":1789981200,"model":"z-model","sessionId":"sess1","token_usage":{"input_tokens":500,"output_tokens":50}}""",
        ]);
        var record = Assert.Single(ZCodeReader.Records(_home));
        Assert.Equal(500, record.Tally.Input);
        Assert.Equal(50, record.Tally.Output);
    }

    [Fact]
    public void Jsonl_Bare_Total_Is_Unclassified()
    {
        var projects = Path.Combine(_home, ".zcode", "projects");
        Directory.CreateDirectory(projects);
        File.WriteAllLines(Path.Combine(projects, "s1.jsonl"),
        [
            """{"timestamp":1789981200,"model":"z-model","sessionId":"sess1","usage":{"totalTokens":777}}""",
        ]);
        var record = Assert.Single(ZCodeReader.Records(_home));
        Assert.Equal(777, record.UnclassifiedTokens);
        Assert.Equal(0, record.Tally.Total); // no kind was named; none is invented
    }

    [Fact]
    public void Database_Input_Is_Cache_Inclusive_And_Output_Reasoning_Inclusive()
    {
        // The schema documents both containments: cache is subtracted from
        // input, reasoning is NOT folded into output a second time, and a
        // computed total beyond input+output is an unclassified remainder.
        var dbDir = Path.Combine(_home, ".zcode", "cli", "db");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "db.sqlite");
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE model_usage (
                  id TEXT PRIMARY KEY, session_id TEXT, model_id TEXT,
                  started_at TEXT, completed_at TEXT,
                  input_tokens INTEGER, output_tokens INTEGER, reasoning_tokens INTEGER,
                  cache_read_input_tokens INTEGER, cache_creation_input_tokens INTEGER,
                  computed_total_tokens INTEGER);
                INSERT INTO model_usage VALUES (
                  'u1', 'sess1', 'z-model', '1789981200000', '1789981500000',
                  1000, 500, 120, 300, 100, 1700);
                """;
            schema.ExecuteNonQuery();
        }

        var record = Assert.Single(ZCodeReader.Database(dbPath));
        Assert.Equal(600, record.Tally.Input);      // 1000 - 300 - 100
        Assert.Equal(500, record.Tally.Output);     // reasoning already inside: not 620
        Assert.Equal(300, record.Tally.CacheRead);
        Assert.Equal(100, record.Tally.CacheWrite);
        Assert.Equal(200, record.UnclassifiedTokens); // 1700 - (1000 + 500)
        Assert.Equal("z-model", record.Model);
    }

    [Fact]
    public void Legacy_Database_Without_Computed_Or_Session_Still_Reads()
    {
        Directory.CreateDirectory(_home);
        var dbPath = Path.Combine(_home, "legacy.sqlite");
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE model_usage (
                  id TEXT PRIMARY KEY, session_id TEXT, model_id TEXT,
                  started_at TEXT, completed_at TEXT,
                  input_tokens INTEGER, output_tokens INTEGER);
                INSERT INTO model_usage VALUES ('u1', 's', 'm', '1789981200000', NULL, 10, 5);
                """;
            schema.ExecuteNonQuery();
        }

        var record = Assert.Single(ZCodeReader.Database(dbPath));
        Assert.Equal(10, record.Tally.Input);
        Assert.Equal(5, record.Tally.Output);
        Assert.Equal("m", record.Model);
    }
}

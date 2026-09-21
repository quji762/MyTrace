using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Fx reader tests: per-model snapshot records with the shared session
/// timestamp, the fx-unknown aggregate fallback, reasoning left out with a
/// partial mark, index.json titles, and the created_at_ms fallback.
/// </summary>
public class FxUsageReaderTests : IDisposable
{
    private readonly string _sessions;

    public FxUsageReaderTests()
    {
        _sessions = Path.Combine(Path.GetTempPath(), $"pulse-fx-{Guid.NewGuid():N}", ".fx", "sessions");
        Directory.CreateDirectory(Path.Combine(_sessions, "sess1"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_sessions))!, recursive: true); }
        catch (IOException) { }
    }

    private void WriteSession(string sessionId, string usage, string? sessionJson = null, long updatedMs = 1789981200000)
    {
        var dir = Path.Combine(_sessions, sessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "usage-v2.json"), usage);
        if (sessionJson is not null)
            File.WriteAllText(Path.Combine(dir, "session.json"),
                string.Concat(
                    "{\"updated_at_ms\":", updatedMs.ToString(),
                    ",\"created_at_ms\":1789980000000,\"workspace_root\":\"E:/Code/MyTrace\"}"));
    }

    [Fact]
    public void Per_Model_Snapshot_Emits_One_Record_Per_Model()
    {
        WriteSession("sess1",
            """{"session_id":"sess1","snapshot":{"models":[{"model":"gemini-3-pro","input_tokens":100,"output_tokens":50,"cache_read_tokens":20,"cache_write_tokens":10,"reasoning_tokens":30},{"model":"gemini-3-flash","input_tokens":200,"output_tokens":80}]}}""",
            sessionJson: "{}");

        var records = FxUsageReader.Records(Path.GetDirectoryName(Path.GetDirectoryName(_sessions)));
        Assert.Equal(2, records.Count);

        var pro = records.Single(r => r.Model == "gemini-3-pro");
        Assert.Equal(100, pro.Tally.Input);
        Assert.Equal(20, pro.Tally.CacheRead);
        Assert.Equal(50, pro.Tally.Output);   // reasoning left out: not 80
        Assert.True(pro.IsPartial);           // a reported reasoning count was unplaced
        Assert.True(pro.IsAggregate);         // session-level timing only

        var flash = records.Single(r => r.Model == "gemini-3-flash");
        Assert.False(flash.IsPartial);        // no reasoning reported there
    }

    [Fact]
    public void Empty_Models_Array_Falls_Back_To_Fx_Unknown()
    {
        // Totals are not lost when the product groups nothing.
        WriteSession("sess1",
            """{"session_id":"sess1","snapshot":{"input_tokens":500,"output_tokens":100}}""",
            sessionJson: "{}");

        var record = Assert.Single(FxUsageReader.Records(Path.GetDirectoryName(Path.GetDirectoryName(_sessions))));
        Assert.Equal("fx-unknown", record.Model);
        Assert.Equal(500, record.Tally.Input);
        Assert.Equal("fx:sess1:fx-unknown", record.DeduplicationID);
    }

    [Fact]
    public void Updated_At_Ms_Zero_Falls_Back_To_Created_At_Ms()
    {
        var dir = Path.Combine(_sessions, "sess1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "usage-v2.json"),
            """{"session_id":"sess1","snapshot":{"input_tokens":10,"output_tokens":5}}""");
        File.WriteAllText(Path.Combine(dir, "session.json"),
            """{"updated_at_ms":0,"created_at_ms":1789980000000}""");

        var record = Assert.Single(FxUsageReader.Records(Path.GetDirectoryName(Path.GetDirectoryName(_sessions))));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789980000000), record.Timestamp);
    }

    [Fact]
    public void No_Timestamp_At_All_Skips_The_Session()
    {
        var dir = Path.Combine(_sessions, "sess1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "usage-v2.json"),
            """{"session_id":"sess1","snapshot":{"input_tokens":10,"output_tokens":5}}""");
        Assert.Empty(FxUsageReader.Records(Path.GetDirectoryName(Path.GetDirectoryName(_sessions))));
    }

    [Fact]
    public void Index_Json_Title_Travels_With_The_Record()
    {
        WriteSession("sess1",
            """{"session_id":"sess1","snapshot":{"input_tokens":10,"output_tokens":5}}""",
            sessionJson: "{}");
        File.WriteAllText(Path.Combine(_sessions, "index.json"),
            """{"sessions":{"sess1":{"title":"Fixing the login flow"}}}""");

        var record = Assert.Single(FxUsageReader.Records(Path.GetDirectoryName(Path.GetDirectoryName(_sessions))));
        Assert.Equal("Fixing the login flow", record.Title);
    }
}

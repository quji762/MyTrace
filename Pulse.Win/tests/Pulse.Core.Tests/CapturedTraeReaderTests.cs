using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Trae reader tests: the Auto-mode trae-&lt;mode&gt; classification, the model
/// name normalization table, the cross-page multiset reconciliation ({A}+{A,B}
/// → A and B, never two As), the shared-region partial mark, and the
/// byte-identical page fold.
/// </summary>
public class CapturedTraeReaderTests : IDisposable
{
    private readonly string _root;

    public CapturedTraeReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-trae-{Guid.NewGuid():N}", ".trae", "captures");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch (IOException) { }
    }

    private static string Row(string sessionId, long usageTime, string? modelName, string mode, int input, int output, int cacheRead = 0, int cacheWrite = 0)
    {
        var nameJson = modelName is null ? "null" : "\"" + modelName + "\"";
        return string.Concat(
            "{\"session_id\":\"", sessionId,
            "\",\"usage_time\":", usageTime.ToString(),
            ",\"model_name\":", nameJson,
            ",\"mode\":\"", mode,
            "\",\"extra_info\":{\"input_token\":", input.ToString(),
            ",\"output_token\":", output.ToString(),
            ",\"cache_read_token\":", cacheRead.ToString(),
            ",\"cache_write_token\":", cacheWrite.ToString(), "}}");
    }

    [Fact]
    public void Auto_Mode_Row_Lands_Under_Trae_Mode()
    {
        // model_name empty when the system picked a model per turn; the true
        // per-turn model is not recoverable.
        WriteCapture("page1.json", Row("s1", 1789981200, null, "auto", 100, 20));
        var record = Assert.Single(CapturedTraeReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal("trae-auto", record.Model);
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal("s1", record.SessionID);
    }

    [Fact]
    public void Model_Name_Table_Normalizes_Known_Names()
    {
        WriteCapture("page1.json",
            Row("s1", 1789981200, "Claude Sonnet 4.5", "auto", 10, 1),
            Row("s2", 1789981300, "GPT-5", "auto", 10, 1),
            Row("s3", 1789981400, "GLM 5.1", "auto", 10, 1));
        var models = CapturedTraeReader.RecordsFromRoots(new[] { _root })
            .Select(r => r.Model).ToHashSet();
        Assert.Equal(new HashSet<string> { "claude-sonnet-4-5", "gpt-5", "glm-5.1" }, models);
    }

    [Fact]
    public void Unrecognised_Model_Passes_Through_Unpriced()
    {
        WriteCapture("page1.json", Row("s1", 1789981200, "MysteryModel X", "auto", 10, 1));
        var record = Assert.Single(CapturedTraeReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal("MysteryModel X", record.Model); // not disguised
    }

    [Fact]
    public void Overlapping_Pages_Reconcile_By_Content_Multiset()
    {
        // Two pages share row A; page2 adds row B. {A} and {A,B} give A and B,
        // never two As — and the shared region marks the scope partial.
        WriteCapture("page1.json",
            Row("s1", 1789981200, "m", "auto", 100, 10));
        WriteCapture("page2.json",
            Row("s1", 1789981200, "m", "auto", 100, 10),
            Row("s2", 1789981300, "m", "auto", 200, 20));

        var records = CapturedTraeReader.RecordsFromRoots(new[] { _root });
        Assert.Equal(2, records.Count); // A and B, not two As plus B
        Assert.All(records, r => Assert.True(r.IsPartial)); // the shared region cannot be confirmed
    }

    [Fact]
    public void Byte_Identical_Page_Folds_Outright()
    {
        var row = Row("s1", 1789981200, "m", "auto", 100, 10);
        WriteCapture("page1.json", row);
        WriteCapture("page2.json", row); // a re-dump of the same bytes

        Assert.Single(CapturedTraeReader.RecordsFromRoots(new[] { _root }));
    }

    [Fact]
    public void Two_Rows_In_One_Page_Stay_Two()
    {
        // No per-row id: two rows in the same second are left as two — they
        // can be two requests.
        WriteCapture("page1.json",
            Row("s1", 1789981200, "m", "auto", 100, 10),
            Row("s1", 1789981200, "m", "auto", 100, 10));
        Assert.Equal(2, CapturedTraeReader.RecordsFromRoots(new[] { _root }).Count);
    }

    [Fact]
    public void Non_Array_Root_And_Zero_Time_Yield_Nothing()
    {
        File.WriteAllText(Path.Combine(_root, "not-array.json"), """{"a":1}""");
        WriteCapture("zero-time.json", Row("s1", 0, "m", "auto", 10, 1));
        Assert.Empty(CapturedTraeReader.RecordsFromRoots(new[] { _root }));
    }

    private void WriteCapture(string name, params string[] rows) =>
        File.WriteAllText(Path.Combine(_root, name), "[" + string.Join(",", rows) + "]");
}

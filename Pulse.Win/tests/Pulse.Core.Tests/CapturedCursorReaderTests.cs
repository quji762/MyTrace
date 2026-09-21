using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// CapturedCursor reader tests: the proved-shape rule (JSON needs
/// usageEventsDisplay, CSV needs the named columns), the usage.backup
/// exclusion, scope grouping, the JSON lane's authority over the CSV inside its
/// date range, the no-account partial rule, and no cost carried into records.
/// </summary>
public class CapturedCursorReaderTests : IDisposable
{
    private readonly string _root;

    public CapturedCursorReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-cursor-cap-{Guid.NewGuid():N}", "UsageImports", "cursor");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch (IOException) { }
    }

    private const string JsonExport = """
        {"usageEventsDisplay":[
          {"model":"gpt-5","timestamp":1789981200000,"conversationId":"conv-1","tokenUsage":{"inputTokens":100,"outputTokens":20,"cacheReadTokens":10,"cacheWriteTokens":5}}
        ]}
        """;

    private const string CsvExport = "Date,Model,Input (w/o cache write),Input (w/ cache write),Cache read,Output tokens,Cloud Agent ID\r\n" +
        "2026-09-21 17:00:00,gpt-5,100,105,10,20,cloud-1";

    [Fact]
    public void Json_Export_Parses_And_Scopes_By_Account()
    {
        File.WriteAllText(Path.Combine(_root, "usage.acct1.json"), JsonExport);
        var record = Assert.Single(CapturedCursorReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(5, record.Tally.CacheWrite);
        Assert.Equal("cursor:acct1:conv-1", record.SessionID); // account folded in
        // A native name states an account: no unscoped partial.
        Assert.False(record.IsPartial);
    }

    [Fact]
    public void Csv_Export_Parses_By_Column_Names()
    {
        File.WriteAllText(Path.Combine(_root, "export.csv"), CsvExport);
        var record = Assert.Single(CapturedCursorReader.RecordsFromRoots(new[] { _root }));
        // Independent buckets: w/o cache write is fresh input, w/ cache write is
        // the cache-write bucket; Total Tokens deliberately not read.
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(105, record.Tally.CacheWrite);
        Assert.Equal(10, record.Tally.CacheRead);
        Assert.Equal(20, record.Tally.Output);
        Assert.True(record.IsAggregate); // a day-level report, not a request time
        Assert.Equal("cursor:unscoped:cloud:cloud-1", record.SessionID); // shared import scope
        Assert.True(record.IsPartial); // no account declared: not claimed complete
    }

    [Fact]
    public void Csv_Row_Inside_The_Json_Range_Is_Not_Added()
    {
        File.WriteAllText(Path.Combine(_root, "usage.a.json"), JsonExport);
        File.WriteAllText(Path.Combine(_root, "usage.a.csv"),
            "Date,Model,Input (w/o cache write),Input (w/ cache write),Cache read,Output tokens\r\n" +
            "2026-09-21 17:00:00,gpt-5,100,105,10,20,cloud-1");

        var records = CapturedCursorReader.RecordsFromRoots(new[] { _root });
        Assert.Single(records); // the CSV row inside the range is not added
        Assert.True(records[0].IsPartial);
    }

    [Fact]
    public void Usage_Backup_Stem_Is_Excluded()
    {
        File.WriteAllText(Path.Combine(_root, "usage.acct1.json"), JsonExport);
        File.WriteAllText(Path.Combine(_root, "usage.backup.json"), JsonExport);
        Assert.Single(CapturedCursorReader.RecordsFromRoots(new[] { _root }));
    }

    [Fact]
    public void Wrong_Shape_Is_Left_Alone()
    {
        File.WriteAllText(Path.Combine(_root, "export.json"), """{"other":"shape"}""");
        File.WriteAllText(Path.Combine(_root, "export.csv"), "not,a,real,cursor,csv,with,date,,model");
        Assert.Empty(CapturedCursorReader.RecordsFromRoots(new[] { _root }));
    }

    [Fact]
    public void Json_Lane_Is_Authoritative_Inside_Its_Date_Range()
    {
        // Same conversation in both lanes: the JSON is authoritative; the CSV
        // row inside its date range is unverifiable overlap, so the account is
        // marked partial and the CSV row is not added.
        File.WriteAllText(Path.Combine(_root, "usage.a.json"), JsonExport);
        File.WriteAllText(Path.Combine(_root, "usage.a.csv"), CsvExport);

        var records = CapturedCursorReader.RecordsFromRoots(new[] { _root });
        Assert.Single(records); // the CSV row fell inside the JSON date range
        Assert.True(records[0].IsPartial);
        Assert.Equal(100, records[0].Tally.Input);
    }

    [Fact]
    public void Csv_Row_Outside_The_Json_Range_Is_Kept()
    {
        File.WriteAllText(Path.Combine(_root, "usage.a.json"), JsonExport);
        var outside = "Date,Model,Input (w/o cache write),Input (w/ cache write),Cache read,Output tokens\r\n" +
            "2027-01-01 00:00:00,gpt-5,300,305,10,50,cloud-9";
        File.WriteAllText(Path.Combine(_root, "usage.a.csv"), outside);

        var records = CapturedCursorReader.RecordsFromRoots(new[] { _root });
        Assert.Equal(2, records.Count); // outside the range: kept
        // A declared account ("a") means the scope is claimed, so the kept rows
        // are not partial.
        Assert.All(records, r => Assert.False(r.IsPartial));
    }
}

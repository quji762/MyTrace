using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// GJC reader tests: entry-id-only dedup (an id-less row is always counted),
/// byte-for-byte mirror detection, unix-millisecond vs RFC3339 timestamps, and
/// cost.total never read as tokens.
/// </summary>
public class GjcUsageReaderTests : IDisposable
{
    private readonly string _root;

    public GjcUsageReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-gjc-{Guid.NewGuid():N}", ".gjc");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Header = """{"type":"session","id":"sess-1","timestamp":"2026-09-21T09:00:00Z","cwd":"E:/Code/MyTrace"}""";

    private static string Message(string entryId, string timestamp, int input = 100, int output = 20, string model = "gjc-model")
    {
        return string.Concat(
            "{\"type\":\"message\",\"id\":\"", entryId,
            "\",\"message\":{\"role\":\"assistant\",\"model\":\"", model,
            "\",\"timestamp\":\"", timestamp,
            "\",\"usage\":{\"input\":", input.ToString(),
            ",\"output\":", output.ToString(),
            ",\"cacheRead\":10,\"cacheWrite\":5,\"totalTokens\":135,\"cost\":{\"total\":0.5}}}}");
    }

    [Fact]
    public void Assistant_Message_Parses_All_Buckets()
    {
        File.WriteAllLines(Path.Combine(_root, "s1.jsonl"),
        [
            Header,
            Message("e1", "2026-09-21T10:00:00Z"),
        ]);

        var all = GjcUsageReader.RecordsFromRoots(new[] { _root });
        var record = Assert.Single(all);
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(20, record.Tally.Output);
        Assert.Equal(10, record.Tally.CacheRead);
        Assert.Equal(5, record.Tally.CacheWrite);
        Assert.Equal("gjc:sess-1:e1", record.DeduplicationID);
        Assert.Equal("MyTrace", record.Project);
        Assert.Equal("sess-1", record.SessionName);
    }

    [Fact]
    public void IdLess_Row_Is_Always_Counted_Even_With_Identical_Figures()
    {
        // Two id-less rows with the same second, model and tokens are two calls:
        // identical figures are not evidence that two calls are one, and the
        // store wrote no id to say so.
        File.WriteAllLines(Path.Combine(_root, "s1.jsonl"),
        [
            Header,
            """{"type":"message","message":{"role":"assistant","model":"m","timestamp":"2026-09-21T10:00:00Z","usage":{"input":100,"output":20}}}""",
            """{"type":"message","message":{"role":"assistant","model":"m","timestamp":"2026-09-21T10:00:00Z","usage":{"input":100,"output":20}}}""",
        ]);

        Assert.Equal(2, GjcUsageReader.RecordsFromRoots(new[] { _root }).Count);
    }

    [Fact]
    public void Message_Millis_Outrank_The_Envelope_Rfc3339()
    {
        File.WriteAllLines(Path.Combine(_root, "s1.jsonl"),
        [
            Header,
            """{"type":"message","id":"e1","timestamp":"2026-09-21T09:30:00Z","message":{"role":"assistant","model":"m","timestamp":1789981200000,"usage":{"input":1,"output":1}}}""",
        ]);
        var record = Assert.Single(GjcUsageReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789981200000), record.Timestamp);
    }

    [Fact]
    public void Cost_Total_Is_Never_Read_As_Tokens()
    {
        // cost.total is the product's own dollars; usage tokens are the counts.
        File.WriteAllLines(Path.Combine(_root, "s1.jsonl"),
        [
            Header,
            Message("e1", "2026-09-21T10:00:00Z"),
        ]);
        var record = Assert.Single(GjcUsageReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(135, record.Tally.Total); // 100+20+10+5, not the 0.5 dollars
    }

    [Fact]
    public void Service_Tier_Rows_Are_Ignored()
    {
        File.WriteAllLines(Path.Combine(_root, "s1.jsonl"),
        [
            Header,
            """{"type":"service-tier","id":"t1","tier":"pro"}""",
            Message("e1", "2026-09-21T10:00:00Z"),
        ]);
        var record = Assert.Single(GjcUsageReader.RecordsFromRoots(new[] { _root }));
    }
}

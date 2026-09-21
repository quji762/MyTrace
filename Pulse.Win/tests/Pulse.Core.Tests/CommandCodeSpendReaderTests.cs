using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// CommandCode spend reader tests: the tree model inheritance (ancestor model,
/// not last model_change), the legacy flat fallback with config model, and the
/// rewind semantics (every branch's replies count).
/// </summary>
public class CommandCodeSpendReaderTests : IDisposable
{
    private readonly string _root;

    public CommandCodeSpendReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-cc-spend-{Guid.NewGuid():N}", ".commandcode");
        Directory.CreateDirectory(Path.Combine(_root, "projects", "proj"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Header = """{"type":"session","id":"sess1"}""";

    [Fact]
    public void Tree_Ancestor_Model_Is_Used_Not_Last_Model_Change()
    {
        // The model change sits on ANOTHER branch: ancestry resolves this
        // reply's model, file order does not.
        File.WriteAllLines(Path.Combine(_root, "projects", "proj", "s1.jsonl"),
        [
            Header,
            """{"id":"m1","parentId":null,"model":"claude-sonnet-4-5"}""",
            """{"id":"m2","parentId":"m1","role":"assistant","timestamp":"2026-09-21T10:00:00Z","model":"claude-sonnet-4-5","usage":{"inputTokens":100,"outputTokens":20,"cacheReadTokens":0,"cacheWriteTokens":0}}""",
            """{"id":"m3","parentId":"m1","type":"model_change","model":"gpt-5"}""",
            """{"id":"m4","parentId":"m3"}""",
            """{"id":"m5","parentId":"m2","role":"assistant","timestamp":"2026-09-21T10:05:00Z","usage":{"inputTokens":50,"outputTokens":10,"cacheReadTokens":0,"cacheWriteTokens":0}}""",
        ]);

        var records = CommandCodeSpendReader.RecordsFromRoots(new[] { Path.Combine(_root, "projects"), Path.Combine(_root, "config.json") });
        Assert.Equal(2, records.Count);
        // m5's ancestor chain (m2→m1) names claude-sonnet-4-5, not the later
        // gpt-5 change on the other branch.
        Assert.All(records, r => Assert.Equal("claude-sonnet-4-5", r.Model));
    }

    [Fact]
    public void Legacy_Flat_Format_Counts_With_Config_Model_Fallback()
    {
        File.WriteAllText(Path.Combine(_root, "config.json"), """{"model":"legacy-model"}""");
        File.WriteAllLines(Path.Combine(_root, "projects", "proj", "flat.jsonl"),
        [
            """{"role":"assistant","timestamp":"2026-09-21T10:00:00Z","usage":{"inputTokens":50,"outputTokens":5}}""",
        ]);

        var record = Assert.Single(CommandCodeSpendReader.RecordsFromRoots(
            new[] { Path.Combine(_root, "projects"), Path.Combine(_root, "config.json") }));
        Assert.Equal(50, record.Tally.Input);
        Assert.Equal(5, record.Tally.Output);
        Assert.Equal("legacy-model", record.Model); // from config.json
    }

    [Fact]
    public void Legacy_Row_Without_Usage_Contributes_Nothing()
    {
        // Token counts from string lengths are estimates, not reported tokens.
        File.WriteAllLines(Path.Combine(_root, "projects", "proj", "flat.jsonl"),
        [
            """{"role":"assistant","timestamp":"2026-09-21T10:00:00Z"}""",
        ]);
        Assert.Empty(CommandCodeSpendReader.RecordsFromRoots(
            new[] { Path.Combine(_root, "projects"), Path.Combine(_root, "config.json") }));
    }

    [Fact]
    public void Rewound_Branch_Keeps_Its_Usage()
    {
        // /rewind moves the leaf but abandoned replies consumed real tokens.
        File.WriteAllLines(Path.Combine(_root, "projects", "proj", "s1.jsonl"),
        [
            Header,
            """{"id":"m1","role":"assistant","timestamp":"2026-09-21T10:00:00Z","model":"m","usage":{"inputTokens":100,"outputTokens":10}}""",
            """{"id":"m2","parentId":"m1","role":"assistant","timestamp":"2026-09-21T10:05:00Z","model":"m","usage":{"inputTokens":50,"outputTokens":5}}""",
        ]);

        Assert.Equal(2, CommandCodeSpendReader.RecordsFromRoots(
            new[] { Path.Combine(_root, "projects"), Path.Combine(_root, "config.json") }).Count);
    }

    [Fact]
    public void Checkpoint_Files_Are_Excluded()
    {
        File.WriteAllLines(Path.Combine(_root, "projects", "proj", "s1.checkpoints.jsonl"),
        [
            """{"role":"assistant","timestamp":"2026-09-21T10:00:00Z","usage":{"inputTokens":99,"outputTokens":9}}""",
        ]);
        Assert.Empty(CommandCodeSpendReader.RecordsFromRoots(
            new[] { Path.Combine(_root, "projects"), Path.Combine(_root, "config.json") }));
    }
}

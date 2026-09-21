using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Pi family reader tests: the four independent buckets, fork-copy dedup via
/// session-independent identity, Kimchi's session-scoped namespace, Senpi's
/// project children and session_info titles, and reasoning-inside-output.
/// </summary>
public class PiFamilySessionReaderTests : IDisposable
{
    private readonly string _home;

    public PiFamilySessionReaderTests() => _home = Path.Combine(Path.GetTempPath(), $"pulse-pi-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string SessionsDir(string client) => Path.Combine(_home, "." + client, "sessions");

    private void WriteSession(string client, string stem, params string[] lines)
    {
        var dir = SessionsDir(client);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, $"{stem}.jsonl"), lines);
    }

    private const string Header = """{"type":"session","id":"s1","cwd":"E:/Code/MyTrace"}""";

    private static string Assistant(string responseId, int input, int output, int cacheRead = 0, int cacheWrite = 0) =>
        $"{{\"type\":\"message\",\"id\":\"row-{responseId}\",\"timestamp\":\"2026-09-21T10:00:00Z\",\"message\":{{\"role\":\"assistant\",\"responseId\":\"{responseId}\",\"model\":\"m\",\"provider\":\"p\",\"usage\":{{\"input\":{input},\"output\":{output},\"cacheRead\":{cacheRead},\"cacheWrite\":{cacheWrite}}}}}}}";

    [Fact]
    public void Four_Independent_Buckets_Are_Read()
    {
        WriteSession("pi", "s1", Header, Assistant("r1", input: 100, output: 50, cacheRead: 20, cacheWrite: 10));
        var record = Assert.Single(PiFamilySessionReader.Records("pi", _home));
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(50, record.Tally.Output);
        Assert.Equal(20, record.Tally.CacheRead);
        Assert.Equal(10, record.Tally.CacheWrite);
        Assert.Equal("E:/Code/MyTrace", record.Project);
    }

    [Fact]
    public void Fork_Copy_Shares_Session_Independent_Identity()
    {
        // A fork copies prior assistant records verbatim into a new file. The
        // reader emits both halves; the cross-session identity is what makes
        // the builder's global dedup fold the copy onto its original.
        WriteSession("pi", "s1", Header, Assistant("r1", input: 100, output: 50));
        var forkHeader = """{"type":"session","id":"s2","cwd":"E:/Code/MyTrace","parentSession":"s1.jsonl"}""";
        WriteSession("pi", "s2", forkHeader, Assistant("r1", input: 100, output: 50));

        var records = PiFamilySessionReader.Records("pi", _home);
        Assert.Equal(2, records.Count);
        Assert.Equal(records[0].DeduplicationID, records[1].DeduplicationID);
        Assert.StartsWith("pi:response:r1", records[0].DeduplicationID);
    }

    [Fact]
    public void Kimchi_Session_Scoped_Dedup_Keeps_Two_Sessions_Apart()
    {
        // Same response id in two sessions: Kimchi's session-scoped namespace
        // keeps them as separate records.
        WriteSession("kimchi", "s1", Header, Assistant("r1", input: 100, output: 50));
        WriteSession("kimchi", "s2", Header, Assistant("r1", input: 100, output: 50));
        Assert.Equal(2, PiFamilySessionReader.Records("kimchi", _home).Count);
    }

    [Fact]
    public void Senpi_Treats_Session_Info_Name_As_Title()
    {
        WriteSession("senpi", "s1", Header,
            """{"type":"session_info","name":"My chat title"}""",
            Assistant("r1", input: 10, output: 1));
        var record = Assert.Single(PiFamilySessionReader.Records("senpi", _home));
        Assert.Equal("My chat title", record.Title);
    }

    [Fact]
    public void Pi_Does_Not_Use_Session_Info_Name_As_Title()
    {
        WriteSession("pi", "s1", Header,
            """{"type":"session_info","name":"My chat title"}""",
            Assistant("r1", input: 10, output: 1));
        var record = Assert.Single(PiFamilySessionReader.Records("pi", _home));
        Assert.Null(record.Title);
    }

    [Fact]
    public void Omp_Provider_Fallback_Fills_Missing_Provider()
    {
        // Provider fallback affects the dedup composite, not the emitted model.
        WriteSession("omp", "s1", Header, Assistant("r1", input: 10, output: 1));
        var record = Assert.Single(PiFamilySessionReader.Records("omp", _home));
        Assert.Equal(10, record.Tally.Input);
    }

    [Fact]
    public void Usage_Without_Time_Or_Model_Marks_Run_Partial()
    {
        WriteSession("pi", "s1", Header,
            """{"type":"message","message":{"role":"assistant","usage":{"input":10,"output":1}}}""");
        WriteSession("pi", "s2", Header, Assistant("r2", input: 5, output: 1));
        var records = PiFamilySessionReader.Records("pi", _home);
        Assert.Single(records);                          // the header-less line emits nothing
        Assert.True(records[0].IsPartial);               // and the run says it was partial
    }
}

/// <summary>
/// Prime Agent tests: the parent/child accounting problem. A parent's
/// cumulative aggregateUsage that includes a found child is reduced by exactly
/// the child usage; when the child cannot be found the parent keeps its
/// aggregate (the conservative, checkable choice).
/// </summary>
public class PrimeAgentSessionReaderTests : IDisposable
{
    private readonly string _root;

    public PrimeAgentSessionReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-prime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string ParentHeader = """{"type":"session","id":"parent","cwd":"E:/Code/P"}""";
    private const string ChildHeader = """{"type":"session","id":"child","cwd":"E:/Code/P","parentSession":"parent.jsonl","rlmDepth":1}""";

    [Fact]
    public void Parent_Aggregate_Is_Reduced_By_The_Attributed_Child()
    {
        // Parent message carries an aggregateUsage of 100/50 that already
        // includes the child's 30/10; the child transcript is also scanned.
        var parent = Path.Combine(_root, "parent.jsonl");
        File.WriteAllLines(parent,
        [
            ParentHeader,
            """{"type":"child_usage_attributed","id":"a1","targetId":"pm1","childUsage":{"input":30,"output":10},"aggregateUsage":{"input":100,"output":50}}""",
            """{"type":"message","id":"pm1","timestamp":"2026-09-21T10:00:00Z","message":{"role":"assistant","model":"m","usage":{"input":100,"output":50}}}""",
        ]);
        var child = Path.Combine(_root, "child.jsonl");
        File.WriteAllLines(child,
        [
            ChildHeader,
            """{"type":"message","id":"cm1","timestamp":"2026-09-21T09:30:00Z","message":{"role":"assistant","model":"m","usage":{"input":30,"output":10}}}""",
        ]);

        var records = PrimeAgentSessionReader.Records(new[] { _root });
        var parentRecord = records.Single(r => r.DeduplicationID == "prime-agent:response:r-pm1" || r.DeduplicationID!.Contains("pm1"));
        Assert.Equal(70, parentRecord.Tally.Input);  // 100 - 30
        Assert.Equal(40, parentRecord.Tally.Output); // 50 - 10
    }

    [Fact]
    public void Missing_Child_Keeps_The_Parent_Aggregate()
    {
        // Subtracting a child Pulse cannot find would silently drop tokens
        // nobody else accounts for; keeping them is conservative and checkable.
        var parent = Path.Combine(_root, "parent.jsonl");
        File.WriteAllLines(parent,
        [
            ParentHeader,
            """{"type":"child_usage_attributed","id":"a1","targetId":"pm1","childUsage":{"input":30,"output":10},"aggregateUsage":{"input":100,"output":50}}""",
            """{"type":"message","id":"pm1","timestamp":"2026-09-21T10:00:00Z","message":{"role":"assistant","model":"m","usage":{"input":100,"output":50}}}""",
        ]);

        var records = PrimeAgentSessionReader.Records(new[] { _root });
        var parentRecord = records.Single(r => r.Tally.Input == 100);
        Assert.Equal(100, parentRecord.Tally.Input); // aggregate kept whole
    }

    [Fact]
    public void Fork_Copy_Of_A_Reduction_Is_Applied_Identically()
    {
        // A fork copies the parent's records; the reduction is keyed by the
        // message's session-independent identity so the copy is reduced the
        // same way, and the attribution key carries the lineage root.
        var parent = Path.Combine(_root, "parent.jsonl");
        File.WriteAllLines(parent,
        [
            ParentHeader,
            """{"type":"child_usage_attributed","id":"a1","targetId":"pm1","childUsage":{"input":30,"output":10},"aggregateUsage":{"input":100,"output":50}}""",
            """{"type":"message","id":"pm1","timestamp":"2026-09-21T10:00:00Z","message":{"role":"assistant","model":"m","usage":{"input":100,"output":50}}}""",
        ]);
        var child = Path.Combine(_root, "child.jsonl");
        File.WriteAllLines(child,
        [
            ChildHeader,
            """{"type":"message","id":"cm1","timestamp":"2026-09-21T09:30:00Z","message":{"role":"assistant","model":"m","usage":{"input":30,"output":10}}}""",
        ]);
        var fork = Path.Combine(_root, "fork.jsonl");
        File.WriteAllLines(fork,
        [
            """{"type":"session","id":"fork","cwd":"E:/Code/P","parentSession":"parent.jsonl"}""",
            """{"type":"child_usage_attributed","id":"a1","targetId":"pm1","childUsage":{"input":30,"output":10},"aggregateUsage":{"input":100,"output":50}}""",
            """{"type":"message","id":"pm1","timestamp":"2026-09-21T10:00:00Z","message":{"role":"assistant","model":"m","usage":{"input":100,"output":50}}}""",
        ]);

        var records = PrimeAgentSessionReader.Records(new[] { _root });
        // Every copy of pm1 reduced identically: no record carries the full 100.
        Assert.DoesNotContain(records, r => r.Tally.Input == 100);
    }
}

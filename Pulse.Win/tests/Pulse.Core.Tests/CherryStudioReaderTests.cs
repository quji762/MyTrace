using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// CherryStudio reader contract tests over hand-built JSONL trees in a temp
/// profile. The load-bearing rule: Cherry appends the same API call several
/// times as a response streams — same requestId, fresh uuid — so counting
/// lines would multiply every call by four. The identity is the call; the
/// counters fold by field-wise maximum.
/// </summary>
public class CherryStudioReaderTests : IDisposable
{
    private readonly string _home;
    private readonly string _roaming;

    public CherryStudioReaderTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"pulse-cherry-{Guid.NewGuid():N}");
        _roaming = Path.Combine(_home, "roaming");
        Directory.CreateDirectory(_roaming);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string V2Projects => Path.Combine(_roaming, "CherryStudio", "Data", "Agents", ".claude", "projects");

    private static string AssistantLine(string requestId, string uuid, int input, int output, string stamp = "2026-09-21T10:00:00Z")
    {
        return string.Concat(
            "{\"type\":\"assistant\",\"requestId\":\"", requestId,
            "\",\"uuid\":\"", uuid,
            "\",\"timestamp\":\"", stamp,
            "\",\"message\":{\"id\":\"msg_01\",\"model\":\"deepseek-v3\",\"usage\":{\"input_tokens\":",
            input.ToString(), ",\"output_tokens\":", output.ToString(), "}}}");
    }

    [Fact]
    public void Streaming_Duplicates_Fold_To_One_Call_By_Field_Maximum()
    {
        Directory.CreateDirectory(V2Projects);
        var sessionDir = Path.Combine(V2Projects, "my-project");
        Directory.CreateDirectory(sessionDir);
        // Three appends of the SAME call: usage grows as the stream does.
        File.WriteAllLines(Path.Combine(sessionDir, "s1.jsonl"),
        [
            AssistantLine("req-1", "uuid-a", input: 100, output: 5, "2026-09-21T10:00:00Z"),
            AssistantLine("req-1", "uuid-b", input: 100, output: 20, "2026-09-21T10:00:05Z"),
            AssistantLine("req-1", "uuid-c", input: 100, output: 48, "2026-09-21T10:00:09Z"),
        ]);

        var records = CherryStudioReader.Records(_home, _roaming).ToList();
        var record = Assert.Single(records);

        // Field-wise maximum: 100 in, 48 out — once, not three times.
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(48, record.Tally.Output);
        // The latest timestamp of the stream wins.
        Assert.Equal(DateTimeOffset.Parse("2026-09-21T10:00:09Z"), record.Timestamp);
        Assert.StartsWith("cherrystudio:", record.DeduplicationID);
    }

    [Fact]
    public void Distinct_RequestIds_Are_Separate_Calls()
    {
        Directory.CreateDirectory(V2Projects);
        var sessionDir = Path.Combine(V2Projects, "proj");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllLines(Path.Combine(sessionDir, "s1.jsonl"),
        [
            AssistantLine("req-1", "uuid-a", input: 10, output: 5),
            AssistantLine("req-2", "uuid-b", input: 20, output: 6),
        ]);

        Assert.Equal(2, CherryStudioReader.Records(_home, _roaming).Count());
    }

    [Fact]
    public void Identity_Falls_Back_To_Message_Id_Then_Is_Optional()
    {
        Directory.CreateDirectory(V2Projects);
        var sessionDir = Path.Combine(V2Projects, "proj");
        Directory.CreateDirectory(sessionDir);

        // No requestId: message.id carries the identity.
        var noRequest = """{"type":"assistant","timestamp":"2026-09-21T10:00:00Z","message":{"id":"msg_9","model":"m","usage":{"input_tokens":10,"output_tokens":1}}}""";
        // No identity at all: kept on its own (a timestamp is required).
        var noIdentity = """{"type":"assistant","timestamp":"2026-09-21T10:01:00Z","message":{"model":"m","usage":{"input_tokens":7,"output_tokens":1}}}""";

        File.WriteAllLines(Path.Combine(sessionDir, "s1.jsonl"), [noRequest, noIdentity]);
        var records = CherryStudioReader.Records(_home, _roaming).ToList();

        Assert.Equal(2, records.Count);
        // message.id carries the identity when requestId is absent.
        Assert.Contains("msg_9", records.Single(r => r.Tally.Input == 10).DeduplicationID);
        // No identity at all: kept on its own with none.
        Assert.Null(records.Single(r => r.Tally.Input == 7).DeduplicationID);
    }

    [Fact]
    public void V2_Tree_Wins_The_Same_Relative_Session_Path()
    {
        var v2 = Path.Combine(_roaming, "CherryStudio", "Data", "Agents", ".claude", "projects", "proj");
        var v1 = Path.Combine(_roaming, "CherryStudio", ".claude", "projects", "proj");
        Directory.CreateDirectory(v2);
        Directory.CreateDirectory(v1);
        File.WriteAllText(Path.Combine(v2, "s1.jsonl"), AssistantLine("req-v2", "uuid-a", 10, 1));
        File.WriteAllText(Path.Combine(v1, "s1.jsonl"), AssistantLine("req-v1", "uuid-b", 20, 1));

        var records = CherryStudioReader.Records(_home, _roaming).ToList();
        // V2 is listed first and claims the relative path; V1's copy is skipped.
        Assert.Single(records);
        Assert.Equal(10, records[0].Tally.Input);
    }

    [Fact]
    public void Build_Folds_Into_A_Priced_Ledger_With_Sessions()
    {
        Directory.CreateDirectory(V2Projects);
        var sessionDir = Path.Combine(V2Projects, "proj");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllLines(Path.Combine(sessionDir, "s1.jsonl"),
        [
            AssistantLine("req-1", "uuid-a", input: 1_000_000, output: 100, "2026-09-21T10:00:00Z"),
        ]);

        var prices = new ModelPrices(new Dictionary<string, ModelPrice>
        {
            ["deepseek-v3"] = new("deepseek-v3", "DeepSeek V3", Input: 0.27, CacheWrite: null, CacheRead: 0.07, Output: 1.1),
        });
        var records = CherryStudioReader.Records(_home, _roaming);
        var result = AgentUsageLedger.Build(records, prices, TimeZoneInfo.Utc);

        Assert.Equal(1_000_100, result.Ledger.AllTime.Tokens);
        Assert.Equal(1, result.Sessions.Count);
        Assert.Equal(1_000_100, result.Sessions[0].Tokens);
        Assert.Equal("s1", result.Sessions[0].Name);
        Assert.Equal("proj", result.Sessions[0].Project);
    }
}

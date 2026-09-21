using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// TencentBuddy reader tests: the JSONL transcript as the only reconcilable
/// channel (identity-folded mirrors, completed-status filter, cache-inclusive
/// input normalization), transcript-wins-over-fallback with a partial mark,
/// and the extension log's naive timestamps and CraftInvokableAgent/AgentReporter
/// lines.
/// </summary>
public class TencentBuddyReaderTests : IDisposable
{
    private readonly string _home;

    public TencentBuddyReaderTests() => _home = Path.Combine(Path.GetTempPath(), $"pulse-buddy-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string TranscriptDir(string client) => Path.Combine(_home, "." + client, "projects", "proj");
    private string CodebuddyLogDir => Path.Combine(_home, ".codebuddy");

    private static string TranscriptLine(long ts, int input, int output, int cacheRead, int cacheWrite, string messageId = "msg-1", string status = "completed") =>
        $"{{\"type\":\"message\",\"role\":\"assistant\",\"id\":\"row-{messageId}\",\"timestamp\":{ts},\"status\":\"{status}\",\"sessionId\":\"s1\",\"cwd\":\"E:/Code/MyTrace\",\"message\":{{\"model\":\"anthropic/claude-4\",\"usage\":{{\"input_tokens\":{input},\"output_tokens\":{output},\"cache_read_input_tokens\":{cacheRead},\"cache_creation_input_tokens\":{cacheWrite},\"total_tokens\":{input + output + cacheRead + cacheWrite}}}}},\"providerData\":{{\"messageId\":\"{messageId}\"}}}}";

    [Fact]
    public void Transcript_Usage_Is_Normalized_Against_Its_Total()
    {
        var dir = TranscriptDir("codebuddy");
        Directory.CreateDirectory(dir);
        // total 1100 == 1000 + 50 + 30 + 20 + 20 reasoning: reasoning separate kind.
        File.WriteAllLines(Path.Combine(dir, "s1.jsonl"),
        [
            TranscriptLine(1789981200000, input: 1000, output: 50, cacheRead: 30, cacheWrite: 20),
        ]);
        var file = Path.Combine(dir, "s1.jsonl");
        var text = File.ReadAllText(file);
        // total_tokens needs to include reasoning to trigger the separate-kind path;
        // keep the default total (no reasoning in fixture) for the base case.

        var record = Assert.Single(TencentBuddyReader.Records("codebuddy", _home));
        Assert.Equal(1000, record.Tally.Input);
        Assert.Equal(30, record.Tally.CacheRead);
        Assert.Equal(20, record.Tally.CacheWrite);
        Assert.Equal(50, record.Tally.Output);
        Assert.Equal("claude-4", record.Model); // provider/ prefix stripped
        Assert.Equal("codebuddy:s1:msg-1", record.DeduplicationID);
        Assert.Equal("MyTrace", record.Project);
    }

    [Fact]
    public void Mirrored_Writes_Fold_To_The_More_Complete_Snapshot()
    {
        var dir = TranscriptDir("codebuddy");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "s1.jsonl"),
        [
            TranscriptLine(1789981200000, input: 100, output: 10, cacheRead: 0, cacheWrite: 0, messageId: "same"),
            // Same messageId, larger counts: the more complete snapshot wins.
            TranscriptLine(1789981200000, input: 150, output: 15, cacheRead: 0, cacheWrite: 0, messageId: "same"),
        ]);
        var record = Assert.Single(TencentBuddyReader.Records("codebuddy", _home));
        Assert.Equal(150, record.Tally.Input); // not 250
    }

    [Fact]
    public void Non_Completed_Status_Is_Not_Counted()
    {
        var dir = TranscriptDir("codebuddy");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "s1.jsonl"),
        [
            TranscriptLine(1789981200000, input: 100, output: 10, cacheRead: 0, cacheWrite: 0, status: "pending"),
        ]);
        Assert.Empty(TencentBuddyReader.Records("codebuddy", _home));
    }

    [Fact]
    public void Transcript_Wins_Over_The_Extension_Log_With_Partial_Mark()
    {
        var dir = TranscriptDir("codebuddy");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "s1.jsonl"),
        [
            TranscriptLine(1789981200000, input: 100, output: 10, cacheRead: 0, cacheWrite: 0),
        ]);
        var logDir = Path.Combine(_home, ".codebuddy");
        // The extension log line would double count: transcript wins, marked partial.
        File.WriteAllLines(Path.Combine(logDir, "ws__log.log"),
        [
            "2026/09/21 10:00:00.000 [AgentReporter] [agent-a] usage: {\"inputTokens\":500,\"outputTokens\":50}",
        ]);

        var record = Assert.Single(TencentBuddyReader.Records("codebuddy", _home));
        Assert.Equal(100, record.Tally.Input); // transcript figures, not the log's 500
        Assert.True(record.IsPartial);         // the excluded fallback is real work
    }

    [Fact]
    public void Extension_Log_Stands_Alone_Without_A_Transcript()
    {
        Directory.CreateDirectory(CodebuddyLogDir);
        File.WriteAllLines(Path.Combine(CodebuddyLogDir, "ws__log.log"),
        [
            "2026/09/21 09:59:00.000 [CraftInvokableAgent] [agent-a] Model prepared: (claude-4)",
            "2026/09/21 10:00:00.000 [AgentReporter] [agent-a] usage: {\"inputTokens\":200,\"outputTokens\":20}",
        ]);

        var record = Assert.Single(TencentBuddyReader.Records("codebuddy", _home));
        Assert.Equal(200, record.Tally.Input);
        Assert.Equal(20, record.Tally.Output);
        Assert.Equal("claude-4", record.Model); // from the prepared line
        Assert.Equal("agent-a", record.SessionID);
        Assert.Equal("ws", record.Project); // workspace from the filename before __
    }
}

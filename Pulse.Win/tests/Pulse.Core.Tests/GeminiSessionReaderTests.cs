using System.Text.Json;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Gemini CLI reader tests over hand-built session JSON, chat JSON, and headless
/// JSONL in a temp tree. Locks the shape-specific cache relations: the session
/// shape proves cache overlap only through a reported total, the headless shape
/// treats a prompt-style input key (or a tokens wrapper) as cache-inclusive.
/// </summary>
public class GeminiSessionReaderTests : IDisposable
{
    private readonly string _home;

    public GeminiSessionReaderTests() => _home = Path.Combine(Path.GetTempPath(), $"pulse-gemini-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Session_Shape_With_Total_Proves_Cache_Inclusive()
    {
        // total 1100 == raw(1000) + output(100), and != that plus cache 400:
        // proven cache-inclusive, fresh input = 1000 - 400.
        var dir = Path.Combine(_home, ".gemini", "tmp", "abc", "chats");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session-x.json"),
            """{"sessionId":"s1","messages":[{"type":"gemini","model":"gemini-3-pro","id":"m1","timestamp":"2026-09-21T10:00:00Z","tokens":{"prompt":1000,"candidates":100,"cached":400,"total":1100}}]}""");

        var records = GeminiSessionReader.Records(_home);
        var record = Assert.Single(records);
        Assert.Equal(600, record.Tally.Input);
        Assert.Equal(400, record.Tally.CacheRead);
        Assert.Equal(100, record.Tally.Output);
        Assert.Equal("gemini:session:s1:m1", record.DeduplicationID);
    }

    [Fact]
    public void Session_Shape_Net_Input_Key_Is_Left_Alone()
    {
        // A bare `input` field is already the fresh count under the session
        // shape's no-total path.
        var dir = Path.Combine(_home, ".gemini", "session-y.json".Replace("session-y.json", ""));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(_home, ".gemini", "session-y.json"),
            """{"sessionId":"s2","messages":[{"type":"gemini","model":"gemini-3-pro","timestamp":"2026-09-21T10:00:00Z","tokens":{"input":500,"cached":200,"output":80}}]}""");

        var record = Assert.Single(GeminiSessionReader.Records(_home));
        Assert.Equal(500, record.Tally.Input); // net: not subtracted
        Assert.Equal(200, record.Tally.CacheRead);
        Assert.Equal(80, record.Tally.Output); // reasoning folded when present
    }

    [Fact]
    public void Headless_Tokens_Wrapper_Is_Cache_Inclusive()
    {
        // The tokens wrapper makes the input cache-inclusive: 1000 - 400 = 600.
        var dir = Path.Combine(_home, ".gemini", "tmp", "headless");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stream.jsonl"),
            """{"type":"init","model":"gemini-3-pro","session_id":"hs1"}"""
            + "\n" +
            """{"tokens":{"prompt":1000,"cached":400,"output":100},"model":"gemini-3-pro","timestamp":"2026-09-21T10:05:00Z"}""");

        var record = Assert.Single(GeminiSessionReader.Records(_home));
        Assert.Equal(600, record.Tally.Input);
        Assert.Equal(400, record.Tally.CacheRead);
        Assert.Equal("hs1", record.SessionID); // from the init line
    }

    [Fact]
    public void Headless_ReExport_Replaces_The_Original()
    {
        // A re-export of the same call (same line id) replaces its original in
        // place rather than adding a second copy.
        var dir = Path.Combine(_home, ".gemini", "tmp", "headless2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stream.jsonl"),
            """{"id":"l1","tokens":{"input":100,"output":10},"model":"m","timestamp":"2026-09-21T10:00:00Z"}"""
            + "\n" +
            """{"id":"l1","tokens":{"input":100,"output":20},"model":"m","timestamp":"2026-09-21T10:01:00Z"}""");

        var record = Assert.Single(GeminiSessionReader.Records(_home));
        Assert.Equal(20, record.Tally.Output); // replaced, not duplicated
    }

    [Fact]
    public void Chat_Path_Requires_Tmp_Id_chats_Shape()
    {
        Assert.True(GeminiSessionReader.IsChatPath("/home/.gemini/tmp/abc/chats/x.json"));
        Assert.False(GeminiSessionReader.IsChatPath("/home/.gemini/chats/x.json"));
        Assert.False(GeminiSessionReader.IsChatPath("/home/.gemini/other/x.json"));
    }

    [Fact]
    public void Session_Reasoning_Is_Additive_On_Output()
    {
        var dir = Path.Combine(_home, ".gemini", "tmp", "abc2", "chats");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "s.json"),
            """{"sessionId":"s","messages":[{"type":"gemini","model":"m","timestamp":"2026-09-21T10:00:00Z","tokens":{"input":100,"output":60,"thoughts":40}}]}""");
        var record = Assert.Single(GeminiSessionReader.Records(_home));
        Assert.Equal(100, record.Tally.Output); // 60 + 40, additive on top of output
    }

    [Fact]
    public void Usage_Without_Model_Marks_Run_Partial()
    {
        var dir = Path.Combine(_home, ".gemini", "tmp", "abc3", "chats");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "s.json"),
            """{"sessionId":"s","messages":[{"type":"gemini","timestamp":"2026-09-21T10:00:00Z","tokens":{"input":10,"output":1}}]}""");
        Assert.Empty(GeminiSessionReader.Records(_home)); // skipped, run marked partial
    }
}

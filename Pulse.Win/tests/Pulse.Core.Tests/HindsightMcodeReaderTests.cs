using System.Text.Json;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Hindsight ledger reader tests: the required dedup id, the zero-total
/// rejection, the cache-read bucket, and the operation/scope title.
/// </summary>
public class HindsightReaderTests : IDisposable
{
    private readonly string _root;

    public HindsightReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-hindsight-{Guid.NewGuid():N}", ".hindsight", "ledger");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_root))!, recursive: true); }
        catch (IOException) { }
    }

    private void WriteLedger(params string[] lines) =>
        File.WriteAllLines(Path.Combine(_root, "llm-requests.jsonl"), lines);

    [Fact]
    public void Ledger_Row_Parses_With_Bank_And_Title()
    {
        WriteLedger(
            """{"id":"h1","model":"llama-4","started_at":1789981200000,"input_tokens":100,"output_tokens":20,"cached_tokens":5,"bank":"my-bank","operation":"recall","scope":"session"}""");

        var record = Assert.Single(CapturedHindsightReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(5, record.Tally.CacheRead);
        Assert.Equal(0, record.Tally.CacheWrite); // not reported on this path
        Assert.Equal(20, record.Tally.Output);    // reasoning never split out
        Assert.Equal("my-bank", record.Project);
        Assert.Equal("recall / session", record.Title); // both present and differ
        Assert.Equal("hindsight:h1", record.DeduplicationID);
    }

    [Fact]
    public void Same_Operation_And_Scope_Collapse_Title()
    {
        WriteLedger(
            """{"id":"h1","model":"m","started_at":1789981200000,"input_tokens":1,"output_tokens":1,"operation":"op","scope":"op"}""");
        var record = Assert.Single(CapturedHindsightReader.RecordsFromRoots(new[] { _root }));
        Assert.Equal("op", record.Title);
    }

    [Fact]
    public void Zero_Total_Asserts_No_Usage()
    {
        WriteLedger(
            """{"id":"h1","model":"m","started_at":1789981200000,"input_tokens":0,"output_tokens":0,"total_tokens":0}""");
        Assert.Empty(CapturedHindsightReader.RecordsFromRoots(new[] { _root }));
    }

    [Fact]
    public void Missing_Id_Is_Skipped()
    {
        // Without the id a re-synced row cannot be folded, so it is required.
        WriteLedger(
            """{"model":"m","started_at":1789981200000,"input_tokens":10,"output_tokens":1}""");
        Assert.Empty(CapturedHindsightReader.RecordsFromRoots(new[] { _root }));
    }
}

/// <summary>
/// Mcode reader tests: per-turn buffering until exec.result names the model,
/// identity-based merge of streaming restatements, unclassified bare totals,
/// and BOM tolerance.
/// </summary>
public class McodeReaderTests : IDisposable
{
    private readonly string _root;

    public McodeReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pulse-mcode-{Guid.NewGuid():N}", ".mcode", "captures");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch (IOException) { }
    }

    private void WriteCapture(params string[] lines) =>
        File.WriteAllLines(Path.Combine(_root, "capture.jsonl"), lines);

    [Fact]
    public void Usage_Is_Buffered_Until_Exec_Result_Names_The_Model()
    {
        WriteCapture(
            """{"type":"message","message":{"role":"assistant","turnId":"t1","timestamp":1789981200000,"usage":{"inputTokens":100,"outputTokens":20}}}""",
            """{"type":"exec.result","turnId":"t1","sessionId":"sess1","model":{"providerId":"openai","modelId":"gpt-5"}}""");

        var record = Assert.Single(CapturedMcodeReader.ParseLines(
            File.ReadAllLines(Path.Combine(_root, "capture.jsonl"))));
        Assert.Equal("gpt-5", record.Model);
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal("sess1", record.SessionID);
        Assert.StartsWith("mcode:sess1:t1:0:100:20:0:0", record.DeduplicationID);
    }

    [Fact]
    public void Stream_Without_A_Model_Is_Unusable()
    {
        // Pulse refuses to guess a model from response text or local config.
        WriteCapture(
            """{"type":"message","message":{"role":"assistant","turnId":"t1","timestamp":1789981200000,"usage":{"inputTokens":100,"outputTokens":20}}}""");
        Assert.Empty(CapturedMcodeReader.ParseLines(
            File.ReadAllLines(Path.Combine(_root, "capture.jsonl"))));
    }

    [Fact]
    public void Same_Id_Restatement_Replaces_Earlier_Entry()
    {
        // The streaming/terminal restatement of one message replaces it; the
        // larger counts are the same message, not a second one.
        WriteCapture(
            """{"type":"message","message":{"role":"assistant","turnId":"t1","id":"msg-1","timestamp":1789981200000,"usage":{"inputTokens":100,"outputTokens":5}}}""",
            """{"type":"message","message":{"role":"assistant","turnId":"t1","id":"msg-1","timestamp":1789981200000,"usage":{"inputTokens":100,"outputTokens":25}}}""",
            """{"type":"exec.result","turnId":"t1","sessionId":"sess1","model":{"providerId":"openai","modelId":"gpt-5"}}""");

        var record = Assert.Single(CapturedMcodeReader.ParseLines(
            File.ReadAllLines(Path.Combine(_root, "capture.jsonl"))));
        Assert.Equal(25, record.Tally.Output); // replaced, not 30
    }

    [Fact]
    public void Id_Less_Lines_Are_Both_Kept()
    {
        // Counting alone never makes two messages one.
        WriteCapture(
            """{"type":"message","message":{"role":"assistant","turnId":"t1","timestamp":1789981200000,"usage":{"inputTokens":100,"outputTokens":5}}}""",
            """{"type":"message","message":{"role":"assistant","turnId":"t1","timestamp":1789981200000,"usage":{"inputTokens":100,"outputTokens":25}}}""",
            """{"type":"exec.result","turnId":"t1","sessionId":"sess1","model":{"providerId":"openai","modelId":"gpt-5"}}""");

        Assert.Equal(2, CapturedMcodeReader.ParseLines(
            File.ReadAllLines(Path.Combine(_root, "capture.jsonl"))).Count);
    }

    [Fact]
    public void Bare_Total_Is_Unclassified()
    {
        WriteCapture(
            """{"type":"message","message":{"role":"assistant","turnId":"t1","timestamp":1789981200000,"usage":{"totalTokens":777}}}""",
            """{"type":"exec.result","turnId":"t1","sessionId":"sess1","model":{"providerId":"x","modelId":"m"}}""");

        var record = Assert.Single(CapturedMcodeReader.ParseLines(
            File.ReadAllLines(Path.Combine(_root, "capture.jsonl"))));
        Assert.Equal(777, record.UnclassifiedTokens);
        Assert.Equal(0, record.Tally.Total);
    }

    [Fact]
    public void Bom_Prefixed_Line_Still_Parses()
    {
        var capture = Path.Combine(_root, "capture.jsonl");
        // A capture that appended one malformed line must still yield its other
        // turns; the BOM on the first line does not lose the rest.
        File.WriteAllBytes(capture, System.Text.Encoding.UTF8.GetBytes(
            "\uFEFF" + """{"type":"message","message":{"role":"assistant","turnId":"t1","timestamp":1789981200000,"usage":{"inputTokens":10,"outputTokens":1}}}""" + "\n" +
            """{"type":"exec.result","turnId":"t1","sessionId":"sess1","model":{"providerId":"x","modelId":"m"}}"""));

        var record = Assert.Single(CapturedMcodeReader.ParseLines(
            File.ReadAllLines(capture)));
        Assert.Equal(10, record.Tally.Input);
    }
}

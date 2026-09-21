using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// VS Code task log reader tests: the api_req_started extraction (four kinds
/// under a text field that is itself JSON), the modelInfo→history-model
/// fallback, and the environment_details tag parsing.
/// </summary>
public class VsCodeTaskLogReaderTests : IDisposable
{
    private readonly string _home;

    public VsCodeTaskLogReaderTests() => _home = Path.Combine(Path.GetTempPath(), $"pulse-vscode-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string WriteTask(string client, string session, string uiMessages, string? history = null)
    {
        var task = Path.Combine(_home, "AppData", "Roaming", "Code", "User", "globalStorage",
            client switch
            {
                "roocode" => "rooveterinaryinc.roo-cline",
                "kilocode" => "kilocode.kilo-code",
                _ => "saoudrizwan.claude-dev",
            }, "tasks", session);
        Directory.CreateDirectory(task);
        File.WriteAllText(Path.Combine(task, "ui_messages.json"), uiMessages);
        if (history is not null)
            File.WriteAllText(Path.Combine(task, "api_conversation_history.json"), history);
        return client;
    }

    /// <summary>Builds one api_req_started entry; the four kinds live under a
    /// text field that is itself JSON.</summary>
    private static string Entry(long ts, int tokensIn, int tokensOut, string? modelId = null)
    {
        var entry = string.Concat(
            "{\"type\":\"say\",\"say\":\"api_req_started\",\"ts\":", ts.ToString(),
            ",\"text\":\"{\\\"tokensIn\\\":", tokensIn.ToString(),
            ",\\\"tokensOut\\\":", tokensOut.ToString(), "}\"");
        if (modelId is not null)
            entry += string.Concat(",\"modelInfo\":{\"modelId\":\"", modelId, "\"}");
        entry += "}";
        return entry;
    }

    private static string EntryUnparseableTs(int tokensIn, int tokensOut)
    {
        return string.Concat(
            "{\"type\":\"say\",\"say\":\"api_req_started\",\"ts\":\"not-a-date\"",
            ",\"text\":\"{\\\"tokensIn\\\":", tokensIn.ToString(),
            ",\\\"tokensOut\\\":", tokensOut.ToString(), "}\"}");
    }

    [Fact]
    public void Api_Req_Started_Entries_Are_Counted()
    {
        var client = WriteTask("cline", "task-1",
            "[" + Entry(1789981200000, 100, 20, modelId: "claude-sonnet-4-5") + "]");

        var record = Assert.Single(VsCodeTaskLogReader.Records(client, _home));
        Assert.Equal(100, record.Tally.Input);
        Assert.Equal(20, record.Tally.Output);
        Assert.Equal("claude-sonnet-4-5", record.Model);
        Assert.Equal("task-1", record.SessionID);
    }

    [Fact]
    public void Model_Falls_Back_To_The_History_Model_Tag()
    {
        var client = WriteTask("roocode", "task-2",
            "[" + Entry(1789981200000, 10, 1) + "]",
            history: "<environment_details><slug>architect</slug><model>gemini-3-pro</model></environment_details>");

        var record = Assert.Single(VsCodeTaskLogReader.Records(client, _home));
        Assert.Equal("gemini-3-pro", record.Model);
        Assert.Equal("architect", record.SessionName); // slug names the agent
    }

    [Fact]
    public void Zero_And_Unparseable_Ts_Are_Skipped()
    {
        // ts=0 is "unset" (zero is never a measured date) and "not-a-date"
        // cannot parse: both entries are skipped, only the third is a reading.
        var client = WriteTask("kilocode", "task-3",
            "[" + Entry(0, 9, 1) + "," + EntryUnparseableTs(9, 1) + "," + Entry(1789981200000, 5, 1) + "]",
            history: "<environment_details><model>kilo-model</model></environment_details>");

        var record = Assert.Single(VsCodeTaskLogReader.Records(client, _home));
        Assert.Equal(5, record.Tally.Input);
        Assert.Equal("kilo-model", record.Model); // from the history fallback
    }

    [Fact]
    public void Other_Say_Types_Are_Ignored()
    {
        var client = WriteTask("cline", "task-4",
            """[{"type":"say","say":"text","ts":1789981200000,"text":"{"tokensIn":99,"tokensOut":9}"}]""");
        Assert.Empty(VsCodeTaskLogReader.Records(client, _home));
    }

    [Fact]
    public void Inputs_Names_Roots_Whether_Or_Not_They_Exist()
    {
        var roots = VsCodeTaskLogReader.Inputs("cline", _home);
        // Desktop + .config spellings across three editors, plus remote servers.
        Assert.True(roots.Count >= 8);
        Assert.Contains(roots, r => r.Contains("saoudrizwan.claude-dev"));
        Assert.Contains(roots, r => r.Contains(".vscode-server"));
    }
}

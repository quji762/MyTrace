using System.Text.Json;
using Pulse.Core.ClaudeHook;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Status-line hook contract tests over hand-built payloads; nothing here reads
/// the user's real ~/.claude (paths are always injected).
/// </summary>
public class StatusLineHookTests : IDisposable
{
    private readonly string _home;

    public StatusLineHookTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"pulse-hook-{Guid.NewGuid():N}");
        // The hook itself creates .claude on write; tests that pre-seed files need it first.
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string Settings => StatusLinePaths.SettingsFile(_home);

    [Fact]
    public void RunAsStatusLine_Banks_The_Rate_Limits_Atomic()
    {
        const string payload = """
            {"model":{"display_name":"Opus"},"rate_limits":{"five_hour":{"used_percentage":42,"resets_at":"2026-09-21T18:00:00Z"},"seven_day":{"used_percentage":12}}}
            """;
        var line = StatusLineCapture.RunAsStatusLine(payload, _home);

        Assert.NotNull(line);
        Assert.Contains("5h 42%", line!);
        Assert.Contains("7d 12%", line);
        Assert.Contains("Opus", line);

        var captured = StatusLineCapture.Read(_home);
        Assert.NotNull(captured);
        Assert.Equal(42, captured!.FiveHourPercent);
        Assert.Equal(12, captured.SevenDayPercent);
        Assert.NotNull(captured.FiveHourReset);
    }

    [Fact]
    public void Epoch_Leak_Is_Not_A_Percentage()
    {
        // Claude Code has shipped builds where used_percentage leaked the epoch
        // reset time; anything past 101 must be dropped, not drawn as a full ring.
        Assert.Null(StatusLineCapture.PercentOf(Json.Number(1789085506)));
        Assert.Equal(42.0, StatusLineCapture.PercentOf(Json.Number(42)));
        Assert.Null(StatusLineCapture.PercentOf(Json.Number(-1)));
        Assert.Equal(101.0, StatusLineCapture.PercentOf(Json.Number(101))); // boundary kept
    }

    [Fact]
    public void Garbage_Payload_Is_Swallowed_Status_Line_Stays_Quiet()
    {
        // Runs inside someone's editor: an error or a hang is far worse than
        // silence.
        Assert.Null(StatusLineCapture.RunAsStatusLine("not json", _home));
        Assert.Null(StatusLineCapture.RunAsStatusLine("", _home));
        Assert.Null(StatusLineCapture.RunAsStatusLine("null", _home));
    }

    [Fact]
    public void Install_Registers_And_Uninstall_Restores()
    {
        Assert.False(StatusLineInstaller.IsInstalled(_home));

        Assert.True(StatusLineInstaller.Install("C:\\Apps\\PulseWin\\Pulse.App.exe", _home));
        Assert.True(StatusLineInstaller.IsInstalled(_home));
        var settings = JsonDocument.Parse(File.ReadAllText(Settings));
        var command = settings.RootElement.GetProperty("statusLine").GetProperty("command").GetString()!;
        Assert.EndsWith("--statusline", command);
        Assert.Contains("Pulse.App.exe", command);

        // Uninstall with nothing remembered removes the entry entirely.
        Assert.True(StatusLineInstaller.Uninstall(_home));
        Assert.False(StatusLineInstaller.IsInstalled(_home));
        Assert.False(JsonDocument.Parse(File.ReadAllText(Settings)).RootElement.TryGetProperty("statusLine", out _));
    }

    [Fact]
    public void Install_Remembers_And_Uninstall_Restores_A_Previous_Command()
    {
        File.WriteAllText(Settings,
            """{"statusLine":{"type":"command","command":"starship prompt"},"model":"opus"}""");

        Assert.True(StatusLineInstaller.Install("C:\\Apps\\PulseWin\\Pulse.App.exe", _home));
        Assert.True(StatusLineInstaller.IsInstalled(_home));

        Assert.True(StatusLineInstaller.Uninstall(_home));
        var restored = JsonDocument.Parse(File.ReadAllText(Settings)).RootElement;
        Assert.Equal("starship prompt", restored.GetProperty("statusLine").GetProperty("command").GetString());
        // The rest of the file survived.
        Assert.Equal("opus", restored.GetProperty("model").GetString());
    }

    [Fact]
    public void Unreadable_Settings_Are_Never_Overwritten()
    {
        // A file that exists but will not parse must be left alone: replacing it
        // wholesale would trade every setting in it for a status line.
        File.WriteAllText(Settings, "{ this is not json, with a comment }");
        var before = File.ReadAllText(Settings);

        Assert.False(StatusLineInstaller.Install("C:\\x.exe", _home));
        Assert.Equal(before, File.ReadAllText(Settings));
    }

    [Fact]
    public void Install_Backups_The_Original_Once()
    {
        File.WriteAllText(Settings, """{"model":"opus"}""");
        StatusLineInstaller.Install("C:\\x.exe", _home);

        var backup = Settings + ".pulse-backup";
        Assert.True(File.Exists(backup));
        Assert.Contains("opus", File.ReadAllText(backup));
    }

    [Fact]
    public void Absent_Settings_File_Is_Created()
    {
        // An absent file is an empty document: nothing to lose by creating one.
        Assert.True(StatusLineInstaller.Install("C:\\x.exe", _home));
        Assert.True(StatusLineInstaller.IsInstalled(_home));
    }

    [Fact]
    public void Read_Missing_Cache_Is_Null()
    {
        Assert.Null(StatusLineCapture.Read(_home));
    }

    [Fact]
    public void Corrupt_Cache_Reads_As_Null()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(StatusLinePaths.CacheFile(_home), "{ half written");
        Assert.Null(StatusLineCapture.Read(_home));
    }

    private static class Json
    {
        public static System.Text.Json.JsonElement Number(double value) =>
            System.Text.Json.JsonSerializer.SerializeToElement(value);

        public static System.Text.Json.JsonElement String(string value) =>
            System.Text.Json.JsonSerializer.SerializeToElement(value);
    }
}

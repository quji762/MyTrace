using System.IO;
using System.Text.Json;

namespace Pulse.App.Settings;

/// <summary>
/// Window/rail position and docked-edge preferences, persisted per the upstream
/// rule: monitor identity + normalized offset, never absolute pixels — so a
/// resolution or monitor change cannot fling the rail off-screen.
/// </summary>
public sealed class RailPreferences
{
    public string? MonitorDeviceName { get; set; }
    public double NormalizedX { get; set; } = 1.0; // default: right edge
    public double NormalizedY { get; set; } = 0.5;
    public string DockedEdge { get; set; } = "right";
    public bool AutoCollapse { get; set; } = true;
    public string Theme { get; set; } = "system";

    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseWin", "rail-prefs.json");

    public static RailPreferences Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<RailPreferences>(File.ReadAllText(Path)) ?? new RailPreferences();
        }
        catch (Exception) { }
        return new RailPreferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            Pulse.Core.Platform.SecureState.WriteStateText(Path, JsonSerializer.Serialize(this));
        }
        catch (Exception) { }
    }
}

using static Pulse.Core.Platform.SecureState;

namespace Pulse.Core.Platform;

/// <summary>Whether the global toggle hotkey is on. Stored beside the other
/// PulseWin settings; default on so the rail is reachable without the tray.</summary>
public static class HotkeyPreferences
{
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "hotkey.txt");

    public static bool Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            if (!File.Exists(file)) return true;
            return !string.Equals(File.ReadAllText(file).Trim(), "off", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static void Save(bool enabled, string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            WriteStateText(file, enabled ? "on" : "off");
        }
        catch (Exception)
        {
        }
    }
}

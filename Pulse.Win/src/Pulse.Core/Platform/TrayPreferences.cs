namespace Pulse.Core.Platform;

/// <summary>
/// Whether the system tray icon is hidden. Off by default so the tray entry
/// point is preserved. Safety invariant: Pulse always keeps at least one
/// visible entry point (tray icon, rail, or global hotkey). Hiding the tray
/// is refused when no other entry point remains.
/// </summary>
public static class TrayPreferences
{
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "hide-tray.txt");

    /// <summary>True when the user chose to hide the tray icon.</summary>
    public static bool Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            if (!File.Exists(file)) return false;
            return string.Equals(File.ReadAllText(file).Trim(), "on", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Save(bool hidden, string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, hidden ? "on" : "off");
        }
        catch (Exception)
        {
        }
    }
}

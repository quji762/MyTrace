using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Pulse.App.Settings;

/// <summary>
/// Light/Dark/System theme, following the OS setting when System is chosen
/// (upstream Pulse has the same three-way choice). Swaps the token
/// ResourceDictionary the rail and settings read their colors from.
/// </summary>
public static class ThemeManager
{
    public enum Theme { Light, Dark, System }

    public static event Action<Theme>? ThemeChanged;

    private static Theme _current = Load();

    public static Theme Current => _current;

    public static void Apply(Theme theme)
    {
        _current = theme;
        Save(theme);
        var palette = EffectivePalette(theme);
        SwapPalette(palette);
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>Register the OS-light/dark change while running (System mode).</summary>
    public static void WatchSystem()
    {
        try
        {
            SystemParameters.StaticPropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "WindowGlassColor" or "WindowGlassBrush")
                {
                    // Cheap proxy: re-resolve on display settings churn.
                    if (_current == Theme.System)
                        SwapPalette(EffectivePalette(Theme.System));
                }
            };
        }
        catch (Exception) { }
    }

    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch (Exception)
        {
            return false; // unreadable: keep dark, the rail's native look
        }
    }

    private static IReadOnlyDictionary<string, string> EffectivePalette(Theme theme)
    {
        var dark = theme switch
        {
            Theme.Light => false,
            Theme.System => !SystemPrefersLight(),
            _ => true,
        };
        return dark ? DarkPalette : LightPalette;
    }

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["RailBackground"] = "#E8141414",
        ["RailForeground"] = "#FFF2F2F2",
        ["RingTrack"] = "#33FFFFFF",
        ["SettingsBackground"] = "#FF1E1E1E",
        ["CardBackground"] = "#FF2A2A2A",
        ["MutedForeground"] = "#FF9E9E9E",
    };

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["RailBackground"] = "#E8FAFAFA",
        ["RailForeground"] = "#FF1A1A1A",
        ["RingTrack"] = "#33000000",
        ["SettingsBackground"] = "#FFF3F3F3",
        ["CardBackground"] = "#FFFFFFFF",
        ["MutedForeground"] = "#FF6B6B6B",
    };

    private static void SwapPalette(IReadOnlyDictionary<string, string> palette)
    {
        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null) return;

        foreach (var (key, color) in palette)
        {
            var brush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
            brush.Freeze();
            if (resources.Contains(key))
                resources[key] = brush;
            else
                resources.Add(key, brush);
        }
    }

    private static readonly string PrefsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "theme.txt");

    private static Theme Load()
    {
        try
        {
            if (File.Exists(PrefsPath) &&
                Enum.TryParse(File.ReadAllText(PrefsPath), out Theme theme))
                return theme;
        }
        catch (Exception) { }
        return Theme.Dark;
    }

    private static void Save(Theme theme)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PrefsPath)!);
            File.WriteAllText(PrefsPath, theme.ToString());
        }
        catch (Exception) { }
    }
}

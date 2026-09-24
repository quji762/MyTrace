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
        ["RailBackground"] = "#F00C0C0F",
        ["RailForeground"] = "#FFF0F0F2",
        ["RingTrack"] = "#28FFFFFF",
        ["SettingsBackground"] = "#FF0E0E11",
        ["SidebarBackground"] = "#FF0A0A0C",
        ["CardBackground"] = "#FF18181C",
        ["CardBorder"] = "#FF2A2A30",
        ["ControlBackground"] = "#FF1E1E24",
        ["ControlBorder"] = "#FF36363E",
        ["MutedForeground"] = "#FF9E9EA8",
        ["SubtleForeground"] = "#FF6C6C78",
        ["HoverOverlay"] = "#12FFFFFF",
        ["PressedOverlay"] = "#20000000",
        ["FocusRing"] = "#885B8CFF",
        ["Divider"] = "#1AFFFFFF",
        ["AccentBrush"] = "#FF5B8CFF",
        ["AccentSubtle"] = "#1A5B8CFF",
        ["SuccessBrush"] = "#FF34D399",
        ["WarningBrush"] = "#FFFBBF24",
        ["DangerBrush"] = "#FFF87171",
    };

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["RailBackground"] = "#F0F8F8FA",
        ["RailForeground"] = "#FF1A1A22",
        ["RingTrack"] = "#22000000",
        ["SettingsBackground"] = "#FFF4F4F7",
        ["SidebarBackground"] = "#FFECECF0",
        ["CardBackground"] = "#FFFFFFFF",
        ["CardBorder"] = "#FFE0E0E8",
        ["ControlBackground"] = "#FFF7F7FA",
        ["ControlBorder"] = "#FFD2D2DC",
        ["MutedForeground"] = "#FF6E6E7A",
        ["SubtleForeground"] = "#FF8E8E9A",
        ["HoverOverlay"] = "#0C000000",
        ["PressedOverlay"] = "#14000000",
        ["FocusRing"] = "#665B8CFF",
        ["Divider"] = "#14000000",
        ["AccentBrush"] = "#FF4A78E8",
        ["AccentSubtle"] = "#144A78E8",
        ["SuccessBrush"] = "#FF0D9F6E",
        ["WarningBrush"] = "#FFD97706",
        ["DangerBrush"] = "#FFDC2626",
    };

    private static void SwapPalette(IReadOnlyDictionary<string, string> palette)
    {
        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null) return;

        void Apply(System.Windows.ResourceDictionary dictionary)
        {
            foreach (var (key, color) in palette)
            {
                var brush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
                brush.Freeze();
                if (dictionary.Contains(key))
                    dictionary[key] = brush;
                else
                    dictionary.Add(key, brush);
            }
        }

        Apply(resources);
        // Windows that merged PulseStyles own their brush keys; retarget those too.
        var app = System.Windows.Application.Current;
        if (app is null) return;
        foreach (System.Windows.Window window in app.Windows)
        {
            Apply(window.Resources);
            foreach (var merged in window.Resources.MergedDictionaries)
                Apply(merged);
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

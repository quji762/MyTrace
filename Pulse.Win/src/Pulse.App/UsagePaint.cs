using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Pulse.Core.Providers;

namespace Pulse.App;

/// <summary>
/// The rail's colour language, the same one the Mac panel uses.
/// Green, amber, and red mean how close a limit is. They are not brand colours.
/// </summary>
public static class UsagePaint
{
    public const double Caution = 0.5;
    public const double Warning = 0.75;

    public static readonly Color Good = Color.FromRgb(0x34, 0xD3, 0x99);
    public static readonly Color Amber = Color.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly Color WarningRed = Color.FromRgb(0xF8, 0x71, 0x71);
    public static readonly Color Spent = Color.FromRgb(0xE6, 0x39, 0x46);

    public static Color For(double usedFraction, bool exhausted, double warningAt = Warning)
    {
        if (exhausted || usedFraction >= 1) return Spent;
        if (usedFraction < Caution) return Good;
        if (usedFraction < warningAt) return Amber;
        return WarningRed;
    }

    public static SolidColorBrush BrushFor(double usedFraction, bool exhausted, double warningAt = Warning)
    {
        var brush = new SolidColorBrush(For(usedFraction, exhausted, warningAt));
        brush.Freeze();
        return brush;
    }
}

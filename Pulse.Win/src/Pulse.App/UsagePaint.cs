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

    public static readonly Color Good = Color.FromRgb(0x00, 0xE6, 0x8C);
    public static readonly Color Amber = Color.FromRgb(0xFF, 0xC2, 0x26);
    public static readonly Color WarningRed = Color.FromRgb(0xFF, 0x4F, 0x42);
    public static readonly Color Spent = Color.FromRgb(0xD9, 0x17, 0x21);

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

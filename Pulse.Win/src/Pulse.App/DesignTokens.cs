using System.Windows;
using System.Windows.Media;

namespace Pulse.App;

/// <summary>
/// One place for the rail's measurements and type scale. Colour lives in
/// ThemeManager / UsagePaint (it changes with theme and with usage); geometry
/// does not. 4px rhythm, 12px card radius, type that reads at ring size.
/// </summary>
public static class DesignTokens
{
    // --- spacing (4px grid) ---
    public const double Space1 = 4;
    public const double Space2 = 8;
    public const double Space3 = 12;
    public const double Space4 = 16;
    public const double Space5 = 20;
    public const double Space6 = 24;

    // --- radii ---
    public const double RadiusControl = 6;
    public const double RadiusCard = 12;
    public const double RadiusPill = 999;

    // --- type ---
    public const double TypeCaption = 11.5;
    public const double TypeBody = 13;
    public const double TypeBodyStrong = 13;
    public const double TypeTitle = 14;
    public const double TypeSection = 15;
    public const double TypePercent = 12;
    public const double TypePercentAlert = 12;

    public static System.Windows.Media.FontFamily UiFont { get; } =
        new("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI, sans-serif");

    // --- ring ---
    public const double RingItemHeight = 56;
    public const double RingDiameter = 40;
    public const double RingStroke = 3.5;
    public const double RingTrackStroke = 3;
    public const double RailWidth = 64;
    public const double RailCollapsedWidth = 10;
    public const double RingGap = 18;

    // --- motion (ms) ---
    public const int MotionQuick = 120;
    public const int MotionBase = 200;
    public const int MotionSlow = 320;
}

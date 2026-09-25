namespace Pulse.App;

/// <summary>
/// Where a dragged rail settles, as pure geometry so tests can pin the rules:
/// the rail stays where it was dropped — anywhere on the screen, not only at
/// an edge — clamped fully inside the monitor's work area. While dragging, the
/// center alone must stay inside, so the containing area can follow the rail
/// across displays. The nearest edge is recorded only to steer which side the
/// hover card opens on.
/// </summary>
public static class RailPlacement
{
    public static (string Edge, double Left, double Top) Settle(
        double left, double top, double width, double height,
        double areaLeft, double areaTop, double areaWidth, double areaHeight)
    {
        var center = left + width / 2;
        var edge = center - areaLeft <= areaLeft + areaWidth - center ? "left" : "right";

        // A rail wider or taller than the work area parks at its top-left.
        var maxLeft = Math.Max(areaLeft, areaLeft + areaWidth - width);
        var maxTop = Math.Max(areaTop, areaTop + areaHeight - height);
        return (edge, Math.Clamp(left, areaLeft, maxLeft), Math.Clamp(top, areaTop, maxTop));
    }

    /// <summary>Live drag: the window's center may straddle the work-area edge
    /// (it decides which monitor contains the rail), but no further.</summary>
    public static (double Left, double Top) KeepCenterInside(
        double left, double top, double width, double height,
        double areaLeft, double areaTop, double areaWidth, double areaHeight)
    {
        var minLeft = areaLeft - width / 2;
        var maxLeft = areaLeft + areaWidth - width / 2;
        var minTop = areaTop - height / 2;
        var maxTop = areaTop + areaHeight - height / 2;
        return (Math.Clamp(left, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop));
    }
}

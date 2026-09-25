namespace Pulse.App;

/// <summary>
/// Where a dragged rail settles, as pure geometry so tests can pin the rules:
/// the drop docks to the NEARER vertical edge — a mid-screen drop snaps there
/// instead of silently snapping back on the next expand — and the height is
/// kept, clamped so the rail cannot be parked off the monitor. The snap rules
/// inherit the upstream placement principle: monitor identity + normalized
/// offset, never absolute pixels.
/// </summary>
public static class RailPlacement
{
    /// <summary>How close a live drag must pass to an edge for it to catch.</summary>
    public const double SnapThreshold = 24;

    /// <summary>Live drag: magnetic snap near the work-area edges, free elsewhere.</summary>
    public static double SnapAxis(double position, double size, double areaStart, double areaSize,
        double threshold = SnapThreshold)
    {
        var areaEnd = areaStart + areaSize;
        if (position - areaStart < threshold) return areaStart;
        if (areaEnd - (position + size) < threshold) return areaEnd - size;
        return position;
    }

    /// <summary>
    /// On release: the nearer edge wins, and the top is clamped so the rail
    /// lands fully inside the work area — the same rule the restore path
    /// applies, so a dragged position and a restored one always agree.
    /// </summary>
    public static (string Edge, double Left, double Top) Settle(
        double left, double top, double width, double height,
        double areaLeft, double areaTop, double areaWidth, double areaHeight)
    {
        var center = left + width / 2;
        var edge = center - areaLeft <= areaLeft + areaWidth - center ? "left" : "right";
        var dockedLeft = edge == "left" ? areaLeft : areaLeft + areaWidth - width;

        // A rail taller than the work area parks at the top.
        var lowestTop = Math.Max(areaTop, areaTop + areaHeight - height);
        var settledTop = Math.Clamp(top, areaTop, lowestTop);
        return (edge, dockedLeft, settledTop);
    }
}

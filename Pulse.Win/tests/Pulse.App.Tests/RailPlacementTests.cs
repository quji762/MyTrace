using Xunit;

namespace Pulse.App.Tests;

/// <summary>
/// Placement rules for dragging the rail by hand: the drop docks to the
/// nearer vertical edge, the height is kept, and the rail can never be parked
/// outside the monitor's work area.
/// </summary>
public class RailPlacementTests
{
    private const double AreaLeft = 0;
    private const double AreaTop = 0;
    private const double AreaWidth = 1920;
    private const double AreaHeight = 1040;

    [Fact]
    public void Drop_Nearer_The_Left_Edge_Docks_Left_And_Keeps_The_Height()
    {
        var (edge, left, top) = RailPlacement.Settle(100, 300, 64, 400, AreaLeft, AreaTop, AreaWidth, AreaHeight);

        Assert.Equal("left", edge);
        Assert.Equal(AreaLeft, left);
        Assert.Equal(300, top); // a mid-edge drop is a vertical position, not a snap
    }

    [Fact]
    public void Drop_Nearer_The_Right_Edge_Docks_Right()
    {
        var (edge, left, _) = RailPlacement.Settle(1500, 300, 64, 400, AreaLeft, AreaTop, AreaWidth, AreaHeight);

        Assert.Equal("right", edge);
        Assert.Equal(AreaWidth - 64, left);
    }

    [Fact]
    public void A_Mid_Screen_Drop_Settles_On_The_Nearer_Edge_Instead_Of_Snapping_Back()
    {
        // The old rule only switched edges within 24px, so a mid-screen drop
        // kept the stale edge and the next expand pulled the rail back — the
        // drag never stuck. The center decides now.
        var (edge, left, _) = RailPlacement.Settle(1200, 300, 64, 400, AreaLeft, AreaTop, AreaWidth, AreaHeight);

        Assert.Equal("right", edge); // center 1232: 1232 from the left, 688 from the right
        Assert.Equal(AreaWidth - 64, left);
    }

    [Fact]
    public void The_Height_Is_Clamped_Inside_The_Work_Area()
    {
        // Dragged far past the bottom: it lands fully inside, flush with the
        // bottom edge — the same rule the restore path applies.
        var (_, _, bottom) = RailPlacement.Settle(0, 2000, 64, 400, AreaLeft, AreaTop, AreaWidth, AreaHeight);
        Assert.Equal(AreaHeight - 400, bottom);

        // Dragged above the top: it stops at the top edge.
        var (_, _, above) = RailPlacement.Settle(0, -300, 64, 400, AreaLeft, AreaTop, AreaWidth, AreaHeight);
        Assert.Equal(AreaTop, above);
    }

    [Fact]
    public void Magnetic_Snap_Catches_Near_Edges_During_The_Drag()
    {
        Assert.Equal(0, RailPlacement.SnapAxis(10, 64, 0, AreaWidth));
        Assert.Equal(AreaWidth - 64, RailPlacement.SnapAxis(AreaWidth - 70, 64, 0, AreaWidth));
        Assert.Equal(500, RailPlacement.SnapAxis(500, 64, 0, AreaWidth)); // mid-drag stays free
        Assert.Equal(0, RailPlacement.SnapAxis(-30, 64, 0, AreaWidth)); // pulled past the edge
        Assert.Equal(AreaHeight - 88, RailPlacement.SnapAxis(2000, 88, 0, AreaHeight)); // Y axis too
    }
}

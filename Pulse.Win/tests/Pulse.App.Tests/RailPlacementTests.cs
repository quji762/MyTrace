using Xunit;

namespace Pulse.App.Tests;

/// <summary>
/// Placement rules for dragging the rail by hand: it stays where it was
/// dropped — anywhere on the screen — clamped fully inside the monitor's work
/// area, with no edge docking. The nearest edge is recorded for the hover
/// card's side only.
/// </summary>
public class RailPlacementTests
{
    private const double AreaLeft = 0;
    private const double AreaTop = 0;
    private const double AreaWidth = 1920;
    private const double AreaHeight = 1040;

    [Fact]
    public void A_Mid_Screen_Drop_Stays_Where_Dropped()
    {
        var (edge, left, top) = RailPlacement.Settle(1200, 300, 64, 400, AreaLeft, AreaTop, AreaWidth, AreaHeight);

        Assert.Equal(1200, left); // free placement: no edge docking
        Assert.Equal(300, top);
        Assert.Equal("right", edge); // recorded for the hover card's side only
    }

    [Fact]
    public void A_Drop_Near_An_Edge_Is_Not_Pulled_OnTo_It()
    {
        var (_, left, _) = RailPlacement.Settle(100, 300, 64, 400, AreaLeft, AreaTop, AreaWidth, AreaHeight);

        Assert.Equal(100, left);
    }

    [Fact]
    public void The_Rail_Is_Clamped_Fully_Inside_The_Work_Area()
    {
        var (_, _, bottom) = RailPlacement.Settle(0, 2000, 64, 400, AreaLeft, AreaTop, AreaWidth, AreaHeight);
        Assert.Equal(AreaHeight - 400, bottom);

        var (_, _, above) = RailPlacement.Settle(0, -300, 64, 400, AreaLeft, AreaTop, AreaWidth, AreaHeight);
        Assert.Equal(AreaTop, above);

        var (_, left, _) = RailPlacement.Settle(5000, 300, 64, 400, AreaLeft, AreaTop, AreaWidth, AreaHeight);
        Assert.Equal(AreaWidth - 64, left);
    }

    [Fact]
    public void While_Dragging_The_Center_May_Straddle_The_Work_Area_Edge()
    {
        // The center decides which monitor contains the rail, so the body may
        // cross the edge until the center does — that is how the drag reaches
        // another display.
        var (left, top) = RailPlacement.KeepCenterInside(1930, -200, 64, 400, AreaLeft, AreaTop, AreaWidth, AreaHeight);

        Assert.Equal(AreaWidth - 32, left); // center exactly on the right edge
        Assert.Equal(-200, top);            // center still inside vertically
    }
}

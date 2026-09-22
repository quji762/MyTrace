using System.Drawing;
using Pulse.App;
using Pulse.App.Tray;
using Xunit;

namespace Pulse.App.Tests;

public class TrayIconTests
{
    [Fact]
    public void Tray_Icon_Still_Has_Pixels_After_It_Is_Built()
    {
        using var icon = NotifyIconTray.CreateIcon();
        using var bitmap = icon.ToBitmap();
        var ink = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A > 0) ink++;
            }
        }

        Assert.True(ink > 20, $"icon drew {ink} opaque pixels");
    }

    [Fact]
    public void A_150_Percent_Screen_Keeps_The_Rail_On_The_Glass()
    {
        const uint dpi = 144;
        var right = MonitorWorkArea.PixelsToDip(2560, dpi);
        var left = right - 64;
        var leftPixels = left * dpi / 96.0;
        Assert.InRange(leftPixels, 0, 2560);
        Assert.True(leftPixels + 64 * dpi / 96.0 <= 2560 + 1);
    }
}

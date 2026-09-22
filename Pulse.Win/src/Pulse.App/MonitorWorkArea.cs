using System.Drawing;
using System.Windows.Forms;

namespace Pulse.App;

/// <summary>
/// A monitor's working area in device-independent pixels, identified by
/// <see cref="Screen.DeviceName"/>. Positions are stored against that identity
/// so a secondary monitor does not collapse back onto the primary.
/// </summary>
internal static class MonitorWorkArea
{
    public readonly record struct DipRect(string DeviceName, double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    public static DipRect ForDevice(string? deviceName, uint dpi)
    {
        var screen = Screen.AllScreens.FirstOrDefault(candidate => candidate.DeviceName == deviceName)
                     ?? Screen.PrimaryScreen
                     ?? Screen.AllScreens.FirstOrDefault();
        return screen is null ? new DipRect("", 0, 0, 1280, 800) : Of(screen, dpi);
    }

    public static DipRect Containing(double pixelX, double pixelY, uint dpi)
    {
        var screen = Screen.FromPoint(new Point((int)pixelX, (int)pixelY));
        return Of(screen, dpi);
    }

    /// <summary>
    /// Screen pixels to the DIPs WPF uses for Left/Top. A 150% monitor is 144
    /// DPI: dividing a 2560px edge by 1 keeps the rail past the glass.
    /// </summary>
    public static double PixelsToDip(double pixels, uint dpi) =>
        dpi >= 96 ? pixels * 96.0 / dpi : pixels;

    private static DipRect Of(Screen screen, uint dpi)
    {
        var area = screen.WorkingArea;
        if (dpi < 96) dpi = 96;
        return new DipRect(
            screen.DeviceName,
            PixelsToDip(area.Left, dpi),
            PixelsToDip(area.Top, dpi),
            PixelsToDip(area.Width, dpi),
            PixelsToDip(area.Height, dpi));
    }

}

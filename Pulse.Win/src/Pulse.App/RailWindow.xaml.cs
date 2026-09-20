using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;

namespace Pulse.App;

/// <summary>
/// The Rail: a borderless, topmost vertical strip docked to a screen edge.
/// MVP scope: drag to reposition with edge snapping, ring per provider,
/// percentage + reset text. Per-monitor DPI refinement lands in W6 per plan.
/// </summary>
public partial class RailWindow : Window
{
    private const double SnapThreshold = 24;

    private readonly Dictionary<ProviderId, RingControl> _rings = new();
    private System.Windows.Point _dragOffset;
    private bool _dragging;

    public RailWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        SourceInitialized += (_, _) => ApplyPerMonitorV2();
    }

    public void ShowAndRestore()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Update one provider's ring from a reading. First window drives the main ring.</summary>
    public void UpdateProvider(MonitoredAccount account, ProviderReadResult result)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateProvider(account, result));
            return;
        }

        if (!_rings.TryGetValue(account.Provider, out var ring))
        {
            ring = new RingControl(account.Provider.ToString());
            _rings[account.Provider] = ring;
            RingHost.Children.Add(ring);
        }

        if (result.Usage is { Windows.Count: > 0 } usage)
        {
            var main = usage.Windows[0];
            ring.SetProgress(main.UsedFraction, main.IsExhausted);
            ring.SetCaption(main.ResetsAt is { } reset
                ? $"resets {reset.ToLocalTime():MM-dd HH:mm}"
                : usage.CreditBalance ?? string.Empty);
        }
        else if (result.Health != ProviderReadHealth.Healthy)
        {
            ring.SetStale();
        }
    }

    // --- Dragging with edge snapping ---------------------------------------------

    private void OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragOffset = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;
        var screen = PointToScreen(e.GetPosition(this));
        Left = screen.X - _dragOffset.X;
        Top = screen.Y - _dragOffset.Y;
        SnapToEdges();
    }

    private void OnMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void SnapToEdges()
    {
        var area = SystemParameters.WorkArea;
        if (Left - area.Left < SnapThreshold) Left = area.Left;
        if (area.Right - (Left + Width) < SnapThreshold) Left = area.Right - Width;
        if (Top - area.Top < SnapThreshold) Top = area.Top;
        if (area.Bottom - (Top + Height) < SnapThreshold) Top = area.Bottom - Height;
    }

    // --- Per-monitor DPI v2 -------------------------------------------------------

    private void ApplyPerMonitorV2()
    {
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0;
        if (msg == WM_DPICHANGED)
        {
            handled = true;
        }
        return IntPtr.Zero;
    }
}

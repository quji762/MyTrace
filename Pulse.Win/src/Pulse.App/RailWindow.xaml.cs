using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.App.Settings;

namespace Pulse.App;

/// <summary>
/// The Rail: a borderless, topmost vertical strip docked to a screen edge.
/// W6 scope: left/right/top edge docking with snap, position persistence by
/// monitor + normalized offset (never absolute pixels), auto-collapse to a
/// sliver with expand-on-hover, and per-provider detail captions.
/// </summary>
public partial class RailWindow : Window
{
    private const double SnapThreshold = 24;
    private const double CollapsedWidth = 8;
    private const double CollapsedHeight = 120;

    private readonly Dictionary<ProviderId, RingControl> _rings = new();
    private readonly RailPreferences _preferences;
    private readonly DispatcherTimer _collapseTimer;
    private System.Windows.Point _dragOffset;
    private bool _dragging;
    private bool _collapsed;
    private double _expandedWidth = 120;

    public RailWindow()
    {
        InitializeComponent();
        _preferences = RailPreferences.Load();
        _expandedWidth = Width;

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseEnter += (_, _) => Expand();
        MouseLeave += (_, _) => ScheduleCollapse();

        Loaded += (_, _) =>
        {
            DockToPreferredEdge();
            ApplyAutoCollapse();
        };
        SourceInitialized += (_, _) => ApplyPerMonitorV2();
        SystemEvents_DisplayChanged += (_, _) => Dispatcher.BeginInvoke(DockToPreferredEdge);

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            Collapse();
        };
    }

    private static event EventHandler SystemEvents_DisplayChanged
    {
        add => SystemParameters.StaticPropertyChanged += HandleDisplayPropertyChanged;
        remove => SystemParameters.StaticPropertyChanged -= HandleDisplayPropertyChanged;
    }

    private static void HandleDisplayPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // PrimaryScreenWidth/Height change on resolution or monitor changes.
        if (e.PropertyName is "PrimaryScreenWidth" or "PrimaryScreenHeight")
            DisplayChanged?.Invoke(sender, e);
    }

    private static event EventHandler? DisplayChanged;

    public void ShowAndRestore()
    {
        Show();
        WindowState = WindowState.Normal;
        Expand();
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
            var caption = main.ResetsAt is { } reset
                ? $"resets {reset.ToLocalTime():MM-dd HH:mm}"
                : usage.CreditBalance ?? string.Empty;
            if (usage.Plan is { } plan)
                caption = $"{plan} · {caption}";
            ring.SetCaption(caption);
            // Unavailability detail rides on the caption: "Cursor route changed",
            // never "Pulse crashed" (contract-health rule).
        }
        else if (result.Health != ProviderReadHealth.Healthy)
        {
            ring.SetStale();
            ring.SetCaption(result.Health switch
            {
                ProviderReadHealth.Unauthorized => "credential refused",
                ProviderReadHealth.CredentialExpired => "not configured",
                ProviderReadHealth.RateLimited => "rate limited",
                ProviderReadHealth.SchemaChanged => "route changed",
                _ => "unavailable",
            });
        }
    }

    // --- Docking & persistence ----------------------------------------------------

    private void DockToPreferredEdge()
    {
        var area = SystemParameters.WorkArea;
        Left = _preferences.DockedEdge switch
        {
            "left" => area.Left,
            "top" or "bottom" => area.Right - Width,
            _ => area.Right - Width,
        };
        Top = area.Top + _preferences.NormalizedY * (area.Height - Height);
        PersistPosition();
    }

    private void PersistPosition()
    {
        var area = SystemParameters.WorkArea;
        _preferences.NormalizedX = area.Width > 0 ? Math.Clamp((Left - area.Left) / Math.Max(area.Width - Width, 1), 0, 1) : 1.0;
        _preferences.NormalizedY = area.Height > 0 ? Math.Clamp((Top - area.Top) / Math.Max(area.Height - Height, 1), 0, 1) : 0.5;
        _preferences.Save();
    }

    // --- Auto-collapse ------------------------------------------------------------

    private void ApplyAutoCollapse()
    {
        if (_preferences.AutoCollapse) ScheduleCollapse();
        else Expand();
    }

    private void ScheduleCollapse()
    {
        if (!_preferences.AutoCollapse) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void Collapse()
    {
        if (_collapsed) return;
        _collapsed = true;
        CollapseHint.Text = _preferences.DockedEdge == "left" ? "▸" : "◂";
        AnimateWidth(CollapsedWidth);
        AnimateHeight(CollapsedHeight);
    }

    private void Expand()
    {
        _collapseTimer.Stop();
        if (!_collapsed) return;
        _collapsed = false;
        AnimateWidth(_expandedWidth);
        AnimateHeight(Height); // keep the current height
    }

    private void AnimateWidth(double target)
    {
        var animation = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase(),
        };
        BeginAnimation(WidthProperty, animation);
    }

    private void AnimateHeight(double target)
    {
        BeginAnimation(HeightProperty, new System.Windows.Media.Animation.DoubleAnimation(Height, target, TimeSpan.FromMilliseconds(150)));
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
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        // Dock to whichever edge we snapped against, then persist normalized.
        var area = SystemParameters.WorkArea;
        if (Left - area.Left < SnapThreshold) _preferences.DockedEdge = "left";
        else if (area.Right - (Left + Width) < SnapThreshold) _preferences.DockedEdge = "right";
        PersistPosition();
        ScheduleCollapse();
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

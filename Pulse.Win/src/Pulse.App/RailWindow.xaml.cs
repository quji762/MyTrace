using System.Linq;
using System.Runtime.InteropServices;
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
    private const double RailWidth = 64;
    private const double CollapsedWidth = 10;

    private readonly Dictionary<string, RingControl> _rings = new();
    private readonly Dictionary<string, ProviderUsage> _latest = new();
    private readonly HashSet<string> _deniedKeys = new();
    private readonly DispatcherTimer _hoverCloseTimer;

    /// <summary>One ring per monitored account, not per provider — two Claude
    /// logins must not overwrite each other.</summary>
    private static string Key(MonitoredAccount account) => $"{account.Provider}:{account.AccountId}";

    private static string TitleOf(MonitoredAccount account)
    {
        var name = ProviderCatalog.DisplayName(account.Provider);
        // Primary is just the provider; an added account carries the user's label.
        if (account.IsBorrowed || string.IsNullOrWhiteSpace(account.Label) ||
            string.Equals(account.AccountId, account.Provider.ToString(), StringComparison.Ordinal))
        {
            return name;
        }
        return $"{name} — {account.Label}";
    }

    private readonly HoverDetailCard _hoverCard = new();
    private readonly RailPreferences _preferences;
    private readonly DispatcherTimer _collapseTimer;
    private System.Windows.Point _dragOffset;
    private bool _dragging;
    private bool _collapsed;
    private double _expandedWidth = RailWidth;
    private double _expandedHeight = 88;
    private DateTimeOffset _lastHoverUtc = DateTimeOffset.MinValue;

    /// <summary>Raised when the pointer enters the rail, so the refresh loop can shorten its wait.</summary>
    public event Action? UserHovering;

    public event Action? OpenSettingsRequested;
    public event Action? OpenTokenSpendRequested;
    public event Action? ExitRequested;

    public bool IsExpanded => !_collapsed;

    /// <summary>True for a short while after the pointer enters. The scheduler treats that as a request for fresh numbers.</summary>
    public bool HoveredRecently => DateTimeOffset.UtcNow - _lastHoverUtc < TimeSpan.FromMinutes(2);

    public RailWindow()
    {
        InitializeComponent();
        _preferences = RailPreferences.Load();
        _expandedWidth = Width;
        _expandedHeight = Height;

        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(MenuItem(Ui.SettingsMenu, () => OpenSettingsRequested?.Invoke()));
        menu.Items.Add(MenuItem(Ui.TokenSpendMenu, () => OpenTokenSpendRequested?.Invoke()));
        menu.Items.Add(MenuItem(Ui.ExitMenu, () => ExitRequested?.Invoke()));
        ContextMenu = menu;

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseEnter += (_, _) =>
        {
            _lastHoverUtc = DateTimeOffset.UtcNow;
            UserHovering?.Invoke();
            Expand();
        };
        MouseLeave += (_, _) => ScheduleCollapse();

        Loaded += (_, _) =>
        {
            DockToPreferredEdge();
            ApplyAutoCollapse();
            // DPI often settles after the first layout. Dock again once it has,
            // or a 96-DPI placement gets scaled off the monitor.
            Dispatcher.BeginInvoke(DockToPreferredEdge, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        DpiChanged += (_, _) => DockToPreferredEdge();
        SourceInitialized += (_, _) => ApplyPerMonitorV2();
        SystemParameters.StaticPropertyChanged += HandleDisplayPropertyChanged;
        EventHandler onDisplay = (_, _) => Dispatcher.BeginInvoke(new Action(() => DockToPreferredEdge()));
        DisplayChanged += onDisplay;
        Closed += (_, _) =>
        {
            DisplayChanged -= onDisplay;
            SystemParameters.StaticPropertyChanged -= HandleDisplayPropertyChanged;
        };

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            Collapse();
        };
        _hoverCard.PointerEntered += () => _collapseTimer.Stop();
        _hoverCard.PointerLeft += ScheduleCollapse;

        // Close the hover card shortly after the pointer leaves a ring, with a
        // grace period so the pointer can travel into the card without flicker.
        _hoverCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _hoverCloseTimer.Tick += (_, _) =>
        {
            _hoverCloseTimer.Stop();
            _hoverCard.IsOpen = false;
        };
        _hoverCard.PointerLeft += () => _hoverCloseTimer.Start();
        _hoverCard.PointerEntered += () => _hoverCloseTimer.Stop();
    }

    private static System.Windows.Controls.MenuItem MenuItem(string title, Action action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = title };
        item.Click += (_, _) => action();
        return item;
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

    /// <summary>Global-hotkey toggle: expand when folded, collapse when open.</summary>
    public void ToggleVisibility()
    {
        if (!IsVisible || _collapsed)
            ShowAndRestore();
        else
            Collapse();
    }

    /// <summary>Deeplink target: bring the rail up and scroll the account's ring into view.</summary>
    public void FocusAccount(string accountId)
    {
        ShowAndRestore();
        foreach (var (key, ring) in _rings)
        {
            var id = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;
            if (!string.Equals(id, accountId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, accountId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            ring.BringIntoView();
            return;
        }
    }

    /// <summary>Update one account's ring from a reading. First window drives the main ring.</summary>
    public void UpdateProvider(MonitoredAccount account, ProviderReadResult result)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateProvider(account, result));
            return;
        }

        var key = Key(account);
        var title = TitleOf(account);

        if (!_rings.TryGetValue(key, out var ring))
        {
            // Do not create rings for accounts the user has disabled —
            // a late in-flight read must not re-add a removed provider.
            if (_deniedKeys.Contains(key)) return;
            ring = new RingControl(account.Provider);
            // Use a closure that reads the current title at hover time,
            // not the one captured at ring creation.
            ring.MouseEnter += (_, _) =>
            {
                if (_latest.TryGetValue(key, out var u))
                    _hoverCard.ShowFor(u, ring, _preferences.DockedEdge != "left", TitleOf(account));
            };
            ring.MouseLeave += (_, _) => _hoverCloseTimer.Start();
            ring.ToolTip = title;
            _rings[key] = ring;
            RingHost.Children.Add(ring);
            Fit();
        }

        if (result.Usage is { Windows.Count: > 0 } usage)
        {
            _latest[key] = usage;
            var main = usage.Windows[0];
            ring.SetProgress(main.UsedFraction, main.IsExhausted);
            ring.ToolTip = title;
        }
        else if (result.Health == ProviderReadHealth.Healthy
                 && result.Usage is { State: UsageState.Unavailable, Unavailability: { Kind: UnavailabilityKind.NoCredits } } emptied)
        {
            // The provider stated the account holds no allowance at all: an
            // answer, not an outage. Clear the stale figure instead of holding
            // it — a withdrawn allowance must not survive on the rail
            // (upstream v1.4.1).
            _latest[key] = emptied;
            ring.SetStale();
            ring.ToolTip = $"{title}\n{Ui.NoCredits}";
        }
        else if (ring.HasFigure)
        {
            // A failed or empty poll must not wipe a number the user just saw.
            ring.Hold();
        }
        else
        {
            ring.SetStale();
            var health = result.Health == ProviderReadHealth.Healthy
                ? null
                : result.Health switch
                {
                    ProviderReadHealth.Unauthorized => Ui.CredentialRefused,
                    ProviderReadHealth.CredentialExpired => Ui.NotConfigured,
                    ProviderReadHealth.RateLimited => Ui.RateLimited,
                    ProviderReadHealth.SchemaChanged => Ui.RouteChanged,
                    _ => Ui.Unavailable,
                };
            ring.ToolTip = health is null ? title : $"{title}\n{health}";
        }
    }

    /// <summary>First ring for a provider (primary usually). Tests and simple lookups.</summary>
    public RingControl? RingFor(ProviderId provider) =>
        _rings.Values.FirstOrDefault(r => r.Provider == provider);

    public RingControl? RingFor(MonitoredAccount account) =>
        _rings.TryGetValue(Key(account), out var ring) ? ring : null;

    /// <summary>Remove one account's ring from the rail (provider disabled).</summary>
    public void RemoveProvider(MonitoredAccount account)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => RemoveProvider(account));
            return;
        }

        var key = Key(account);
        _deniedKeys.Add(key);
        if (!_rings.Remove(key, out var ring)) return;
        RingHost.Children.Remove(ring);
        _latest.Remove(key);
        Fit();
    }

    /// <summary>Remove ALL rings for a provider (primary + added accounts) and deny re-creation.</summary>
    public void RemoveProviderAll(ProviderId provider)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => RemoveProviderAll(provider));
            return;
        }

        var prefix = $"{provider}:";
        var toRemove = _rings.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in toRemove)
        {
            _deniedKeys.Add(key);
            if (_rings.Remove(key, out var ring))
            {
                RingHost.Children.Remove(ring);
                _latest.Remove(key);
            }
        }
        if (toRemove.Count > 0) Fit();
    }

    /// <summary>Clear denied keys for a provider so its rings can return.</summary>
    public void AllowProviderAll(ProviderId provider)
    {
        var prefix = $"{provider}:";
        _deniedKeys.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>Re-enable a previously denied account key.</summary>
    public void AllowProvider(MonitoredAccount account)
    {
        _deniedKeys.Remove(Key(account));
    }

    /// <summary>Sync rings with the given active accounts: remove stale, allow current.</summary>
    public void SyncActiveProviders(IReadOnlyList<MonitoredAccount> activeAccounts)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SyncActiveProviders(activeAccounts));
            return;
        }

        var activeKeySet = activeAccounts.Select(Key).ToHashSet();
        // Keys in the denied set that are now active again get cleared.
        _deniedKeys.RemoveWhere(k => activeKeySet.Contains(k));

        var stale = _rings.Keys.Where(k => !activeKeySet.Contains(k)).ToList();
        foreach (var key in stale)
        {
            if (_rings.Remove(key, out var ring))
            {
                RingHost.Children.Remove(ring);
                _latest.Remove(key);
                _deniedKeys.Add(key); // suppress late in-flight reads
            }
        }
        if (stale.Count > 0) Fit();
    }

    private void Fit()
    {
        var count = _rings.Count;
        var content = count == 0
            ? 20
            : count * RingControl.ItemHeight + count * 18;
        var height = 22 * 2 + content;
        var area = AreaFor(_preferences.MonitorDeviceName);
        if (area.Height > 120)
            height = Math.Min(height, area.Height - 16);
        _expandedHeight = height;
        _expandedWidth = RailWidth;
        EmptyMark.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RingScroll.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_dragging) return;

        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        Width = RailWidth;
        Height = height;
    }

    private MonitorWorkArea.DipRect AreaFor(string? deviceName) =>
        MonitorWorkArea.ForDevice(deviceName, DipDpi());

    /// <summary>
    /// The scale WPF is actually applying. Win32 still reports 96 while this
    /// window is already being drawn at 150%, which is how the rail left the screen.
    /// </summary>
    private uint DipDpi()
    {
        // Primary width in DIPs versus the same screen in pixels. This stays
        // right even while GetDpiForWindow still reports 96.
        var dip = System.Windows.SystemParameters.PrimaryScreenWidth;
        var pixels = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 0;
        if (dip > 1 && pixels > 1)
        {
            var scale = pixels / dip;
            if (scale >= 1)
                return (uint)Math.Round(scale * 96);
        }

        var origin = PointToScreen(new System.Windows.Point(0, 0));
        var unit = PointToScreen(new System.Windows.Point(96, 0));
        var measured = (unit.X - origin.X) / 96.0;
        if (measured >= 1.05)
            return (uint)Math.Round(measured * 96);

        var system = GetDpiForSystem();
        return system >= 96 ? system : 96;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private void ShowHover(string key, string title, UIElement target)
    {
        if (_collapsed) return;
        if (_latest.TryGetValue(key, out var usage))
            _hoverCard.ShowFor(usage, target, _preferences.DockedEdge != "left", title);
    }

    // --- Docking & persistence ----------------------------------------------------

    private void DockToPreferredEdge()
    {
        var area = AreaFor(_preferences.MonitorDeviceName);
        var width = _collapsed ? CollapsedWidth : _expandedWidth;
        var height = _expandedHeight;
        Left = _preferences.DockedEdge == "left" ? area.Left : area.Right - width;
        Top = area.Top + _preferences.NormalizedY * Math.Max(area.Height - height, 1);
        PersistPosition();
    }

    private void PersistPosition()
    {
        var center = PointToScreen(new System.Windows.Point(Math.Max(ActualWidth, Width) / 2, Math.Max(ActualHeight, Height) / 2));
        var area = MonitorWorkArea.Containing(center.X, center.Y, DipDpi());
        _preferences.MonitorDeviceName = area.DeviceName;
        _preferences.NormalizedX = area.Width > 0
            ? Math.Clamp((Left - area.Left) / Math.Max(area.Width - Width, 1), 0, 1)
            : 1.0;
        _preferences.NormalizedY = area.Height > 0
            ? Math.Clamp((Top - area.Top) / Math.Max(area.Height - Height, 1), 0, 1)
            : 0.5;
        _preferences.Save();
    }

    private MonitorWorkArea.DipRect AreaUnderPointer()
    {
        var center = PointToScreen(new System.Windows.Point(Math.Max(ActualWidth, 1) / 2, Math.Max(ActualHeight, 1) / 2));
        return MonitorWorkArea.Containing(center.X, center.Y, DipDpi());
    }

    // --- Auto-collapse ------------------------------------------------------------

    private void ApplyAutoCollapse()
    {
        // Leave the rings up. Folding them into a few pixels made the numbers
        // vanish, and the sliver was too thin to hover back open.
        _collapseTimer.Stop();
        _collapsed = false;
    }

    private void ScheduleCollapse()
    {
        _collapseTimer.Stop();
        _collapseTimer.Interval = TimeSpan.FromSeconds(2);
        _collapseTimer.Start();
    }

    private void Collapse()
    {
        if (_collapsed) return;
        _collapsed = true;
        _hoverCard.IsOpen = false;
        _hoverCloseTimer.Stop();
        RingScroll.Visibility = Visibility.Collapsed;
        EmptyMark.Visibility = Visibility.Collapsed;
        var area = AreaFor(_preferences.MonitorDeviceName);
        if (_preferences.DockedEdge == "left") Left = area.Left;
        else Left = area.Right - CollapsedWidth;
        AnimateWidth(CollapsedWidth);
    }

    private void Expand()
    {
        _collapseTimer.Stop();
        if (!_collapsed) return;
        _collapsed = false;
        EmptyMark.Visibility = _rings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RingScroll.Visibility = _rings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var area = AreaFor(_preferences.MonitorDeviceName);
        if (_preferences.DockedEdge == "left") Left = area.Left;
        else Left = area.Right - RailWidth;
        AnimateWidth(RailWidth);
    }

    private void AnimateWidth(double target)
    {
        var animation = new System.Windows.Media.Animation.DoubleAnimation(
            target,
            TimeSpan.FromMilliseconds(DesignTokens.MotionBase))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            },
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        animation.Completed += (_, _) => Width = target;
        BeginAnimation(WidthProperty, animation);
    }

    // --- Dragging with edge snapping ---------------------------------------------

    private void OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // The drag must own the pointer: the hover card is a separate HWND
        // that would otherwise take the press, and the collapse timer must
        // not fold the shell mid-drag if the pointer slips off it.
        _hoverCard.IsOpen = false;
        _collapseTimer.Stop();
        _dragging = true;
        _dragOffset = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;
        // GetPosition is in DIPs, the same unit as Left/Top. PointToScreen is
        // physical pixels and must not be assigned to Left/Top.
        var pos = e.GetPosition(this);
        Left += pos.X - _dragOffset.X;
        Top += pos.Y - _dragOffset.Y;
        SnapToEdges();
    }

    private void OnMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        // The drop docks to the nearer edge — a mid-screen drop settles there
        // instead of snapping back on the next expand — and keeps its height,
        // clamped so the rail cannot be parked off the monitor. Do NOT
        // ScheduleCollapse here: the mouse is still over the rail after the
        // drop. Collapse is MouseLeave's job.
        var area = AreaUnderPointer();
        var (edge, left, top) = RailPlacement.Settle(
            Left, Top, Math.Max(ActualWidth, Width), Math.Max(ActualHeight, Height),
            area.Left, area.Top, area.Width, area.Height);
        _preferences.DockedEdge = edge;
        Left = left;
        Top = top;
        PersistPosition();
    }

    private void SnapToEdges()
    {
        var area = AreaUnderPointer();
        Left = RailPlacement.SnapAxis(Left, Math.Max(ActualWidth, Width), area.Left, area.Width);
        Top = RailPlacement.SnapAxis(Top, Math.Max(ActualHeight, Height), area.Top, area.Height);
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
        if (msg == WM_DPICHANGED && lParam != IntPtr.Zero)
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            Width = _expandedWidth > 0 ? _expandedWidth : RailWidth;
            Height = _expandedHeight > 0 ? _expandedHeight : Height;
            // The suggested origin is the previous DIP position times the new
            // scale, which walks a right-docked rail off the monitor.
            Dispatcher.BeginInvoke(DockToPreferredEdge);
            handled = true;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

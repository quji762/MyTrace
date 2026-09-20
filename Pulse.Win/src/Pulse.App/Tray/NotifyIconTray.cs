using System.Drawing;
using System.IO;
using System.Windows.Interop;

namespace Pulse.App.Tray;

/// <summary>
/// System tray icon via WinForms NotifyIcon. Explorer restarts recreate the icon
/// automatically (WinForms handles TaskbarCreated); exit and rail-show go through here.
/// </summary>
public sealed class NotifyIconTray : IDisposable
{
    public event Action? ShowRailRequested;
    public event Action? ShowSettingsRequested;
    public event Action? ExitRequested;

    private System.Windows.Forms.NotifyIcon? _icon;

    public void Initialize()
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Pulse — AI usage at a glance",
            Visible = true,
            Icon = LoadIcon(),
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show rail", null, (_, _) => ShowRailRequested?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => ShowSettingsRequested?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowRailRequested?.Invoke();
    }

    private static Icon LoadIcon()
    {
        // Application icon ships with the MSIX pipeline (W8); a generic icon keeps dev runs clean.
        try
        {
            var uri = new Uri("pack://application:,,,/Pulse.App;component/app.ico");
            var resource = System.Windows.Application.GetResourceStream(uri);
            if (resource is not null) return new Icon(resource.Stream);
        }
        catch (IOException) { }
        return SystemIcons.Application;
    }

    /// <summary>Threshold alerts surface here as balloon tips. Never the only
    /// channel: the rail itself carries the state.</summary>
    public void ShowNotification(string title, string message)
    {
        if (_icon is null) return;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}

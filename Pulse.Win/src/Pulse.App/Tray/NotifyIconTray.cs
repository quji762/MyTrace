using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.Win32;

namespace Pulse.App.Tray;

/// <summary>
/// System tray icon via WinForms NotifyIcon. Explorer restarts recreate the icon
/// automatically (WinForms handles TaskbarCreated); exit and rail-show go through here.
/// </summary>
public sealed class NotifyIconTray : IDisposable
{
    public event Action? ShowRailRequested;
    public event Action? ShowSettingsRequested;
    public event Action? ShowTokenSpendRequested;
    public event Action? ExitRequested;
    public event Action? OpenReleasesRequested;

    private System.Windows.Forms.NotifyIcon? _icon;
    private System.Windows.Forms.ToolStripMenuItem? _updateItem;

    /// <summary>Show or hide the tray icon at runtime (hide-tray-icon setting).</summary>
    public void SetVisible(bool visible)
    {
        if (_icon is null) return;
        _icon.Visible = visible;
    }
    public void SetUpdateAvailable(string? tag)
    {
        if (_icon?.ContextMenuStrip is not { } menu) return;
        if (_updateItem is not null)
        {
            menu.Items.Remove(_updateItem);
            _updateItem.Dispose();
            _updateItem = null;
        }
        if (string.IsNullOrEmpty(tag)) return;
        _updateItem = new System.Windows.Forms.ToolStripMenuItem(Ui.UpdateAvailable(tag));
        _updateItem.Click += (_, _) => OpenReleasesRequested?.Invoke();
        menu.Items.Insert(0, _updateItem);
    }

    public void Initialize()
    {
        // Icon first, then Visible. Setting Visible while Icon is still null
        // makes Explorer drop the notification icon and never draw it.
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Pulse",
            Visible = false,
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Ui.ShowRail, null, (_, _) => ShowRailRequested?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Ui.TokenSpendMenu, null, (_, _) => ShowTokenSpendRequested?.Invoke());
        menu.Items.Add(Ui.SettingsMenu, null, (_, _) => ShowSettingsRequested?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Ui.ExitMenu, null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowRailRequested?.Invoke();
        PromoteOnTaskbar();
        _icon.Visible = true;
    }

    /// <summary>
    /// Windows 11 parks a new notification icon in the overflow. Mark this
    /// executable promoted so the ring shows on the taskbar itself.
    /// </summary>
    private static void PromoteOnTaskbar()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (root is null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name, writable: true);
                var path = sub?.GetValue("ExecutablePath") as string;
                if (sub is null || !string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
            }
        }
        catch (Exception)
        {
            // A locked notification setting still leaves the icon in the overflow.
        }
    }

    /// <summary>
    /// A dark disc with a green usage arc, the same mark as the rail.
    /// Owned by the caller; the tray icon disposes it on exit.
    /// </summary>
    public static Icon CreateIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var disc = new SolidBrush(Color.FromArgb(255, 20, 20, 20));
            graphics.FillEllipse(disc, 1, 1, 30, 30);
            using var track = new Pen(Color.FromArgb(70, 255, 255, 255), 3f);
            graphics.DrawEllipse(track, 5, 5, 22, 22);
            using var arc = new Pen(Color.FromArgb(255, 0, 230, 140), 3f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            graphics.DrawArc(arc, 5, 5, 22, 22, -90, 220);
        }

        // An icon built from a stream owns its pixels. GetHicon plus DestroyIcon
        // leaves the tray holding a blank image, so Explorer draws nothing.
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        var pngBytes = png.ToArray();
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(pngBytes.Length);
            writer.Write(22);
            writer.Write(pngBytes);
        }

        ico.Position = 0;
        return new Icon(ico);
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
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Icon?.Dispose();
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
            _icon = null;
        }
    }
}

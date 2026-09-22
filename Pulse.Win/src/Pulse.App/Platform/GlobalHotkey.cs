using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Pulse.App.Platform;

/// <summary>
/// One system-wide hotkey (default Ctrl+Alt+P) that toggles the rail. Win32
/// RegisterHotKey posts WM_HOTKEY to the window's HWND; the registration dies
/// with the process so a crash cannot leave a stuck hotkey.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5057; // 'PW'

    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;

    public event Action? Pressed;

    public static (uint Modifiers, uint VirtualKey) DefaultBinding =>
        (ModControl | ModAlt, 0x50); // Ctrl+Alt+P

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>Bind against a window's HWND. False when the combination is taken.</summary>
    public bool Register(Window window, uint modifiers, uint virtualKey)
    {
        Unregister();
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        if (_source is null) return false;
        _source.AddHook(WndProc);
        _registered = RegisterHotKey(_hwnd, HotkeyId, modifiers, virtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _hwnd != IntPtr.Zero)
            _ = UnregisterHotKey(_hwnd, HotkeyId);
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
        _registered = false;
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Unregister();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

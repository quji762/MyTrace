using Microsoft.Win32;

namespace Pulse.Core.Platform;

/// <summary>
/// Windows integrations: single instance via a named mutex, and launch-at-startup
/// through the current user's Run key (HKCU — no admin required, per the policy
/// that normal features must not need elevation).
/// </summary>
public static class WindowsIntegration
{
    private const string MutexName = "PulseWin.SingleInstance.{4E3A5C6B}";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PulseWin";

    /// <summary>Try to become the only running instance. False when one already runs.</summary>
    public static IDisposable? AcquireSingleInstanceLock(out bool isFirstInstance)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        isFirstInstance = createdNew;
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        return mutex;
    }

    public static bool IsLaunchAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void SetLaunchAtStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // A locked-down registry is "cannot enable", not a crash.
        }
    }
}

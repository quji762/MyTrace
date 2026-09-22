#pragma warning disable CA1416 // Windows-only ACL APIs; product is Windows.
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Pulse.Core.Platform;

/// <summary>
/// Owner-only ACLs for PulseWin state under LocalAppData. DPAPI blobs are
/// encrypted, but account index / deeplink / preference files are not â€?they
/// must not be world-readable on a shared machine.
/// </summary>
public static class SecureState
{
    /// <summary>Apply owner-only DACL to a directory or file. Best-effort.</summary>
    public static void Protect(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                var security = info.GetAccessControl();
                Restrict(security);
                info.SetAccessControl(security);
            }
            else if (File.Exists(path))
            {
                var info = new FileInfo(path);
                var security = info.GetAccessControl();
                Restrict(security);
                info.SetAccessControl(security);
            }
        }
        catch (Exception)
        {
            // A locked profile still leaves DPAPI encryption as the backstop.
        }
    }

    /// <summary>Create the PulseWin state dir and lock it down.</summary>
    public static string EnsureStateDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseWin");
        Directory.CreateDirectory(dir);
        Protect(dir);
        return dir;
    }

    private static void Restrict(FileSystemSecurity security)
    {
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null) return;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            security.RemoveAccessRule(rule);
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl, AccessControlType.Allow));
    }
}

#pragma warning restore CA1416

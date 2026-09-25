#pragma warning disable CA1416 // Windows-only ACL APIs; product is Windows.
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Pulse.Core.Platform;

/// <summary>
/// Owner-only ACLs for PulseWin state under LocalAppData. DPAPI blobs are
/// encrypted, but account index / deeplink / preference files are not: they
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

    /// <summary>
    /// State text write with self-repair: older builds could leave a state file
    /// with an EMPTY DACL (protection on, no rules), which denies every write
    /// forever. The file's owner can always re-grant themselves access
    /// (WRITE_DAC is an implicit ownership right), so an access-denied write
    /// repairs the ACL -- and a stale read-only attribute -- and retries once.
    /// </summary>
    public static void WriteStateText(string path, string contents)
    {
        try
        {
            File.WriteAllText(path, contents);
        }
        catch (UnauthorizedAccessException)
        {
            RepairAccess(path);
            File.WriteAllText(path, contents);
        }
    }

    /// <summary>State write with a temp-file + atomic replace, repaired the same
    /// way: replacing needs DELETE on the target, which the repaired ACL grants.</summary>
    public static void WriteStateTextAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents);
            try
            {
                File.Move(tmp, path, overwrite: true);
            }
            catch (UnauthorizedAccessException)
            {
                RepairAccess(path);
                File.Move(tmp, path, overwrite: true);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void RepairAccess(string path)
    {
        try
        {
            // A stale read-only attribute denies writes the same way.
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
        catch (Exception)
        {
        }

        Protect(path);
    }

    private static void Restrict(FileSystemSecurity security)
    {
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        // A null user SID must never leave an EMPTY DACL -- protection without
        // a single rule denies everyone, including the owner's next write.
        // CreatorOwner resolves to the file's own owner and is always valid.
        var user = WindowsIdentity.GetCurrent().User
                   ?? new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            security.RemoveAccessRule(rule);
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl, AccessControlType.Allow));
    }
}

#pragma warning restore CA1416


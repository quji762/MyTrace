using System.Security.AccessControl;
using Pulse.Core.Platform;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// State-file writes self-repair when a legacy file carries an empty DACL
/// (protection enabled, no rules — every write denied for everyone). The
/// owner can always re-grant themselves access, so the write retries once.
/// </summary>
public class SecureStateTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"pulse-securestate-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (Exception) { }
    }

    [Fact]
    public void Write_Repairs_A_File_With_An_Empty_Dacl()
    {
        File.WriteAllText(_path, "old");
        // Simulate the legacy damage exactly: protection on, no rules left.
        var info = new FileInfo(_path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        info.SetAccessControl(security);

        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(_path, "denied"));

        SecureState.WriteStateText(_path, "repaired");
        Assert.Equal("repaired", File.ReadAllText(_path));

        // The repair re-grants the owner: a plain write works again too.
        File.WriteAllText(_path, "plain");
        Assert.Equal("plain", File.ReadAllText(_path));
    }

    [Fact]
    public void Atomic_Write_Replaces_A_File_With_An_Empty_Dacl()
    {
        File.WriteAllText(_path, "old");
        var info = new FileInfo(_path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        info.SetAccessControl(security);

        SecureState.WriteStateTextAtomic(_path, "replaced");
        Assert.Equal("replaced", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Protect_Never_Leaves_An_Empty_Dacl()
    {
        File.WriteAllText(_path, "state");
        SecureState.Protect(_path);

        var rules = new FileInfo(_path).GetAccessControl()
            .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier));
        Assert.NotEmpty(rules);
    }
}

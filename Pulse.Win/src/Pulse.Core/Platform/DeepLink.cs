namespace Pulse.Core.Platform;

/// <summary>
/// `pulse://` protocol: register under HKCU so no elevation is needed. A second
/// launch with a URL hands the command to the running instance instead of
/// starting a second rail. `pulse://show` expands the rail, `pulse://settings`
/// opens Settings, `pulse://account/&lt;id&gt;` focuses that account's ring.
/// </summary>
public static class DeepLink
{
    public const string Scheme = "pulse";

    public sealed record Command(string Action, string? AccountId = null)
    {
        public const string Show = "show";
        public const string Settings = "settings";
        public const string Account = "account";
    }

    /// <summary>Register HKCU\Software\Classes\pulse → this executable. Best-effort.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            if (key is null) return;
            key.SetValue(null, "URL:Pulse");
            key.SetValue("URL Protocol", "");
            using var command = key.CreateSubKey(@"shell\open\command");
            command?.SetValue(null, $"\"{exe}\" \"%1\"");
        }
        catch (Exception)
        {
            // A locked registry still leaves in-app shortcuts working.
        }
    }

    /// <summary>Parse argv for a single pulse:// URL. Returns null when absent.</summary>
    public static Command? Parse(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (!arg.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase)) continue;
            return ParseUri(arg);
        }
        return null;
    }

    public static Command? ParseUri(string uriText)
    {
        if (!uriText.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase)) return null;
        // Parse from the raw text: account slots are `Provider#hex`, and Uri
        // would treat `#hex` as a fragment and silently drop the slot.
        var rest = uriText[(Scheme.Length + 3)..];
        var cut = rest.IndexOf('?');
        if (cut >= 0) rest = rest[..cut];
        rest = rest.Trim('/');
        if (rest.Length == 0) return null;

        var slash = rest.IndexOf('/');
        var head = slash < 0 ? rest : rest[..slash];
        var tail = slash < 0 ? "" : rest[(slash + 1)..];

        if (head.Equals(Command.Show, StringComparison.OrdinalIgnoreCase))
            return new Command(Command.Show);
        if (head.Equals(Command.Settings, StringComparison.OrdinalIgnoreCase))
            return new Command(Command.Settings);
        if (head.Equals(Command.Account, StringComparison.OrdinalIgnoreCase) && tail.Length > 0)
            return new Command(Command.Account, Uri.UnescapeDataString(tail));
        return null;
    }

    /// <summary>Hand a command to the running instance (second launch path).</summary>
    public static void WritePending(Command command)
    {
        try
        {
            var file = PendingPath();
            SecureState.EnsureStateDirectory();
            SecureState.EnsureStateDirectory();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var account = command.AccountId is null ? "" : Uri.EscapeDataString(command.AccountId);
            File.WriteAllText(file, $"{command.Action}\n{account}\n{Guid.NewGuid():N}");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Take the pending command if any. Move-then-read so two drains
    /// cannot both apply the same command.</summary>
    public static Command? TakePending()
    {
        try
        {
            var file = PendingPath();
            if (!File.Exists(file)) return null;
            var claimed = file + "." + Guid.NewGuid().ToString("N") + ".taken";
            File.Move(file, claimed);
            var lines = File.ReadAllLines(claimed);
            File.Delete(claimed);
            if (lines.Length == 0) return null;
            var action = lines[0].Trim();
            var account = lines.Length > 1 && lines[1].Trim().Length > 0
                ? Uri.UnescapeDataString(lines[1].Trim())
                : null;
            return action.Length == 0 ? null : new Command(action, account);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string PendingPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "pending-deeplink.txt");
}

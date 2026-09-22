using System.Security;
using System.Text.Json;

namespace Pulse.Core.ClaudeHook;

/// <summary>
/// Pulse's side of Claude Code's status line; port of upstream StatusLineHook.
/// Claude Code pipes a JSON blob to whatever command is registered as the status
/// line after every response, and that blob carries `rate_limits.five_hour` and
/// `rate_limits.seven_day` with the official `used_percentage` and `resets_at`.
///
/// Three pieces, all here:
/// - <see cref="StatusLineInstaller"/> registers the hook in %USERPROFILE%\.claude\settings.json
///   (remembering and restoring any previous status line, backing the file up,
///   refusing to touch an unreadable one).
/// - <see cref="StatusLineCapture"/> is the `--statusline` mode: read stdin, bank the
///   usage part atomically, print a line for Claude Code to show, chain any
///   previous command.
/// - <see cref="StatusLineCapture.Read"/> is the app side: the cached reading, with
///   its age — this is a PUSH, so between sessions the figures are whatever they
///   were at last use, and the provider reports them stale rather than current.
/// </summary>
public static class StatusLinePaths
{
    public static string CacheFile(string? userProfile = null) => Path.Combine(
        userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "pulse-usage.json");

    public static string SettingsFile(string? userProfile = null) => Path.Combine(
        userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "settings.json");

    public const string ModeArgument = "--statusline";
}

/// <summary>
/// Installs/uninstalls the status line registration in the CLI's settings.json.
/// A file that exists but will not parse is LEFT ALONE: an unreadable
/// settings.json must never be replaced wholesale — every permission, hook and
/// model setting in it gone, in exchange for a status line. (JSON parsers refuse
/// things Claude Code itself tolerates, comments among them.)
/// </summary>
public static class StatusLineInstaller
{
    /// <summary>Whether the status line currently points at this app's capture mode.</summary>
    public static bool IsInstalled(string? userProfile = null)
    {
        var settings = ReadSettings(userProfile);
        return settings is not null &&
               settings.TryGetValue("statusLine", out var line) &&
               line.ValueKind == JsonValueKind.Object &&
               line.TryGetProperty("command", out var command) &&
               command.ValueKind == JsonValueKind.String &&
               command.GetString()!.Contains(StatusLinePaths.ModeArgument, StringComparison.Ordinal);
    }

    /// <summary>
    /// Points Claude Code's status line at this executable, remembering any
    /// command that was already there so it can be chained and later restored.
    /// Returns false (and changes nothing) when the settings file exists but
    /// cannot be parsed.
    /// </summary>
    public static bool Install(string executablePath, string? userProfile = null)
    {
        var settingsPath = StatusLinePaths.SettingsFile(userProfile);
        // An absent file is an empty document — there is nothing to lose by
        // creating one. An unreadable one is refused.
        Dictionary<string, JsonElement> settings;
        if (File.Exists(settingsPath))
        {
            if (ReadSettings(userProfile) is not { } parsed) return false;
            settings = parsed.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        }
        else
        {
            settings = new Dictionary<string, JsonElement>();
        }

        // Remember the user's own status line, once, so uninstall can restore it.
        var previousPath = PreviousCommandPath();
        if (settings.TryGetValue("statusLine", out var previous) &&
            previous.ValueKind == JsonValueKind.Object &&
            previous.TryGetProperty("command", out var previousCommand) &&
            previousCommand.ValueKind == JsonValueKind.String &&
            !previousCommand.GetString()!.Contains(StatusLinePaths.ModeArgument, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(previousPath)!);
            File.WriteAllText(previousPath, previousCommand.GetString()!);
        }

        settings["statusLine"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["type"] = "command",
            ["command"] = $"\"{executablePath}\" {StatusLinePaths.ModeArgument}",
        });

        return WriteSettings(settingsPath, settings);
    }

    /// <summary>Puts the status line back the way it was.</summary>
    public static bool Uninstall(string? userProfile = null)
    {
        var settingsPath = StatusLinePaths.SettingsFile(userProfile);
        if (ReadSettings(userProfile) is not { } parsed) return false;
        var settings = parsed.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());

        var previousPath = PreviousCommandPath();
        var previous = File.Exists(previousPath) ? File.ReadAllText(previousPath).Trim() : "";
        if (previous.Length > 0)
        {
            settings["statusLine"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["type"] = "command",
                ["command"] = previous,
            });
        }
        else
        {
            settings.Remove("statusLine");
        }
        if (File.Exists(previousPath)) File.Delete(previousPath);

        return WriteSettings(settingsPath, settings);
    }

    /// <summary>Where the previous command was remembered beside the backup.</summary>
    public static string PreviousCommandPath(string? userProfile = null) =>
        StatusLinePaths.SettingsFile(userProfile) + ".pulse-previous";

    // --- file io -------------------------------------------------------------------

    private static Dictionary<string, JsonElement>? ReadSettings(string? userProfile)
    {
        var path = StatusLinePaths.SettingsFile(userProfile);
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                .ToDictionary(property => property.Name, property => property.Value.Clone());
        }
        catch (JsonException)
        {
            return null; // unreadable: never overwrite
        }
    }

    private static bool WriteSettings(string path, Dictionary<string, JsonElement> settings)
    {
        try
        {
            // Keep a copy of the file as it was before Pulse first touched it.
            var backup = path + ".pulse-backup";
            if (!File.Exists(backup) && File.Exists(path))
                File.Copy(path, backup, overwrite: false);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(settings, options));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// The `--statusline` capture mode and the app-side reader of what it banked.
/// Anything unexpected is swallowed on purpose: this runs inside someone's editor
/// after every response, and a status line that errors out or hangs is far worse
/// than one that says nothing.
/// </summary>
public static class StatusLineCapture
{
    public sealed record CapturedUsage(DateTimeOffset CapturedAt, double? FiveHourPercent, double? SevenDayPercent, DateTimeOffset? FiveHourReset, DateTimeOffset? SevenDayReset);

    /// <summary>
    /// Process one status line payload: bank the usage part atomically (a
    /// temp-file + replace, so the app never catches it half-written) and return
    /// the line Claude Code should show. Never throws.
    /// </summary>
    public static string? RunAsStatusLine(string? stdinPayload, string? userProfile = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(stdinPayload)) return null;
            using var document = JsonDocument.Parse(stdinPayload);
            var root = document.RootElement.Clone();

            if (root.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
                Capture(limits, userProfile);

            return DefaultLine(root);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Capture(JsonElement limits, string? userProfile)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["capturedAt"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
                ["rate_limits"] = limits,
            });

            var cacheFile = StatusLinePaths.CacheFile(userProfile);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var temporary = cacheFile + $".tmp-{Environment.ProcessId}";
            File.WriteAllText(temporary, payload);
            File.Move(temporary, cacheFile, overwrite: true); // one step, atomic on NTFS
        }
        catch (Exception)
        {
            // Banking is best-effort; the status line must keep working.
        }
    }

    /// <summary>What the hook shows when it owns the status line outright: the two
    /// limits, which is the reason the hook is there in the first place.</summary>
    public static string? DefaultLine(JsonElement root)
    {
        var parts = new List<string>();

        var model = root.TryGetProperty("model", out var modelElement) &&
                    modelElement.ValueKind == JsonValueKind.Object &&
                    modelElement.TryGetProperty("display_name", out var displayName) &&
                    displayName.ValueKind == JsonValueKind.String
            ? displayName.GetString()
            : null;

        if (root.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
        {
            foreach (var (key, label) in new[] { ("five_hour", "5h"), ("seven_day", "7d") })
            {
                if (limits.TryGetProperty(key, out var window) && window.ValueKind == JsonValueKind.Object &&
                    PercentOf(window.TryGetProperty("used_percentage", out var up) ? up : default) is { } percent)
                    parts.Add($"{label} {Math.Round(percent)}%");
            }
        }

        if (model is not null) parts.Insert(0, model);
        return parts.Count > 0 ? string.Join("  ·  ", parts) : "";
    }

    /// <summary>
    /// Claude Code has shipped builds where this field leaked the epoch reset
    /// time instead of a percentage; anything past 101 is not a percentage, so
    /// drop it rather than draw a full ring from it.
    /// </summary>
    public static double? PercentOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var d) && d is >= 0 and <= 101 => d,
        _ => null,
    };

    // --- the app side ----------------------------------------------------------------

    /// <summary>
    /// The cached reading for the provider to present, or null when there is
    /// nothing (or nothing fresh). The capturedAt age is what lets the provider
    /// say "as of …" rather than pass an old number off as current.
    /// </summary>
    public static CapturedUsage? Read(string? userProfile = null)
    {
        var path = StatusLinePaths.CacheFile(userProfile);
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            // No capturedAt: age is unknown — do not stamp Now (stale would look fresh).
            if (!root.TryGetProperty("capturedAt", out var at) || !at.TryGetInt64(out var seconds))
                return null;
            var capturedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);

            double? fiveHour = null, sevenDay = null;
            DateTimeOffset? fiveHourReset = null, sevenDayReset = null;

            if (root.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
            {
                if (limits.TryGetProperty("five_hour", out var five) && five.ValueKind == JsonValueKind.Object)
                {
                    fiveHour = PercentOf(five.TryGetProperty("used_percentage", out var fp) ? fp : default);
                    fiveHourReset = ResetOf(five);
                }
                if (limits.TryGetProperty("seven_day", out var seven) && seven.ValueKind == JsonValueKind.Object)
                {
                    sevenDay = PercentOf(seven.TryGetProperty("used_percentage", out var sp) ? sp : default);
                    sevenDayReset = ResetOf(seven);
                }
            }

            return new CapturedUsage(capturedAt, fiveHour, sevenDay, fiveHourReset, sevenDayReset);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTimeOffset? ResetOf(JsonElement window) =>
        window.TryGetProperty("resets_at", out var reset) &&
        reset.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(reset.GetString(), null, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}

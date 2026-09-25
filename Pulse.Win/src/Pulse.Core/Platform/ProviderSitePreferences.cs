using System.Text.Json;
using Pulse.Core.Providers;

namespace Pulse.Core.Platform;

/// <summary>
/// Which site a session-cookie provider's pasted session belongs to: Qoder's
/// qoder.com / qoder.com.cn and StepFun's platform.stepfun.ai /
/// platform.stepfun.com are separate sign-ins. Defaults follow upstream:
/// Qoder reads the international site, StepFun the China one. Providers read
/// this per request, so a change applies on the next refresh pass — and the
/// saved session is discarded, because a session for one host is never sent
/// to the other.
/// </summary>
public static class ProviderSitePreferences
{
    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "sites.json");

    /// <summary>True when the provider reads its China site.</summary>
    public static bool UseChina(ProviderId id, string? path = null)
    {
        if (!HasSiteChoice(id)) return false;
        try
        {
            var file = path ?? DefaultPath();
            if (!File.Exists(file)) return Default(id);
            var map = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(file));
            return map is { } sites && sites.TryGetValue(id.ToString(), out var useChina)
                ? useChina
                : Default(id);
        }
        catch (Exception)
        {
            return Default(id);
        }
    }

    public static void Save(ProviderId id, bool useChina, string? path = null)
    {
        if (!HasSiteChoice(id)) return;
        try
        {
            var file = path ?? DefaultPath();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var sites = new Dictionary<string, bool>();
            if (File.Exists(file))
                sites = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(file)) ?? [];
            sites[id.ToString()] = useChina;
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(sites));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Only the session-cookie providers with two sites offer a choice.</summary>
    public static bool HasSiteChoice(ProviderId id) =>
        id is ProviderId.Qoder or ProviderId.StepFun;

    private static bool Default(ProviderId id) => id == ProviderId.StepFun;
}

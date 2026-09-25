using System.Text.Json;
using Pulse.Core.Providers;

namespace Pulse.Core.Platform;

/// <summary>
/// Which ADDED accounts (multi-account slots) show on the rail. The primary
/// account's visibility is the provider-level toggle; added slots default to
/// shown and can be hidden individually from Settings. Keys are the same
/// "Provider:Slot" strings the rail's ring dictionary uses.
/// </summary>
public static class AccountVisibility
{
    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "account-visibility.json");

    public static bool IsVisible(ProviderId provider, string accountId, string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            if (!File.Exists(file)) return true;
            var map = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(file));
            return map is null || !map.TryGetValue($"{provider}:{accountId}", out var visible) || visible;
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static void Set(ProviderId provider, string accountId, bool visible, string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var map = File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(file)) ?? []
                : [];
            map[$"{provider}:{accountId}"] = visible;
            SecureState.WriteStateText(file, JsonSerializer.Serialize(map));
        }
        catch (Exception)
        {
        }
    }
}

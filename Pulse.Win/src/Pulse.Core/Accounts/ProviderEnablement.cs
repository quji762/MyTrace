using System.Text.Json;
using Pulse.Core.Providers;

namespace Pulse.Core.Accounts;

/// <summary>
/// Per-provider on/off. A missing file means "no overrides": each provider
/// follows <see cref="ProviderPresence.DefaultEnabled"/>. An explicit false
/// keeps a present provider out of the refresh pass.
/// </summary>
public static class ProviderEnablement
{
    public static IReadOnlyDictionary<ProviderId, bool> LoadOverrides(string path)
    {
        if (!File.Exists(path)) return new Dictionary<ProviderId, bool>();
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path))
                      ?? new Dictionary<string, bool>();
            var parsed = new Dictionary<ProviderId, bool>();
            foreach (var (key, value) in raw)
            {
                if (Enum.TryParse<ProviderId>(key, out var id))
                    parsed[id] = value;
            }

            return parsed;
        }
        catch (Exception)
        {
            return new Dictionary<ProviderId, bool>();
        }
    }

    public static void SaveOverrides(string path, IReadOnlyDictionary<ProviderId, bool> overrides)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var raw = overrides.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);
        File.WriteAllText(path, JsonSerializer.Serialize(raw));
    }

    public static bool IsEnabled(
        ProviderId id,
        IReadOnlyDictionary<ProviderId, bool> overrides,
        bool pathPresent,
        bool secretStored)
    {
        if (overrides.TryGetValue(id, out var choice)) return choice;
        return ProviderPresence.DefaultEnabled(pathPresent, secretStored);
    }

    /// <summary>Accounts a due refresh may ask. A switched-off provider is absent.</summary>
    public static IReadOnlyList<MonitoredAccount> DueAccounts(
        IEnumerable<MonitoredAccount> accounts,
        Func<ProviderId, bool> enabled)
    {
        var due = new List<MonitoredAccount>();
        foreach (var account in accounts)
        {
            if (enabled(account.Provider))
                due.Add(account with { Enabled = true });
        }

        return due;
    }
}

using System.Text.Json;
using Pulse.Core.Providers;

namespace Pulse.Core.Usage;

/// <summary>
/// Last-good readings on disk. A reload always comes back stale, drops a window
/// whose reset has passed, and drops a window with no reset older than 24 hours.
/// </summary>
public static class DurableUsageCache
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static void Save(string path, ProviderUsage usage)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        Platform.SecureState.WriteStateText(path, JsonSerializer.Serialize(usage, Json));
    }

    public static ProviderUsage? Load(string path, DateTimeOffset now)
    {
        if (!File.Exists(path)) return null;
        ProviderUsage? stored;
        try
        {
            stored = JsonSerializer.Deserialize<ProviderUsage>(File.ReadAllText(path), Json);
        }
        catch (Exception)
        {
            return null;
        }

        if (stored is null) return null;
        if (stored.ObservedAt is { } observed && now - observed > UsageCache.MaximumAge)
            return null;

        var cache = new UsageCache();
        cache.Store(stored, now);
        var restored = cache.Get(stored.Provider, stored.AccountId, now);
        if (restored is null) return null;
        // Keep the origin the reading was banked with. Cache.Store stamps
        // AppCache for the in-memory last-good path; the file itself is not
        // the source of the figures.
        return restored with { State = UsageState.Stale, Origin = stored.Origin };
    }
}

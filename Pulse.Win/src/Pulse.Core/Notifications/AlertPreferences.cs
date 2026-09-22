using System.Text.Json;

namespace Pulse.Core.Notifications;

/// <summary>How loud the rail's threshold alerts are. Off until the reader chooses otherwise.</summary>
public enum AlertLevel
{
    Off,
    Eighty,
    NinetyFive,
}

public static class AlertPreferences
{
    public static AlertLevel Default => AlertLevel.Off;

    public static AlertLevel Load(string path)
    {
        if (!File.Exists(path)) return Default;
        try
        {
            var raw = JsonSerializer.Deserialize<string>(File.ReadAllText(path));
            return Enum.TryParse<AlertLevel>(raw, out var level) ? level : Default;
        }
        catch (Exception)
        {
            return Default;
        }
    }

    public static void Save(string path, AlertLevel level)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(level.ToString()));
    }

    public static double[] Thresholds(AlertLevel level) => level switch
    {
        AlertLevel.Eighty => [0.8],
        AlertLevel.NinetyFive => [0.95],
        _ => [],
    };
}

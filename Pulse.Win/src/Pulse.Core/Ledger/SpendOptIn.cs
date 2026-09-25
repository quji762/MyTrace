namespace Pulse.Core.Ledger;

/// <summary>Token-spend discovery stays off until this file says otherwise.</summary>
public static class SpendOptIn
{
    public static bool Load(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            return File.ReadAllText(path).Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Save(string path, bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Platform.SecureState.WriteStateText(path, enabled ? "on" : "off");
    }
}

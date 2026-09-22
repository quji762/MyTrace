namespace Pulse.Core;

/// <summary>
/// The single version string shared by the running app and the MSIX identity.
/// The file <c>Pulse.Win/VERSION</c> is that source; both sides read it rather
/// than keeping a second copy in code.
/// </summary>
public static class ProductVersion
{
    public static string Read(string path)
    {
        var text = File.ReadAllText(path).Trim();
        if (string.IsNullOrEmpty(text))
            throw new InvalidDataException("VERSION is empty");
        return text;
    }
}

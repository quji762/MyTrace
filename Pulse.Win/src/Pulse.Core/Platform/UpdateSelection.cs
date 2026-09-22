using System.Text.Json;

namespace Pulse.Core.Platform;

/// <summary>
/// Picks the update to offer. A <c>windows-v*</c> tag with a prerelease suffix
/// is ignored even when its number is newer than the latest stable tag.
/// </summary>
public static class UpdateSelection
{
    public readonly record struct ReleaseTag(string Tag, Version Version);

    public static string? SelectStable(IEnumerable<string> tags)
    {
        ReleaseTag? best = null;
        foreach (var tag in tags)
        {
            if (!TryParseStable(tag, out var parsed)) continue;
            if (best is null || parsed.Version > best.Value.Version)
                best = parsed;
        }

        return best?.Tag;
    }

    /// <summary>
    /// The update check the app runs: GitHub release JSON in, the stable
    /// <c>windows-v*</c> tag out. A newer prerelease does not win.
    /// </summary>
    public static string? ChooseFromReleaseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
        var tags = new List<string>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("tag_name", out var tag) && tag.GetString() is { } name)
                tags.Add(name);
        }

        return SelectStable(tags);
    }

    /// <summary>
    /// True only when <paramref name="stableTag"/> is a stable <c>windows-v*</c>
    /// tag strictly newer than <paramref name="currentVersion"/>. An older or
    /// equal tag stays quiet. Missing version parts compare as zero, so
    /// <c>0.9.0</c> and <c>0.9.0.0</c> are the same release.
    /// </summary>
    public static bool IsNewer(string? currentVersion, string? stableTag)
    {
        if (string.IsNullOrWhiteSpace(currentVersion) || string.IsNullOrWhiteSpace(stableTag))
            return false;
        if (!TryParseStable(stableTag, out var parsed)) return false;
        if (!Version.TryParse(currentVersion.Trim(), out var current)) return false;
        return Compare(parsed.Version, current) > 0;
    }

    private static int Compare(Version left, Version right)
    {
        var parts = new (int Left, int Right)[]
        {
            (left.Major, right.Major),
            (Part(left.Minor), Part(right.Minor)),
            (Part(left.Build), Part(right.Build)),
            (Part(left.Revision), Part(right.Revision)),
        };
        foreach (var (a, b) in parts)
        {
            var diff = a.CompareTo(b);
            if (diff != 0) return diff;
        }

        return 0;
    }

    private static int Part(int value) => value < 0 ? 0 : value;

    public static bool TryParseStable(string tag, out ReleaseTag parsed)
    {
        parsed = default;
        const string prefix = "windows-v";
        if (!tag.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = tag[prefix.Length..];
        if (rest.Contains('-', StringComparison.Ordinal)) return false;
        if (!Version.TryParse(rest, out var version)) return false;
        parsed = new ReleaseTag(tag, version);
        return true;
    }
}

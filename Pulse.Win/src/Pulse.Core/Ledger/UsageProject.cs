namespace Pulse.Core.Ledger;

/// <summary>
/// Project identity survives reading and caching; its short name is only a label.
/// Port of upstream UsageProject.
/// </summary>
public sealed record UsageProject
{
    public enum IdentityKind { Directory, Label, Source }

    public IdentityKind Kind { get; }
    public string IdentityValue { get; }
    public string Name { get; }

    private UsageProject(IdentityKind kind, string identity, string name)
    {
        Kind = kind;
        IdentityValue = identity;
        Name = name;
    }

    /// <summary>Create from a raw value. Returns null for empty/whitespace.</summary>
    public static UsageProject? From(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();

        // Unix absolute path or Windows drive/UNC path.
        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\') ||
            (trimmed.Length >= 3 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' && (trimmed[2] == '\\' || trimmed[2] == '/')))
        {
            var normalized = trimmed.Replace('\\', '/');
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var path = normalized.StartsWith('/') ? "/" + string.Join("/", parts) : string.Join("/", parts);
            var name = parts.Length > 0 ? parts[^1] : normalized;
            return new UsageProject(IdentityKind.Directory, path, name);
        }

        // A workspace URI that is not a local path — VS Code Remote-SSH etc.
        // The whole URI stays its identity; name is the last path component.
        string labelName;
        if (trimmed.Contains("://") && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var last = uri.Segments.Length > 0 ? uri.Segments[^1].TrimEnd('/') : "";
            labelName = string.IsNullOrEmpty(last) || last == "/" ? trimmed : last;
        }
        else
        {
            labelName = trimmed;
        }

        return new UsageProject(IdentityKind.Label, trimmed, labelName);
    }

    /// <summary>Create from a store's project folder when the working directory is not known.</summary>
    public static UsageProject FromSource(string source, string name) =>
        new(IdentityKind.Source, source, name);

    public string? Path => Kind == IdentityKind.Directory ? IdentityValue : null;

    /// <summary>Extend only ambiguous directory names, using the shortest distinct suffix.</summary>
    public static string DisplayName(UsageProject project, IReadOnlySet<UsageProject> projects)
    {
        var peers = projects.Where(p => p != project && p.Name == project.Name).ToList();
        if (peers.Count == 0) return project.Name;

        if (project.Path is not { } path)
            return project.Kind == IdentityKind.Source ? project.IdentityValue : project.Name;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return path;
        for (var count = 2; count <= Math.Max(2, parts.Length); count++)
        {
            var suffix = string.Join("/", parts.Skip(Math.Max(0, parts.Length - count)));
            if (string.IsNullOrEmpty(suffix)) continue;
            if (peers.All(peer => peer.Path is not { } other
                ? suffix != peer.Name
                : string.Join("/", other.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(Math.Max(0, other.Split('/', StringSplitOptions.RemoveEmptyEntries).Length - count))) != suffix))
                return suffix;
        }

        return path;
    }

    public override string ToString() => $"{Kind}:{IdentityValue}";
}

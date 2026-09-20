using System.Text.Json;
namespace Pulse.Core.Ledger;

/// <summary>
/// Crush's project registry and the per-project databases it references; port of
/// upstream CrushReader. A `projects.json` registry names a working `path` and a
/// `data_dir` per project; the database is `&lt;data_dir&gt;/crush.db` when that is
/// absolute, else `&lt;path&gt;/&lt;data_dir&gt;/crush.db`.
///
/// **There are no token records here, by design.** Crush reports a session
/// `cost` in dollars and no tokens. Money in this app comes from tokens at
/// published rates; converting a cost back into tokens would invent a count
/// nobody measured. Like upstream, the reader produces no records — but the
/// registry and databases are still LOCATED, so the UI can mark Crush as
/// recognised-but-without-token-counts rather than silently absent.
/// </summary>
public static class CrushReader
{
    /// <summary>
    /// Registry candidates — every one is reported so the UI can watch them —
    /// in priority order: CRUSH_GLOBAL_DATA, XDG-style data home, local app data.
    /// </summary>
    public static IReadOnlyList<string> RegistryCandidates(
        string? userProfile = null,
        string? appDataLocal = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? FromEnv(string name) =>
            environment is null ? Environment.GetEnvironmentVariable(name) : environment.GetValueOrDefault(name);

        var candidates = new List<string>();
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = appDataLocal ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (FromEnv("CRUSH_GLOBAL_DATA") is { } global && global.Length > 0)
            candidates.Add(Path.Combine(global, "projects.json"));
        if (local.Length > 0)
            candidates.Add(Path.Combine(local, "crush", "projects.json"));
        if (home.Length > 0)
            candidates.Add(Path.Combine(home, "AppData", "Local", "crush", "projects.json"));
        return candidates;
    }

    /// <summary>
    /// The databases each project in an existing registry names, with the
    /// absolute-vs-relative data_dir rule applied. Databases are surfaced for
    /// recognition; no records are ever read from them (the schema's token
    /// columns are not trustworthy and its cost is money, not tokens).
    /// </summary>
    public static IReadOnlyList<string> Databases(string registryPath)
    {
        var databases = new List<string>();
        if (!File.Exists(registryPath)) return databases;

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(registryPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("projects", out var projects) ||
                projects.ValueKind != JsonValueKind.Array)
                return databases;

            foreach (var project in projects.EnumerateArray())
            {
                if (project.ValueKind != JsonValueKind.Object) continue;
                var path = project.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var dataDir = project.TryGetProperty("data_dir", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dataDir)) continue;

                // Absolute data_dir wins; else it sits under the project path.
                var directory = Path.IsPathRooted(dataDir!)
                    ? dataDir!
                    : Path.Combine(path!, dataDir!);
                databases.Add(Path.GetFullPath(Path.Combine(directory, "crush.db")));
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        finally
        {
            document?.Dispose();
        }
        return databases;
    }

    /// <summary>
    /// **No records, ever.** Crush reports cost, not tokens, and a cost is not a
    /// token count. The method exists so the pipeline treats Crush as a
    /// recognised source whose contribution is honestly zero.
    /// </summary>
    public static IReadOnlyList<AgentUsageRecord> Records(
        string? userProfile = null,
        string? appDataLocal = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        // Watched, recognised, and empty — the upstream decision, kept.
        return Array.Empty<AgentUsageRecord>();
    }
}

using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// The Pi-shaped clients: one parser, four identities; port of upstream
/// PiFamilySessionReader. omp shares Pi's format; Senpi adds OmO project
/// children and treats `session_info.name` as a human title; Kimchi keeps a
/// session-scoped dedup namespace instead of the cross-session one. Nothing
/// else differs, so they are configurations of one reader rather than copies.
/// </summary>
public static class PiFamilySessionReader
{
    public enum Dedup { CrossSession, SessionScoped }

    public sealed record Configuration(Dedup Dedup, bool DiscoversProjectChildren, bool SessionInfoNameIsTitle, string? ProviderFallback);

    public static Configuration? ConfigurationFor(string client) => client switch
    {
        "pi" => new Configuration(Dedup.CrossSession, DiscoversProjectChildren: false, SessionInfoNameIsTitle: false, ProviderFallback: null),
        "omp" => new Configuration(Dedup.CrossSession, DiscoversProjectChildren: false, SessionInfoNameIsTitle: false, ProviderFallback: "omp"),
        "senpi" => new Configuration(Dedup.CrossSession, DiscoversProjectChildren: true, SessionInfoNameIsTitle: true, ProviderFallback: null),
        "kimchi" => new Configuration(Dedup.SessionScoped, DiscoversProjectChildren: false, SessionInfoNameIsTitle: false, ProviderFallback: null),
        _ => null,
    };

    public static IReadOnlyList<AgentUsageRecord> Records(string client, string? userProfile = null)
    {
        if (ConfigurationFor(client) is not { } configuration) return Array.Empty<AgentUsageRecord>();

        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Roots follow upstream SessionLogPaths exactly: the agent segment is
        // part of the real layout (`.pi/agent/sessions`), and Kimchi lives
        // under .config/kimchi/harness rather than its own dot-directory.
        var root = client switch
        {
            "pi" => Path.Combine(home, ".pi", "agent", "sessions"),
            "omp" => Path.Combine(home, ".omp", "agent", "sessions"),
            "senpi" => Path.Combine(home, ".senpi", "agent", "sessions"),
            "kimchi" => Path.Combine(home, ".config", "kimchi", "harness", "sessions"),
            _ => null,
        };
        // Only the matching client's root is scanned; a root that does not
        // exist reads as empty.
        if (root is null || !Directory.Exists(root)) return Array.Empty<AgentUsageRecord>();

        var files = new List<PiTranscript.ParsedFile>();
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
            files.Add(PiTranscript.Parse(file));

        // Senpi's OmO children live outside its sessions tree, under the
        // working directory each header names: `<cwd>/.omo/senpi-task/children`.
        if (configuration.DiscoversProjectChildren)
        {
            var known = files.Select(f => Path.GetFullPath(f.Path)).ToHashSet();
            foreach (var cwd in CwdValues(root))
            {
                var children = Path.Combine(cwd, ".omo", "senpi-task", "children");
                if (!Directory.Exists(children)) continue;
                foreach (var extra in Directory.EnumerateFiles(children, "*.jsonl", SearchOption.AllDirectories)
                             .OrderBy(f => f, StringComparer.Ordinal))
                {
                    if (!known.Add(Path.GetFullPath(extra))) continue;
                    files.Add(PiTranscript.Parse(extra));
                }
            }
        }

        var records = new List<AgentUsageRecord>();
        var incomplete = false;
        foreach (var file in files)
        {
            var sessionID = file.IsValid ? file.Header.Id : null;
            foreach (var message in file.Messages)
            {
                if (message.Tally.Total <= 0 && message.Unclassified <= 0) continue;
                if (sessionID is null || message.Timestamp is not { } timestamp || message.Model is null)
                {
                    // Real usage in a file with no usable header, time or model:
                    // a readable subset, not the whole story.
                    incomplete = true;
                    continue;
                }

                records.Add(new AgentUsageRecord
                {
                    Timestamp = timestamp,
                    Model = message.Model,
                    Tally = message.Tally,
                    SessionID = sessionID,
                    Title = configuration.SessionInfoNameIsTitle ? file.SessionInfoName : null,
                    Project = file.Header.Cwd,
                    DeduplicationID = DeduplicationID(client, configuration, sessionID, message),
                    UnclassifiedTokens = message.Unclassified,
                });
            }
        }
        return incomplete ? records.Select(MarkPartial).ToList() : records;
    }

    private static AgentUsageRecord MarkPartial(AgentUsageRecord record) => record with { IsPartial = true };

    /// <summary>The working directories recorded by the session headers under
    /// a root. Discovery needs the header alone, not every conversation.</summary>
    private static IEnumerable<string> CwdValues(string root)
    {
        var cwds = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string? cwd = null;
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    JsonDocument document;
                    try { document = JsonDocument.Parse(line); }
                    catch (JsonException) { continue; }
                    using (document)
                    {
                        var row = document.RootElement;
                        if (row.ValueKind != JsonValueKind.Object) continue;
                        var type = row.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                        if (type == "title") continue;
                        if (type != "session") break;
                        var id = row.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                        if (id is null) break;
                        cwd = row.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                        break;
                    }
                }
            }
            catch (Exception) { }
            if (cwd is { } value) cwds.Add(value);
        }
        return cwds;
    }

    /// <summary>A fork copy of a message folds onto its original by response id,
    /// or by a composite of the message's own fields; Kimchi's older scheme
    /// keeps each session's namespace separate, and no message identity means
    /// no stable key — each occurrence stands on its own.</summary>
    private static string? DeduplicationID(string client, Configuration configuration, string sessionID, PiTranscript.Message message)
    {
        switch (configuration.Dedup)
        {
            case Dedup.SessionScoped:
                if (message.Id is not { } scopedId) return null;
                return $"{client}:{sessionID}:{scopedId}";
            default:
                if (message.ResponseId is { } responseId) return $"{client}:response:{responseId}";
                if (message.Timestamp is not { } timestamp) return null;
                var milliseconds = timestamp.ToUnixTimeMilliseconds();
                var provider = message.Provider ?? configuration.ProviderFallback ?? "";
                var tally = message.Tally;
                return $"{client}:message:{message.Id ?? ""}:{milliseconds}:{provider}:"
                    + $"{message.Model ?? ""}:{tally.Input}:{tally.Output}:{tally.CacheRead}:{tally.CacheWrite}";
        }
    }
}

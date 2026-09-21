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
        var roots = new List<string>();
        foreach (var (clientName, directory) in new[]
                 {
                     ("pi", "pi"), ("omp", "omp"), ("senpi", "senpi"), ("kimchi", "kimchi"),
                 })
        {
            if (clientName == client)
                roots.Add(Path.Combine(home, "." + directory, "sessions"));
        }
        // Only the matching client's root is scanned; a root that does not
        // exist reads as empty.
        if (roots.Count == 0 || !Directory.Exists(roots[0])) return Array.Empty<AgentUsageRecord>();

        var files = new List<PiTranscript.ParsedFile>();
        foreach (var file in Directory.EnumerateFiles(roots[0], "*.jsonl", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
            files.Add(PiTranscript.Parse(file));

        // Senpi's OmO children live outside its sessions tree, under the
        // working directory each header names.
        if (configuration.DiscoversProjectChildren)
        {
            var known = files.Select(f => f.Path).ToHashSet();
            foreach (var extra in Directory.EnumerateFiles(roots[0], "*.jsonl", SearchOption.AllDirectories))
            {
                if (known.Contains(Path.GetFullPath(extra))) continue;
                files.Add(PiTranscript.Parse(extra));
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

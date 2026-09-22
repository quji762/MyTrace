using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Prime Agent: the Pi RLM format plus a parent/child accounting problem; port
/// of upstream PrimeAgentSessionReader.
///
/// A parent assistant message can persist a CUMULATIVE `aggregateUsage` that
/// already includes a child invocation's usage, and a `child_usage_attributed`
/// record states which child and how much. The child transcript is also scanned
/// directly as its own usage, so adding the parent's aggregate unchanged would
/// count the child twice. The fix is not a guess: the parent message whose own
/// tally EQUALS the stated aggregate is the one injected, and it is reduced by
/// exactly the stated child usage, clamped at zero.
///
/// When the child is not present, the parent KEEPS its aggregate: subtracting a
/// child that cannot be found would silently drop tokens nobody else accounts
/// for.
///
/// Attribution ids are eight hex characters unique only inside one session, and
/// a fork copies a session's records into a new file. The attribution key pairs
/// the id with the resolved fork-lineage root; a parentSession cycle resolves
/// every member to the lexicographically smallest path, so the lineage is
/// deterministic rather than traversal-order dependent.
/// </summary>
public static class PrimeAgentSessionReader
{
    /// <summary>Session roots: `sessions` plus the sibling `session-artifacts`.
    /// `PRIME_AGENT_HOME` relocates the product tree, `PRIME_AGENT_SESSION_DIR`
    /// or a `settings.json` `sessionDir` redirects the sessions half only —
    /// artifacts stay under the product tree.</summary>
    public static IReadOnlyList<string> Roots(
        string? userProfile = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? FromEnv(string name)
        {
            var value = environment is null
                ? Environment.GetEnvironmentVariable(name)
                : environment.GetValueOrDefault(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var product = FromEnv("PRIME_AGENT_HOME") is { } productHome
            ? productHome
            : string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".prime");
        if (product is null) return Array.Empty<string>();

        var sessions = FromEnv("PRIME_AGENT_SESSION_DIR")
            ?? SettingsSessionDir(product)
            ?? Path.Combine(product, "agent", "sessions");
        return
        [
            sessions,
            Path.Combine(product, "agent", "session-artifacts"),
        ];
    }

    /// <summary>The `sessionDir` key in the product's `settings.json`, if any.
    /// A blank, non-string or unreadable file is treated as absent rather than
    /// inventing a path.</summary>
    private static string? SettingsSessionDir(string product)
    {
        var path = Path.Combine(product, "settings.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("sessionDir", out var value)) return null;
            if (value.ValueKind != System.Text.Json.JsonValueKind.String) return null;
            var dir = value.GetString();
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static IReadOnlyList<AgentUsageRecord> Records(IEnumerable<string> roots)
    {
        var files = new List<PiTranscript.ParsedFile>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                             .OrderBy(f => f, StringComparer.Ordinal))
                    files.Add(PiTranscript.Parse(file));
            }
            catch (Exception) { }
        }

        var parentOf = new Dictionary<string, string>();
        foreach (var file in files)
        {
            if (file.Header.ParentSession is not { } parent) continue;
            parentOf[file.Path] = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file.Path)!, parent));
        }

        // The top of a fork lineage, collapsing a cycle to its smallest path.
        string LineageRoot(string start)
        {
            var chain = new List<string>();
            var current = start;
            while (true)
            {
                var index = chain.IndexOf(current);
                if (index >= 0)
                {
                    var cycle = chain.GetRange(index, chain.Count - index);
                    return cycle.Min() ?? current;
                }
                chain.Add(current);
                if (!parentOf.TryGetValue(current, out var parent)) return current;
                current = parent;
            }
        }

        bool IsDescendant(string child, string ancestor)
        {
            var current = (string?)child;
            var visited = new HashSet<string>();
            while (current is { } path)
            {
                if (path == ancestor) return true;
                if (!visited.Add(path)) return false;
                current = parentOf.GetValueOrDefault(path);
            }
            return false;
        }

        // Every child transcript's total, for matching a stated child usage. A
        // fork's parentSession names the file it copied (not a child); only a
        // positive rlmDepth is a child.
        var childTotals = files
            .Where(f => (f.Header.RlmDepth ?? 0) > 0)
            .Select(f => (Path: f.Path, Tally: f.Messages.Aggregate(new TokenTally(), (acc, m) => acc + m.Tally)))
            .ToList();

        var consumedChildren = new HashSet<string>();
        var seenAttributions = new HashSet<string>();
        // Keyed by the parent message's stable identity so a fork copy of the
        // same message is reduced identically.
        var reductions = new Dictionary<string, TokenTally>();

        foreach (var file in files)
        {
            foreach (var attribution in file.Attributions)
            {
                if (attribution.Id is not { } id || attribution.TargetId is not { } targetId) continue;

                var key = $"{LineageRoot(file.Path)}#{id}";
                if (!seenAttributions.Add(key)) continue;
                if (attribution.AggregateUsage.Total <= 0 || attribution.ChildUsage.Total <= 0) continue;

                if (file.Messages.FirstOrDefault(m =>
                        m.Id == targetId && m.Tally == attribution.AggregateUsage) is not { } message) continue;

                var childIndex = childTotals.FindIndex(entry =>
                    !consumedChildren.Contains(entry.Path) &&
                    entry.Tally == attribution.ChildUsage &&
                    IsDescendant(entry.Path, LineageRoot(file.Path)));
                if (childIndex < 0) continue;
                consumedChildren.Add(childTotals[childIndex].Path);

                var identity = DeduplicationID(message);
                reductions[identity] = (reductions.GetValueOrDefault(identity, new TokenTally())) + attribution.ChildUsage;
            }
        }

        var records = new List<AgentUsageRecord>();
        var incomplete = false;
        foreach (var file in files)
        {
            var sessionID = file.IsValid ? file.Header.Id : null;
            foreach (var message in file.Messages)
            {
                var tally = message.Tally;
                if (reductions.TryGetValue(DeduplicationID(message), out var subtract))
                {
                    tally = new TokenTally(
                        Input: Math.Max(0, tally.Input - subtract.Input),
                        CacheWrite: Math.Max(0, tally.CacheWrite - subtract.CacheWrite),
                        CacheRead: Math.Max(0, tally.CacheRead - subtract.CacheRead),
                        Output: Math.Max(0, tally.Output - subtract.Output));
                }
                if (tally.Total <= 0 && message.Unclassified <= 0) continue;
                if (sessionID is null || message.Timestamp is not { } timestamp || message.Model is null)
                {
                    incomplete = true;
                    continue;
                }

                records.Add(new AgentUsageRecord
                {
                    Timestamp = timestamp,
                    Model = message.Model,
                    Tally = tally,
                    SessionID = sessionID,
                    Project = file.Header.Cwd,
                    DeduplicationID = DeduplicationID(message),
                    UnclassifiedTokens = message.Unclassified,
                });
            }
        }
        return incomplete ? records.Select(MarkPartial).ToList() : records;
    }

    private static AgentUsageRecord MarkPartial(AgentUsageRecord record) => record with { IsPartial = true };

    /// <summary>A fork copy must fold onto its original, so the key is
    /// session-independent: the response id when present, else a composite of
    /// the message's own fields.</summary>
    private static string DeduplicationID(PiTranscript.Message message)
    {
        if (message.ResponseId is { } responseId) return $"prime-agent:response:{responseId}";
        var milliseconds = message.Timestamp is { } ts
            ? (long)Math.Round(ts.ToUnixTimeMilliseconds() / 1000.0) * 1000
            : 0;
        var tally = message.Tally;
        return $"prime-agent:message:{message.Id ?? ""}:{milliseconds}:{message.Provider ?? ""}:"
            + $"{message.Model ?? ""}:{tally.Input}:{tally.Output}:{tally.CacheRead}:{tally.CacheWrite}";
    }
}

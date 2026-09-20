namespace Pulse.Core.Ledger;

/// <summary>
/// One normalized usage record from any agent's store; port of upstream
/// AgentUsageRecord. The optional metadata is explicit: a session only exists
/// where the store named one, and a title or project is only ever what the store
/// stated, never something guessed from a path.
/// </summary>
public sealed record AgentUsageRecord
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Model { get; init; }

    /// <summary>The work that was split into the four priced kinds. Zero-kind
    /// records carry their count in <see cref="UnclassifiedTokens"/> instead.</summary>
    public TokenTally Tally { get; init; } = new();

    public string? SessionID { get; init; }
    public string? SessionName { get; init; }
    public string? Title { get; init; }
    public string? Project { get; init; }

    /// <summary>The product's own identity for a message, used to fold one
    /// message that two roots (or a stream of snapshots) both contain down to
    /// one. Only an explicit identity folds; everything else is a separate
    /// request even when it looks identical.</summary>
    public string? DeduplicationID { get; init; }

    /// <summary>Real tokens that could not be placed in any of the four kinds.
    /// Added to the four kinds to make the record's total; never placed inside
    /// the tally and never priced. A negative value makes the record broken
    /// data, and the builder skips the record rather than clamping.</summary>
    public int UnclassifiedTokens { get; init; }

    /// <summary>True when only session- or report-level timing is known.</summary>
    public bool IsAggregate { get; init; }

    /// <summary>True when the source could not prove the report was complete.</summary>
    public bool IsPartial { get; init; }

    public int KnownTotal =>
        Tally.Input + Tally.CacheWrite + Tally.CacheRead + Tally.Output;
}

/// <summary>
/// Folds normalized records into a ledger, whatever store they came from; port
/// of upstream AgentUsageLedger.build. Every count is checked before anything
/// else looks at it: a negative kind or remainder, or an overflowing record, is
/// broken data — the record is skipped whole, the rest of the run is kept, and
/// nothing is clamped or saturated, because either would fabricate a reading.
/// </summary>
public static class AgentUsageLedger
{
    public sealed record BuildResult(UsageLedger Ledger, IReadOnlyList<UsageLedger.SessionRow> Sessions);

    public static BuildResult Build(
        IEnumerable<AgentUsageRecord> records,
        ModelPrices prices,
        TimeZoneInfo? timeZone = null,
        UsageLedger.Origin origin = UsageLedger.Origin.LocalTranscripts)
    {
        var knownBuckets = new Dictionary<string, Dictionary<string, TokenTally>>();
        var seen = new HashSet<string>();
        var unpriced = new SortedSet<string>();
        var names = new Dictionary<string, string>();

        // Session rollup, folded as we go.
        var sessions = new Dictionary<string, RunningSession>();

        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Model)) continue;
            var model = record.Model;

            // Every count is a real, non-negative one that fits an int.
            var known = record.KnownTotal;
            var total = known + record.UnclassifiedTokens;
            if (known < 0 || record.UnclassifiedTokens < 0 || total <= 0) continue;

            // Only an explicit identity folds a message.
            if (!string.IsNullOrWhiteSpace(record.DeduplicationID) &&
                !seen.Add(record.DeduplicationID))
                continue;

            var slot = TranscriptParser.SlotKeyFor(record.Timestamp);

            if (known > 0)
            {
                if (!knownBuckets.TryGetValue(slot, out var models))
                    knownBuckets[slot] = models = new Dictionary<string, TokenTally>();
                models[model] = models.GetValueOrDefault(model, new TokenTally()) + record.Tally;
            }

            if (!string.IsNullOrWhiteSpace(record.SessionID))
            {
                var money = known > 0 && prices.PriceFor(model) is { } price
                    ? record.Tally.Cost(price)
                    : 0;

                if (!sessions.TryGetValue(record.SessionID!, out var running))
                    sessions[record.SessionID!] = running = new RunningSession
                    {
                        Start = record.Timestamp,
                        End = record.Timestamp,
                    };
                running.Tokens += total;
                running.Cost += money;
                if (record.Timestamp < running.Start) running.Start = record.Timestamp;
                if (record.Timestamp > running.End) running.End = record.Timestamp;
                running.Name ??= record.SessionName;
                running.Title ??= record.Title;
                running.Project ??= record.Project;
            }
        }

        var ledger = TranscriptParser.Price(knownBuckets, prices, timeZone);
        var withOrigin = ledger with { LedgerOrigin = origin };

        var sessionRows = sessions
            .Select(kv => new UsageLedger.SessionRow
            {
                Name = kv.Value.Name ?? kv.Key,
                Title = kv.Value.Title,
                Project = kv.Value.Project,
                Start = kv.Value.Start,
                End = kv.Value.End,
                Tokens = kv.Value.Tokens,
                Cost = kv.Value.Cost,
            })
            .OrderByDescending(row => row.End)
            .ToList();

        return new BuildResult(withOrigin, sessionRows);
    }

    private sealed class RunningSession
    {
        public int Tokens;
        public double Cost;
        public DateTimeOffset Start;
        public DateTimeOffset End;
        public string? Name;
        public string? Title;
        public string? Project;
    }
}

namespace Pulse.Core.Ledger;

/// <summary>
/// One pass over local token-spend sources. Discovery does not run while the
/// switch is off. Kilo CLI is not OpenCode, and omp / Senpi / Kimchi are not Pi.
/// </summary>
public static class SpendReading
{
    public sealed record SourceResult(
        string SourceId,
        bool Recognized,
        int Tokens,
        IReadOnlyList<ModelTotal> Models);

    public sealed record ModelTotal(string Model, int Tokens, double? PricedAmount, int UnpricedTokens);

    public sealed record Report(bool Enabled, IReadOnlyList<SourceResult> Sources);

    public static Report Read(
        bool enabled,
        string home,
        ModelPrices prices,
        DateOnly? spanStart,
        DateOnly? spanEnd,
        Func<string, bool>? exists = null)
    {
        if (!enabled)
            return new Report(false, Array.Empty<SourceResult>());

        exists ??= FileOrDirectoryExists;
        var sources = new List<SourceResult>();

        AddStore(sources, "opencode", Path.Combine(home, ".local", "share", "opencode", "opencode.db"), prices, spanStart, spanEnd, exists);
        AddStore(sources, "kilo", Path.Combine(home, ".local", "share", "kilo", "kilo.db"), prices, spanStart, spanEnd, exists);

        foreach (var client in new[] { "pi", "omp", "senpi", "kimchi" })
            AddPi(sources, client, home, prices, spanStart, spanEnd, exists);

        AddFreebuff(sources, home, exists);
        AddAntigravityIde(sources, home, prices, spanStart, spanEnd, exists);
        AddAntigravityExport(sources, home, prices, spanStart, spanEnd, exists);

        return new Report(true, sources);
    }

    private static void AddStore(
        List<SourceResult> sources,
        string sourceId,
        string database,
        ModelPrices prices,
        DateOnly? spanStart,
        DateOnly? spanEnd,
        Func<string, bool> exists)
    {
        if (!exists(database)) return;
        var ledger = OpenCodeStoreReader.LedgerAt(database, prices);
        sources.Add(new SourceResult(sourceId, true, TokensInSpan(ledger, spanStart, spanEnd), ModelsInSpan(ledger, spanStart, spanEnd)));
    }

    private static void AddPi(
        List<SourceResult> sources,
        string client,
        string home,
        ModelPrices prices,
        DateOnly? spanStart,
        DateOnly? spanEnd,
        Func<string, bool> exists)
    {
        var root = PiFamilySessionReader.SessionRoot(client, home);
        if (root is null || !exists(root)) return;
        var records = PiFamilySessionReader.Records(client, home)
            .Where(record => InSpan(record.Timestamp, spanStart, spanEnd))
            .ToArray();
        var built = AgentUsageLedger.Build(records, prices);
        sources.Add(new SourceResult(client, true, built.Ledger.AllTime.Tokens, ModelsOf(built.Ledger)));
    }

    private static void AddFreebuff(List<SourceResult> sources, string home, Func<string, bool> exists)
    {
        var root = Path.Combine(home, ".codebuff");
        if (!exists(root)) return;
        _ = FreebuffUsageReader.Records();
        sources.Add(new SourceResult("freebuff", true, 0, Array.Empty<ModelTotal>()));
    }

    private static void AddAntigravityIde(
        List<SourceResult> sources,
        string home,
        ModelPrices prices,
        DateOnly? spanStart,
        DateOnly? spanEnd,
        Func<string, bool> exists)
    {
        var root = Path.Combine(home, ".gemini", "antigravity", "conversations");
        var alt = Path.Combine(home, ".gemini", "antigravity-ide", "conversations");
        if (!exists(root) && !exists(alt)) return;
        var records = AntigravityCliReader.Records(home, "antigravity-ide")
            .Where(record => InSpan(record.Timestamp, spanStart, spanEnd));
        var built = AgentUsageLedger.Build(records, prices);
        sources.Add(new SourceResult("antigravity-ide", true, built.Ledger.AllTime.Tokens, ModelsOf(built.Ledger)));
    }

    private static void AddAntigravityExport(
        List<SourceResult> sources,
        string home,
        ModelPrices prices,
        DateOnly? spanStart,
        DateOnly? spanEnd,
        Func<string, bool> exists)
    {
        var root = Path.Combine(home, ".antigravity", "cache");
        if (!exists(root)) return;
        var records = CapturedAntigravityReader.Records(home)
            .Where(record => InSpan(record.Timestamp, spanStart, spanEnd));
        var built = AgentUsageLedger.Build(records, prices);
        sources.Add(new SourceResult("antigravity", true, built.Ledger.AllTime.Tokens, ModelsOf(built.Ledger)));
    }

    public static int TokensInSpan(UsageLedger ledger, DateOnly? spanStart, DateOnly? spanEnd) =>
        Days(ledger, spanStart, spanEnd).Sum(day => day.Tokens);

    public static IReadOnlyList<ModelTotal> ModelsInSpan(UsageLedger ledger, DateOnly? spanStart, DateOnly? spanEnd)
    {
        var tokens = new Dictionary<string, int>();
        var unpriced = new Dictionary<string, int>();
        var priced = new Dictionary<string, double>();
        foreach (var day in Days(ledger, spanStart, spanEnd))
        {
            foreach (var (model, count) in day.Models)
                tokens[model] = tokens.GetValueOrDefault(model) + count;
            foreach (var (model, count) in day.ModelUnclassifiedTokens)
                unpriced[model] = unpriced.GetValueOrDefault(model) + count;
            foreach (var (model, cost) in day.ModelCosts)
                priced[model] = priced.GetValueOrDefault(model) + cost.Total;
            if (day.ModelTallies.Count > 0 && day.Models.Count == 0)
            {
                foreach (var (model, tally) in day.ModelTallies)
                    tokens[model] = tokens.GetValueOrDefault(model) + tally.Input + tally.Output + tally.CacheRead + tally.CacheWrite;
            }
        }

        return tokens
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var hasPrice = priced.ContainsKey(pair.Key);
                var unclassified = unpriced.GetValueOrDefault(pair.Key);
                return new ModelTotal(
                    pair.Key,
                    pair.Value + unclassified,
                    hasPrice ? priced[pair.Key] : null,
                    hasPrice ? unclassified : pair.Value + unclassified);
            })
            .ToArray();
    }

    private static IReadOnlyList<ModelTotal> ModelsOf(UsageLedger ledger) =>
        ModelsInSpan(ledger, null, null);

    private static IEnumerable<LedgerDay> Days(UsageLedger ledger, DateOnly? spanStart, DateOnly? spanEnd) =>
        ledger.Days.Where(day => InSpan(day.Date, spanStart, spanEnd));

    private static bool InSpan(DateTimeOffset timestamp, DateOnly? spanStart, DateOnly? spanEnd) =>
        // Local days match the ledger's day buckets.
        InSpan(DateOnly.FromDateTime(timestamp.LocalDateTime), spanStart, spanEnd);

    private static bool InSpan(DateOnly date, DateOnly? spanStart, DateOnly? spanEnd)
    {
        if (spanStart is { } start && date < start) return false;
        if (spanEnd is { } end && date > end) return false;
        return true;
    }

    private static bool FileOrDirectoryExists(string path) => File.Exists(path) || Directory.Exists(path);
}

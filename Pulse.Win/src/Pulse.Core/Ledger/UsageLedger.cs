using System.Globalization;

namespace Pulse.Core.Ledger;

/// <summary>
/// Tokens of each kind, which is what a price list needs to become money; port of
/// upstream TokenTally.
/// </summary>
public sealed record TokenTally(
    int Input = 0,
    int CacheWrite = 0,
    int CacheRead = 0,
    int Output = 0)
{
    public int Total => Input + CacheWrite + CacheRead + Output;

    public static TokenTally operator +(TokenTally left, TokenTally right) => new(
        left.Input + right.Input,
        left.CacheWrite + right.CacheWrite,
        left.CacheRead + right.CacheRead,
        left.Output + right.Output);

    /// <summary>
    /// Rates are per million tokens. A missing cache rate falls back to the plain
    /// input rate — that is the provider's own arrangement for models that don't
    /// price the cache separately, not a guess.
    /// </summary>
    public TokenCost CostBreakdown(ModelPrice price)
    {
        var cacheWriteRate = price.CacheWrite ?? price.Input;
        var cacheReadRate = price.CacheRead ?? price.Input;
        return new TokenCost(
            Input: (double)Input * price.Input / 1_000_000,
            CacheWrite: (double)CacheWrite * cacheWriteRate / 1_000_000,
            CacheRead: (double)CacheRead * cacheReadRate / 1_000_000,
            Output: (double)Output * price.Output / 1_000_000);
    }

    public double Cost(ModelPrice price) => CostBreakdown(price).Total;
}

/// <summary>Money worked out from one tally at one model's rates.</summary>
public sealed record TokenCost(
    double Input = 0,
    double CacheWrite = 0,
    double CacheRead = 0,
    double Output = 0)
{
    public double Total => Input + CacheWrite + CacheRead + Output;

    public static TokenCost operator +(TokenCost left, TokenCost right) => new(
        left.Input + right.Input,
        left.CacheWrite + right.CacheWrite,
        left.CacheRead + right.CacheRead,
        left.Output + right.Output);
}

/// <summary>Per-model published API rates (per million tokens), from models.dev.</summary>
public sealed record ModelPrice(
    string RawId,
    string? Name,
    double Input,
    double? CacheWrite,
    double? CacheRead,
    double Output);

/// <summary>The price list; deterministic fallback when no published price exists.</summary>
public sealed class ModelPrices
{
    private readonly IReadOnlyDictionary<string, ModelPrice> _prices;

    public ModelPrices(IReadOnlyDictionary<string, ModelPrice>? prices = null)
    {
        _prices = prices ?? new Dictionary<string, ModelPrice>();
    }

    /// <summary>
    /// Several raw ids can resolve to one display name; the lookup tries the id
    /// first and then any name the price list carries for it.
    /// </summary>
    public ModelPrice? PriceFor(string modelId) =>
        _prices.GetValueOrDefault(modelId);

    public static ModelPrices Empty { get; } = new();
}

/// <summary>One day's work, priced; port of upstream LedgerDay.</summary>
public sealed record LedgerDay
{
    public required DateOnly Date { get; init; }
    public int Tokens { get; init; }
    public double Cost { get; init; }

    /// <summary>Tokens spent on models with no published price: counted towards
    /// Tokens but not Cost, so the two read honestly side by side.</summary>
    public int UnpricedTokens { get; init; }

    /// <summary>Tokens by model, so "which model is doing the work" can be
    /// answered over any span rather than only the one totalled at scan time.</summary>
    public IReadOnlyDictionary<string, int> Models { get; init; } = new Dictionary<string, int>();

    /// <summary>The same day split by kind — fresh input, cache written, cache
    /// read, output. Separates "I sent a lot" from "I re-read a lot".</summary>
    public TokenTally Tally { get; init; } = new();

    /// <summary>The same day split by raw model id, each with its own tally.
    /// The key is the model id as written by the agent, not the display name.</summary>
    public IReadOnlyDictionary<string, TokenTally> ModelTallies { get; init; } =
        new Dictionary<string, TokenTally>();

    /// <summary>Per raw id, what its tokens cost at that model's own rates. A
    /// model absent here has no published price — counted, never priced, never
    /// patched with a zero.</summary>
    public IReadOnlyDictionary<string, TokenCost> ModelCosts { get; init; } =
        new Dictionary<string, TokenCost>();

    /// <summary>Per raw id, tokens that could not be classified into the four
    /// kinds. Never invented into input and never priced.</summary>
    public IReadOnlyDictionary<string, int> ModelUnclassifiedTokens { get; init; } =
        new Dictionary<string, int>();
}

/// <summary>A provider's history, worked out from the logs its own CLI leaves on
/// this machine; port of upstream UsageLedger.</summary>
public sealed record UsageLedger
{
    /// <summary>A quarter of an hour's work. Days are what the card shows, but a
    /// five-hour limit opens and closes inside one, so the totals are kept fine
    /// enough to answer "since this window opened".</summary>
    public sealed record Slot
    {
        public required DateTimeOffset Start { get; init; }
        public int Tokens { get; init; }
        public double Cost { get; init; }

        /// <summary>The quarter-hour's tokens split by raw model id.</summary>
        public IReadOnlyDictionary<string, TokenTally> Models { get; init; } =
            new Dictionary<string, TokenTally>();
    }

    /// <summary>Where the figures came from, which decides what may be said about
    /// them. Transcripts carry the split a price list needs; a provider's own
    /// statistics give one unpriced total — money for that would be invented.</summary>
    public enum Origin
    {
        LocalTranscripts,
        ImportedRecords,
        ProviderStatistics,
    }

    public Origin LedgerOrigin { get; init; } = Origin.LocalTranscripts;

    /// <summary>Some of this work has only session- or report-level timing, so the
    /// hour profile cannot be trusted.</summary>
    public bool HasAggregateTiming { get; init; }

    /// <summary>Some of the counts may be missing; the total is marked partial,
    /// never padded.</summary>
    public bool HasPartialCounts { get; init; }

    /// <summary>Ascending by date, gaps closed so the chart reads as a calendar.</summary>
    public IReadOnlyList<LedgerDay> Days { get; init; } = Array.Empty<LedgerDay>();

    public DateOnly? Earliest { get; init; }

    /// <summary>Models seen in the logs that the price list has no rate for.</summary>
    public IReadOnlyList<string> UnpricedModels { get; init; } = Array.Empty<string>();

    /// <summary>How each model id is written by its provider, where known.</summary>
    public IReadOnlyDictionary<string, string> ModelNames { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Ascending by start time. Only slots with work in them.</summary>
    public IReadOnlyList<Slot> Slots { get; init; } = Array.Empty<Slot>();

    public static UsageLedger EmptyLedger { get; } = new();

    /// <summary>What has gone through since a moment — the figure a rate-limit
    /// window needs. A slot straddling the boundary counts in full.</summary>
    public (int Tokens, double Cost) SpendSince(DateTimeOffset start)
    {
        int tokens = 0;
        double cost = 0;
        foreach (var slot in Slots)
        {
            if (slot.Start < start) continue;
            tokens += slot.Tokens;
            cost += slot.Cost;
        }
        return (tokens, cost);
    }

    public LedgerDay? Today
    {
        get
        {
            if (Days.Count == 0) return null;
            var today = DateOnly.FromDateTime(DateTime.Now);
            return Days[^1].Date == today ? Days[^1] : null;
        }
    }

    public (int Tokens, double Cost) TotalOverLast(int count)
    {
        int tokens = 0;
        double cost = 0;
        foreach (var day in Days.TakeLast(count))
        {
            tokens += day.Tokens;
            cost += day.Cost;
        }
        return (tokens, cost);
    }

    public (int Tokens, double Cost) AllTime
    {
        get
        {
            int tokens = 0;
            double cost = 0;
            foreach (var day in Days)
            {
                tokens += day.Tokens;
                cost += day.Cost;
            }
            return (tokens, cost);
        }
    }

    public IReadOnlyList<LedgerDay> Recent(int count) => Days.TakeLast(count).ToList();

    /// <summary>The heaviest day in a span. Scoped rather than all-time so it sits
    /// beside the other figures without quietly changing the shared window.</summary>
    public LedgerDay? BusiestDayOverLast(int count) =>
        Days.TakeLast(count).OrderByDescending(day => day.Tokens).FirstOrDefault();

    /// <summary>The model most of the work went through, and how much of it.
    /// Falls back to the whole history when the recent window is quiet, so the
    /// line doesn't vanish after a week off.</summary>
    public (string Name, double Share)? TopModelOverLast(int count)
    {
        var window = Days.TakeLast(count).Any(day => day.Tokens > 0)
            ? Days.TakeLast(count).ToList()
            : Days.ToList();

        var totals = new Dictionary<string, int>();
        foreach (var day in window)
        {
            foreach (var (model, tokens) in day.Models)
                totals[model] = totals.GetValueOrDefault(model) + tokens;
        }

        if (totals.Count == 0) return null;
        var leader = totals.MaxBy(kv => kv.Value);
        var overall = totals.Values.Sum();
        if (overall <= 0) return null;

        return (ModelNames.GetValueOrDefault(leader.Key) ?? leader.Key,
            (double)leader.Value / overall);
    }
}

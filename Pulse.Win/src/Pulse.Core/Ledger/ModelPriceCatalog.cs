using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// The price list, fetched from models.dev and kept on disk; port of upstream
/// ModelPrices (the actor) on top of the query-shaped ModelPrices record.
///
/// models.dev covers every provider in one large document; only the providers
/// Pulse reads usage for are kept, which leaves a few kilobytes to cache. It
/// is re-fetched once a day — list prices change on the order of months, and
/// the cached copy is what makes the spend pane work offline.
///
/// <para><b>Model ids are unique within a provider and not across all of
/// them</b>, which is why the first-party list is ordered rather than the
/// whole document. Across the list there are collisions, most of them
/// `github-copilot` re-listing somebody else's model — it is a reseller, and
/// it is left out. The one real collision is `glm-5.2`, sold by both Zhipu and
/// Alibaba; the order settles it, and the rates are within a rounding error of
/// each other anyway.</para>
///
/// <para>The plan vendors are consulted only when <b>no first-party provider
/// publishes the model at all</b>, and then only for the vendor asked about.
/// That is not a second guess at the same number, it is a different question:
/// a rate published by the plan the tokens were actually bought on beats no
/// figure at all. Stored namespaced (`vendor|id`) so a vendor's price can
/// never be found by a lookup that did not ask for that vendor.</para>
/// </summary>
public static class ModelPriceCatalog
{
    /// <summary>Providers whose models Pulse can see usage for, in priority
    /// order. First provider in the list wins a shared id.</summary>
    public static readonly string[] Providers =
    [
        "anthropic", "openai", "xai", "moonshotai", "zhipuai", "minimax",
        "deepseek", "google", "xiaomi", "alibaba", "mistral", "meta",
    ];

    /// <summary>The plan vendors an agent can be priced against when no
    /// first-party provider publishes the model at all.</summary>
    internal static readonly string[] Vendors = ["opencode-go", "kilo", "cline-pass"];

    /// <summary>Separates a vendor from a model id in the table. Not a
    /// character any models.dev id uses.</summary>
    public const char VendorSeparator = '|';

    public static string VendorKey(string vendor, string model) =>
        vendor + VendorSeparator + model;

    private const string Source = "https://models.dev/api.json";
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(24);

    /// <summary>The cache file name; version 4 includes namespaced plan-vendor
    /// rates. A version 3 table can be fresh but cannot satisfy the new
    /// lookup, so it is only an offline fallback and never suppresses a
    /// download on upgrade.</summary>
    public const string CacheFileName = "model-prices-4.json";
    private const string PreviousCacheFileName = "model-prices-3.json";

    public static string DefaultCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseWin");

    // --- one fetch, with the disk as the offline floor --------------------

    /// <summary>Produce a table: a fresh cache, else a download, else the
    /// previous-format cache as an offline fallback. Never throws — a failed
    /// download is an empty table, which the pane already renders as
    /// "unpriced", not as zero.</summary>
    public static async Task<IReadOnlyDictionary<string, ModelPrice>> LoadAsync(
        string? cacheDirectory = null, HttpClient? http = null)
    {
        var directory = cacheDirectory ?? DefaultCacheDirectory();

        if (ReadCache(directory) is { } fresh && DateTime.UtcNow - fresh.FetchedAt < RefreshAfter)
            return fresh.Prices;

        var fetched = await DownloadAsync(http ?? SharedClient()).ConfigureAwait(false);
        if (fetched is { } table && table.Count > 0)
        {
            WriteCache(directory, table);
            return table;
        }

        // Offline: an old copy beats no prices at all, since list prices
        // barely move.
        return ReadCache(directory, allowPreviousVersion: true)?.Prices
            ?? new Dictionary<string, ModelPrice>();
    }

    private static HttpClient SharedClient()
    {
        lock (ClientGate)
        {
            if (_shared is null)
            {
                var handler = new SocketsHttpHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                };
                Platform.NetworkProxy.Load().ApplyTo(handler);
                _shared = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
            }
            return _shared;
        }
    }

    private static readonly object ClientGate = new();
    private static HttpClient? _shared;

    // --- the download -----------------------------------------------------

    /// <summary>The two Pulse price shapes: first-party providers in priority
    /// order, then plan vendors namespaced and only where first-party was
    /// silent. A vendor re-listing somebody else's model must not shadow that
    /// model's own rate.</summary>
    public static IReadOnlyDictionary<string, ModelPrice>? Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = document.RootElement;

            var prices = new Dictionary<string, ModelPrice>(StringComparer.Ordinal);
            foreach (var provider in Providers)
            {
                if (!root.TryGetProperty(provider, out var entry) ||
                    entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("models", out var models) ||
                    models.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var model in models.EnumerateObject())
                {
                    // First provider in the list wins a shared id.
                    if (prices.ContainsKey(model.Name)) continue;
                    if (model.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!TryCost(model.Value, out var input, out var output, out var cacheRead, out var cacheWrite, out var name))
                        continue;
                    prices[model.Name] = new ModelPrice(model.Name, name, input, cacheWrite, cacheRead, output);
                }
            }

            foreach (var vendor in Vendors)
            {
                if (!root.TryGetProperty(vendor, out var entry) ||
                    entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("models", out var models) ||
                    models.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var model in models.EnumerateObject())
                {
                    var key = VendorKey(vendor, model.Name);
                    if (prices.ContainsKey(key)) continue;
                    if (model.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!TryCost(model.Value, out var input, out var output, out var cacheRead, out var cacheWrite, out var name))
                        continue;
                    prices[key] = new ModelPrice(model.Name, name, input, cacheWrite, cacheRead, output);
                }
            }

            return prices.Count == 0 ? null : prices;
        }
    }

    private static async Task<IReadOnlyDictionary<string, ModelPrice>?> DownloadAsync(HttpClient http)
    {
        try
        {
            using var response = await http.GetAsync(Source).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return Parse(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryCost(JsonElement model, out double input, out double output, out double? cacheRead, out double? cacheWrite, out string? name)
    {
        input = output = 0;
        cacheRead = cacheWrite = null;
        name = null;
        if (!model.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
            return false;
        if (!Number(Property(cost, "input"), out input) || !Number(Property(cost, "output"), out output))
            return false;
        if (Number(Property(cost, "cache_read"), out var read)) cacheRead = read;
        if (Number(Property(cost, "cache_write"), out var write)) cacheWrite = write;
        if (Property(model, "name") is { } n && n.ValueKind == JsonValueKind.String)
            name = n.GetString();
        return true;
    }

    private static bool Number(JsonElement? element, out double value)
    {
        value = 0;
        if (element is not { } e) return false;
        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetDouble(out var d) => Assign(d, out value),
            JsonValueKind.String => double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static JsonElement? Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : null;

    private static bool Assign(double d, out double value) { value = d; return true; }

    // --- the lookup and its spellings -------------------------------------

    /// <summary>The price for a model id, allowing for the fact that the agents
    /// do not all spell one the same way.</summary>
    ///
    /// <remarks><b>Aliases, not fuzzy matching.</b> Every rule here is one
    /// product's known habit, written out, because the failure mode of a loose
    /// match is a model priced at another model's rate — a wrong number that
    /// looks right. A lookup that still misses is left unpriced, which is what
    /// the footnote on the pane counts.</remarks>
    public static ModelPrice? PriceFor(string model, IReadOnlyDictionary<string, ModelPrice> table, string? vendor = null)
    {
        if (FirstParty(model, table) is { } direct) return direct;

        // Only now, and only for the vendor asked about: the plan the tokens
        // were bought on is the last word, never the first.
        if (vendor is null) return null;
        if (table.TryGetValue(VendorKey(vendor, model), out var exact)) return exact;
        var lowered = VendorKey(vendor, model).ToLowerInvariant();
        foreach (var kv in table)
            if (kv.Key.ToLowerInvariant() == lowered) return kv.Value;
        foreach (var candidate in Aliases(model))
            if (table.TryGetValue(VendorKey(vendor, candidate), out var match)) return match;
        return null;
    }

    private static ModelPrice? FirstParty(string model, IReadOnlyDictionary<string, ModelPrice> table)
    {
        if (table.TryGetValue(model, out var exact)) return exact;

        // MiniMax writes `MiniMax-M3` and the agents that call it write
        // `minimax-m3`. Case is the only difference.
        var lowered = model.ToLowerInvariant();
        foreach (var kv in table)
            if (kv.Key.ToLowerInvariant() == lowered) return kv.Value;

        foreach (var candidate in Aliases(model))
        {
            if (table.TryGetValue(candidate, out var match)) return match;
            var folded = candidate.ToLowerInvariant();
            foreach (var kv in table)
                if (kv.Key.ToLowerInvariant() == folded) return kv.Value;
        }
        return null;
    }

    /// <summary>Spellings to try for one id, most specific first.</summary>
    public static List<string> Aliases(string model)
    {
        var candidates = new List<string>();

        // Grok Build tags its own build of a model: `grok-4.6-build` is
        // xAI's `grok-4.6`, at xAI's rates.
        if (model.EndsWith("-build", StringComparison.Ordinal) && model != "grok-build-0.1")
            candidates.Add(model[..^"-build".Length]);

        // Devin's CLI writes the version with dashes and an effort on the end:
        // `gpt-5-6-sol-medium` is OpenAI's `gpt-5.6-sol`.
        foreach (var effort in new[] { "-medium", "-high", "-low", "-minimal" })
        {
            if (!model.EndsWith(effort, StringComparison.Ordinal)) continue;
            var baseModel = model[..^effort.Length];
            candidates.Add(baseModel);
            candidates.Add(Dotted(baseModel));
        }
        candidates.Add(Dotted(model));

        // A context window on the end is the same model with more room:
        // `k3-256k` is `kimi-k3`, and it is billed at `k3`'s rates.
        var tag = ContextWindowSuffix(model);
        if (tag >= 0)
        {
            var baseModel = model[..tag];
            candidates.Add(baseModel);
            candidates.AddRange(Aliases(baseModel));
        }

        // Kimi's CLI abbreviates: `k2p6` is `kimi-k2.6`, `k3` is `kimi-k3`.
        if (model.Length > 1 && model[0] == 'k' && model[1..].All(c => char.IsAsciiDigit(c) || c == 'p'))
            candidates.Add("kimi-" + model.Replace("p", "."));

        return candidates.Where(c => c != model).ToList();
    }

    /// <summary>A trailing `-<digits><k|K|m|M>` context-window tag, or -1.</summary>
    private static int ContextWindowSuffix(string model)
    {
        var index = model.Length;
        if (index == 0) return -1;
        var unit = model[index - 1];
        if (unit is not ('k' or 'K' or 'm' or 'M')) return -1;
        index--;
        var start = index;
        while (start > 0 && char.IsAsciiDigit(model[start - 1])) start--;
        if (start == index) return -1;              // no digits
        if (start < 2 || model[start - 1] != '-') return -1;
        return start - 1;
    }

    /// <summary>`gpt-5-6-sol` → `gpt-5.6-sol`: a digit, a dash, a digit is a
    /// version number somebody spelled with the wrong separator. Two words
    /// joined by a dash are left alone.</summary>
    private static string Dotted(string model)
    {
        var characters = model.ToCharArray();
        var outCharacters = new char[characters.Length];
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            if (character == '-' && index > 0 && index + 1 < characters.Length &&
                char.IsAsciiDigit(characters[index - 1]) && char.IsAsciiDigit(characters[index + 1]))
                outCharacters[index] = '.';
            else
                outCharacters[index] = character;
        }
        return new string(outCharacters);
    }

    // --- the disk ----------------------------------------------------------

    public sealed record CacheFile(DateTimeOffset FetchedAt, IReadOnlyDictionary<string, ModelPrice> Prices)
    {
        public TimeSpan Age => DateTimeOffset.UtcNow - FetchedAt;
    }

    public static CacheFile? ReadCache(string directory, bool allowPreviousVersion = false) =>
        ReadCache(new DirectoryInfo(directory), allowPreviousVersion);

    internal static CacheFile? ReadCache(DirectoryInfo directory, bool allowPreviousVersion = false)
    {
        var names = allowPreviousVersion
            ? new[] { CacheFileName, PreviousCacheFileName }
            : new[] { CacheFileName };
        foreach (var name in names)
        {
            try
            {
                var path = Path.Combine(directory.FullName, name);
                if (!File.Exists(path)) continue;
                var data = JsonSerializer.Deserialize<CachedTable>(File.ReadAllText(path));
                if (data is null || data.Prices is null) continue;
                var prices = new Dictionary<string, ModelPrice>(StringComparer.Ordinal);
                foreach (var (id, dto) in data.Prices)
                    prices[id] = new ModelPrice(id, dto.Name, dto.Input, dto.CacheWrite, dto.CacheRead, dto.Output);
                return new CacheFile(data.FetchedAt, prices);
            }
            catch (Exception) { }
        }
        return null;
    }

    public static void WriteCache(string directory, IReadOnlyDictionary<string, ModelPrice> prices)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var table = new Dictionary<string, CachedPrice>(StringComparer.Ordinal);
            foreach (var (id, price) in prices)
                table[id] = new CachedPrice(price.Name, price.Input, price.CacheRead, price.CacheWrite, price.Output);
            var payload = JsonSerializer.Serialize(
                new CachedTable(DateTimeOffset.UtcNow, table));
            File.WriteAllText(Path.Combine(directory, CacheFileName), payload);
        }
        catch (Exception) { }
    }

    private sealed record CachedTable(DateTimeOffset FetchedAt, Dictionary<string, CachedPrice> Prices);

    private sealed record CachedPrice(string? Name, double Input, double? CacheRead, double? CacheWrite, double Output);
}

/// <summary>A price table that resolves ids through the catalog's alias rules:
/// the query shape the readers consume, now backed by the live table.</summary>
public sealed class CatalogModelPrices : ModelPrices
{
    private readonly string? _vendor;

    public CatalogModelPrices(IReadOnlyDictionary<string, ModelPrice> prices, string? vendor = null)
        : base(prices) => _vendor = vendor;

    public override ModelPrice? PriceFor(string modelId) =>
        ModelPriceCatalog.PriceFor(modelId, Table, _vendor);
}

using System.Text.Json;
using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Model price catalog tests over synthetic models.dev documents: the
/// priority order that settles shared ids, plan vendors namespaced and never
/// shadowing first-party rates, the alias rules (each one product's known
/// habit), the disk cache round-trip, and the stale/offline fallbacks.
/// </summary>
public class ModelPriceCatalogTests : IDisposable
{
    private readonly string _directory;

    public ModelPriceCatalogTests() =>
        _directory = Path.Combine(Path.GetTempPath(), $"pulse-prices-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private const string Document = """
        {
          "anthropic": {"models": {
            "claude-opus-4.6": {"name": "Claude Opus 4.6", "cost": {"input": 15, "output": 75, "cache_read": 1.5, "cache_write": 18.75}},
            "claude-sonnet-4.5": {"cost": {"input": 3, "output": 15}}
          }},
          "openai": {"models": {
            "gpt-5.6-sol": {"cost": {"input": 1.25, "output": 10}},
            "shared-id": {"cost": {"input": 2, "output": 8}}
          }},
          "xai": {"models": {"grok-4.6": {"cost": {"input": 3, "output": 15}}}},
          "moonshotai": {"models": {"kimi-k2.6": {"cost": {"input": 0.6, "output": 2.5}}}},
          "zhipuai": {"models": {"glm-5.2": {"cost": {"input": 0.6, "output": 2.2}}}},
          "alibaba": {"models": {"glm-5.2": {"cost": {"input": 0.61, "output": 2.21}}}},
          "deepseek": {"models": {"deepseek-v4": {"cost": {"input": 0.27, "output": 1.1}}}},
          "opencode-go": {"models": {
            "deepseek-v4.1-flash": {"cost": {"input": 0.1, "output": 0.4}},
            "claude-sonnet-4.5": {"cost": {"input": 99, "output": 99}}
          }}
        }
        """;

    private static IReadOnlyDictionary<string, ModelPrice> Parse(string json)
    {
        var prices = ModelPriceCatalog.Parse(json);
        Assert.NotNull(prices);
        return prices!;
    }

    [Fact]
    public void Cost_Fields_Are_Read_And_Optionals_Carry_Null()
    {
        var prices = Parse(Document);
        var opus = prices["claude-opus-4.6"];
        Assert.Equal(15, opus.Input);
        Assert.Equal(75, opus.Output);
        Assert.Equal(1.5, opus.CacheRead);
        Assert.Equal(18.75, opus.CacheWrite);
        Assert.Equal("Claude Opus 4.6", opus.Name);
        // claude-sonnet-4.5 names no cache rates and no display name.
        var sonnet = prices["claude-sonnet-4.5"];
        Assert.Null(sonnet.CacheRead);
        Assert.Null(sonnet.CacheWrite);
        Assert.Null(sonnet.Name);
    }

    [Fact]
    public void First_Provider_In_The_List_Wins_A_Shared_Id()
    {
        var prices = Parse(Document);
        // zhipuai precedes alibaba: zhipu's glm-5.2 rate is the table's.
        Assert.Equal(0.6, prices["glm-5.2"].Input);
    }

    [Fact]
    public void Reseller_Named_GithubCopilot_Is_Not_In_The_Provider_List()
    {
        var prices = Parse(Document.Replace("\"shared-id\"", "\"anything\""));
        Assert.DoesNotContain("github-copilot", ModelPriceCatalog.Providers);
    }

    [Fact]
    public void Vendor_Rates_Are_Namespaced_And_Never_Shadow_First_Party()
    {
        var prices = Parse(Document);
        // The vendor re-lists claude-sonnet-4.5 at 99: first-party always wins
        // — even when the vendor is named — because the plan the tokens were
        // bought on is the LAST word, never the first.
        Assert.Equal(3, ModelPriceCatalog.PriceFor("claude-sonnet-4.5", prices)!.Input);
        Assert.Equal(3, ModelPriceCatalog.PriceFor("claude-sonnet-4.5", prices, "opencode-go")!.Input);
        // A model no first-party provider publishes prices from the plan.
        Assert.Equal(0.1, ModelPriceCatalog.PriceFor("deepseek-v4.1-flash", prices, "opencode-go")!.Input);
        // And without the vendor named, it stays unpriced.
        Assert.Null(ModelPriceCatalog.PriceFor("deepseek-v4.1-flash", prices));
    }

    [Fact]
    public void Aliases_Resolve_One_Products_Known_Habits()
    {
        var prices = Parse(Document);

        // Grok Build tags its own build of a model.
        Assert.Equal("grok-4.6", ModelPriceCatalog.Aliases("grok-4.6-build")[0]);
        Assert.Equal(3, ModelPriceCatalog.PriceFor("grok-4.6-build", prices)!.Input);

        // Devin spells versions with dashes and an effort suffix.
        Assert.Contains("gpt-5.6-sol", ModelPriceCatalog.Aliases("gpt-5-6-sol-medium"));
        Assert.Equal(1.25, ModelPriceCatalog.PriceFor("gpt-5-6-sol-medium", prices)!.Input);

        // A context window tag is the same model with more room.
        Assert.Equal(0.6, ModelPriceCatalog.PriceFor("k2p6-256k", prices, "kilo")!.Input);

        // Kimi's CLI abbreviates: k2p6 is kimi-k2.6.
        Assert.Contains("kimi-k2.6", ModelPriceCatalog.Aliases("k2p6"));
        Assert.Equal(0.6, ModelPriceCatalog.PriceFor("k2p6", prices, "kilo")!.Input);

        // Case is the only difference MiniMax introduces.
        Assert.Equal(0.6, ModelPriceCatalog.PriceFor("GLM-5.2", prices)!.Input);
    }

    [Fact]
    public void Dotted_Leaves_Two_Words_Joined_By_A_Dash_Alone()
    {
        // Only digit-dash-digit is a version separator.
        var aliases = ModelPriceCatalog.Aliases("claude-sonnet");
        Assert.DoesNotContain("claude.sonnet", aliases);
    }

    [Fact]
    public void Cache_Round_Trip_Preserves_The_Table()
    {
        var prices = Parse(Document);
        ModelPriceCatalog.WriteCache(_directory, prices);
        Assert.True(File.Exists(Path.Combine(_directory, ModelPriceCatalog.CacheFileName)));

        var cached = ModelPriceCatalog.ReadCache(_directory);
        Assert.NotNull(cached);
        Assert.Equal(prices.Count, cached!.Prices.Count);
        var opus = cached.Prices["claude-opus-4.6"];
        Assert.Equal(15, opus.Input);
        Assert.Equal(18.75, opus.CacheWrite);
        Assert.Equal("Claude Opus 4.6", opus.Name);
        Assert.True(cached.Age < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Unparsable_Document_Produces_Nothing_Not_An_Empty_Table_That_Looks_Fresh()
    {
        Assert.Null(ModelPriceCatalog.Parse("{not json"));
        Assert.Null(ModelPriceCatalog.Parse("[]"));
        // A document with none of the providers we read is also nothing.
        Assert.Null(ModelPriceCatalog.Parse("""{"unknown": {"models": {"m": {"cost": {"input": 1, "output": 1}}}}}"""));
    }

    [Fact]
    public void LoadAsync_Uses_A_Fresh_Cache_Without_Downloading()
    {
        var prices = Parse(Document);
        ModelPriceCatalog.WriteCache(_directory, prices);

        var loaded = ModelPriceCatalog.LoadAsync(_directory, new FailingHandler().Client).Result;
        Assert.Equal(prices.Count, loaded.Count);
    }

    [Fact]
    public void LoadAsync_Offline_Falls_Back_To_A_Stale_Cache_Of_The_Previous_Format()
    {
        var prices = Parse(Document);
        Directory.CreateDirectory(_directory);
        // A previous-format file (only the name matters here) that is stale.
        File.WriteAllText(Path.Combine(_directory, "model-prices-3.json"),
            JsonSerializer.Serialize(new ModelPriceCatalog.CacheFile(
                DateTimeOffset.UtcNow - TimeSpan.FromDays(3), prices)));

        var loaded = ModelPriceCatalog.LoadAsync(_directory, new FailingHandler().Client).Result;
        Assert.Equal(prices.Count, loaded.Count);
    }

    /// <summary>A handler that fails every request: the offline paths must not
    /// touch the network, and a test must never read the real catalog.</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        public HttpClient Client { get; } = new(Throwing);

        private static readonly HttpMessageHandler Throwing = new DelegatingHandlerStub();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline test"));
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline test");
    }
}

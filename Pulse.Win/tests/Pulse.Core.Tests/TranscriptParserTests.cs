using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// Transcript parser contract tests, driven over hand-built transcripts — the
/// user's own ~/.claude is never read (upstream Docs/testing.md rule, carried over).
/// Semantics are the upstream parser's: dedupe by message id, drop synthetic
/// models, Codex running-total differencing, cached split out of input.
/// </summary>
public class TranscriptParserTests
{
    // --- Claude Code -----------------------------------------------------------------

    [Fact]
    public void Claude_Assistant_Usage_Is_Tallied_Per_Model_Per_Quarter_Hour()
    {
        const string transcript = """
            {"type":"user","message":{"role":"user","content":"fix the login bug"},"timestamp":"2026-09-20T09:00:00Z"}
            {"type":"assistant","message":{"id":"msg_01","model":"claude-sonnet-4-5","usage":{"input_tokens":100,"cache_creation_input_tokens":50,"cache_read_input_tokens":200,"output_tokens":30}},"timestamp":"2026-09-20T09:01:23Z"}
            """;
        var scanned = TranscriptParser.ParseClaudeCode(transcript.Split('\n'));

        var slot = Assert.Single(scanned.Buckets);
        var tally = scanned.Buckets[slot.Key]["claude-sonnet-4-5"];
        Assert.Equal(100, tally.Input);
        Assert.Equal(50, tally.CacheWrite);
        Assert.Equal(200, tally.CacheRead);
        Assert.Equal(30, tally.Output);
        Assert.Equal(380, tally.Total);
    }

    [Fact]
    public void Claude_Duplicate_Message_Id_Is_Counted_Once()
    {
        // Retries and resumed sessions can write the same reply twice.
        const string reply = """{"type":"assistant","message":{"id":"msg_01","model":"claude-sonnet-4-5","usage":{"input_tokens":100,"output_tokens":30}},"timestamp":"2026-09-20T09:01:23Z"}""";
        var transcript = $"{reply}\n{reply}";
        var scanned = TranscriptParser.ParseClaudeCode(transcript.Split('\n'));

        var tally = scanned.Buckets.Values.First()["claude-sonnet-4-5"];
        Assert.Equal(130, tally.Total); // not 260
    }

    [Fact]
    public void Claude_Synthetic_Model_Is_Dropped()
    {
        // A placeholder for the CLI's own errors: no request, nothing to price.
        const string transcript = """
            {"type":"assistant","message":{"id":"msg_02","model":"<synthetic>","usage":{"input_tokens":500,"output_tokens":50}},"timestamp":"2026-09-20T09:02:00Z"}
            """;
        var scanned = TranscriptParser.ParseClaudeCode(transcript.Split('\n'));
        Assert.Empty(scanned.Buckets);
    }

    [Fact]
    public void Claude_Zero_Usage_Is_Dropped()
    {
        const string transcript = """
            {"type":"assistant","message":{"id":"msg_03","model":"claude-sonnet-4-5","usage":{"input_tokens":0,"output_tokens":0}},"timestamp":"2026-09-20T09:03:00Z"}
            """;
        var scanned = TranscriptParser.ParseClaudeCode(transcript.Split('\n'));
        Assert.Empty(scanned.Buckets);
    }

    [Fact]
    public void Claude_Title_Comes_From_Opening_Prompt_And_CustomTitle_Wins_Later()
    {
        const string transcript = """
            {"type":"user","message":{"role":"user","content":"fix the login bug"},"timestamp":"2026-09-20T09:00:00Z"}
            {"cwd":"/home/me/Code/MyTrace","type":"user","message":{"role":"user","content":"x"}}
            {"type":"assistant","message":{"id":"msg_1","model":"m","usage":{"input_tokens":1}},"timestamp":"2026-09-20T09:01:00Z"}
            {"customTitle":"Renamed session","type":"user","message":{"role":"user","content":"y"},"timestamp":"2026-09-20T09:05:00Z"}
            """;
        var scanned = TranscriptParser.ParseClaudeCode(transcript.Split('\n'));
        Assert.Equal("Renamed session", scanned.Title); // a later rename outranks the opening prompt
        Assert.Equal("/home/me/Code/MyTrace", scanned.Cwd);
    }

    [Fact]
    public void Claude_Sidechain_And_Envelope_Lines_Are_Not_Titles()
    {
        const string transcript = """
            {"type":"user","isSidechain":true,"message":{"role":"user","content":"sidechain noise"},"timestamp":"2026-09-20T09:00:00Z"}
            {"type":"user","message":{"role":"user","content":"<command-envelope>"},"timestamp":"2026-09-20T09:00:01Z"}
            {"type":"user","message":{"role":"user","content":"Caveat: some notice"},"timestamp":"2026-09-20T09:00:02Z"}
            """;
        var scanned = TranscriptParser.ParseClaudeCode(transcript.Split('\n'));
        Assert.Null(scanned.Title);
    }

    [Fact]
    public void Claude_Long_Title_Is_Truncated()
    {
        var longText = new string('a', 100);
        const string template = """{{"type":"user","message":{{"role":"user","content":"{0}"}},"timestamp":"2026-09-20T09:00:00Z"}}""";
        var scanned = TranscriptParser.ParseClaudeCode(new[] { string.Format(template, longText) });
        Assert.Equal(70, scanned.Title!.Length);
        Assert.EndsWith("…", scanned.Title);
    }

    // --- Codex -----------------------------------------------------------------------

    [Fact]
    public void Codex_Running_Total_Is_Differenced()
    {
        // Codex reports a running total per turn; summing it would double-count.
        const string transcript = """
            {"timestamp":"2026-09-20T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":20}},"model":"gpt-5.3-codex"}}
            {"timestamp":"2026-09-20T10:05:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":250,"cached_input_tokens":50,"cache_write_input_tokens":30,"output_tokens":60}},"model":"gpt-5.3-codex"}}
            """;
        var scanned = TranscriptParser.ParseCodex(transcript.Split('\n'));

        var allTallies = scanned.Buckets.Values
            .SelectMany(m => m.Values)
            .Aggregate(new TokenTally(), (a, b) => a + b);

        // Turn 1 (no previous): full first reading. Turn 2 delta: input 150,
        // cached 50, cacheWrite 30, output 40. Both turns are real work.
        Assert.Equal(200, allTallies.Input);      // 100 + (150 - 50 cached)
        Assert.Equal(30, allTallies.CacheWrite);
        Assert.Equal(50, allTallies.CacheRead);
        Assert.Equal(60, allTallies.Output);      // 20 + 40
    }

    [Fact]
    public void Codex_Cached_Is_Split_Out_Of_Input()
    {
        // Codex counts cached tokens INSIDE its input figure; the price list
        // treats them as two separate rates.
        const string transcript = """
            {"timestamp":"2026-09-20T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":40,"output_tokens":10}},"model":"gpt-5.3-codex"}}
            """;
        var scanned = TranscriptParser.ParseCodex(transcript.Split('\n'));
        var tally = scanned.Buckets.Values.First()["gpt-5.3-codex"];
        Assert.Equal(60, tally.Input);      // 100 - 40 cached
        Assert.Equal(40, tally.CacheRead);
    }

    [Fact]
    public void Codex_Running_Total_Reset_Is_Clamped_To_Zero()
    {
        // The running total only ever climbs; a clamp keeps a weird file honest.
        const string transcript = """
            {"timestamp":"2026-09-20T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":500,"output_tokens":50}},"model":"gpt-5.3-codex"}}
            {"timestamp":"2026-09-20T10:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"output_tokens":1}},"model":"gpt-5.3-codex"}}
            """;
        var scanned = TranscriptParser.ParseCodex(transcript.Split('\n'));
        var all = scanned.Buckets.Values.SelectMany(m => m.Values).Aggregate(new TokenTally(), (a, b) => a + b);
        Assert.Equal(500, all.Input); // the drop contributed nothing negative
        Assert.Equal(50, all.Output);
    }

    [Fact]
    public void Codex_Title_Comes_From_User_Response_Item()
    {
        // The opening prompt is a response_item with the user's role — NOT an
        // event_msg, which is why every Codex session came out unnamed upstream.
        const string transcript = """
            {"timestamp":"2026-09-20T09:59:00Z","type":"session_meta","payload":{"cwd":"E:\\Projects\\MyTrace"}}
            {"timestamp":"2026-09-20T09:59:30Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"write the ledger tests"}]}}
            """;
        var scanned = TranscriptParser.ParseCodex(transcript.Split('\n'));
        Assert.Equal("write the ledger tests", scanned.Title);
        Assert.Equal("E:\\Projects\\MyTrace", scanned.Cwd);
    }

    // --- Pricing ---------------------------------------------------------------------

    [Fact]
    public void Pricing_Produces_Days_Slots_And_Money()
    {
        var prices = new ModelPrices(new Dictionary<string, ModelPrice>
        {
            ["claude-sonnet-4-5"] = new("claude-sonnet-4-5", "Claude Sonnet 4.5",
                Input: 3, CacheWrite: 3.75, CacheRead: 0.30, Output: 15),
        });

        var buckets = new Dictionary<string, Dictionary<string, TokenTally>>
        {
            // One quarter-hour slot.
            ["2026-09-20 11:00"] = new()
            {
                ["claude-sonnet-4-5"] = new TokenTally(Input: 1_000_000, Output: 1_000_000),
                ["some-unknown-model"] = new TokenTally(Input: 500_000),
            },
        };

        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var ledger = TranscriptParser.Price(buckets, prices, zone);

        // Slot: priced model = 1M input @3 + 1M output @15 = $18.
        var slot = Assert.Single(ledger.Slots);
        Assert.Equal(18.0, slot.Cost, 3);

        // Unpriced model: counted, never priced, never patched with zero.
        Assert.Equal(2_500_000, ledger.AllTime.Tokens);
        Assert.Equal(18.0, ledger.AllTime.Cost, 3);
        Assert.Equal(["some-unknown-model"], ledger.UnpricedModels);
        Assert.Equal(500_000, ledger.Days.Single(d => d.Tokens > 0).UnpricedTokens);

        // Model name resolved from the price list.
        Assert.Equal("Claude Sonnet 4.5", ledger.ModelNames["claude-sonnet-4-5"]);
    }

    [Fact]
    public void Pricing_Fills_Quiet_Days_So_The_Chart_Reads_As_A_Calendar()
    {
        var buckets = new Dictionary<string, Dictionary<string, TokenTally>>
        {
            ["2026-09-14 09:00"] = new() { ["m"] = new TokenTally(Input: 10) },
            ["2026-09-21 09:00"] = new() { ["m"] = new TokenTally(Input: 10) },
        };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var ledger = TranscriptParser.Price(buckets, prices: ModelPrices.Empty, zone);

        // 9-14 → 9-21 inclusive: 8 days, the quiet six in between.
        Assert.Equal(8, ledger.Days.Count);
        Assert.Equal(2, ledger.Days.Count(d => d.Tokens > 0));
        Assert.Equal(6, ledger.Days.Count(d => d.Tokens == 0));
    }

    [Fact]
    public void Ledger_Queries_Answer_The_Card()
    {
        var buckets = new Dictionary<string, Dictionary<string, TokenTally>>
        {
            ["2026-09-19 09:00"] = new() { ["m"] = new TokenTally(Input: 100) },
            ["2026-09-21 09:00"] = new() { ["m"] = new TokenTally(Input: 400) },
        };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var ledger = TranscriptParser.Price(buckets, ModelPrices.Empty, zone);

        Assert.Equal(500, ledger.AllTime.Tokens);
        Assert.Equal(400, ledger.TotalOverLast(1).Tokens);
        Assert.Equal(500, ledger.TotalOverLast(10).Tokens);
        var topModel = ledger.TopModelOverLast(7);
        Assert.NotNull(topModel);
        Assert.Equal("m", topModel!.Value.Name);
        Assert.Equal(1.0, topModel.Value.Share, 3);

        // spend(since:) — the figure a five-hour rate-limit window needs.
        var since = TranscriptParser.ParseSlotKey("2026-09-21 09:00", zone)!.Value;
        Assert.Equal(400, ledger.SpendSince(since).Tokens);
    }

    [Fact]
    public void Slot_Keys_Floor_To_Quarter_Hours()
    {
        var moment = new DateTimeOffset(2026, 9, 21, 11, 47, 23, TimeSpan.FromHours(8));
        Assert.Equal("2026-09-21 11:45", TranscriptParser.SlotKeyFor(moment));

        // Same instant in UTC floors to its own quarter-hour.
        var utc = new DateTimeOffset(2026, 9, 21, 3, 47, 23, TimeSpan.Zero);
        Assert.Equal("2026-09-21 03:45", TranscriptParser.SlotKeyFor(utc, TimeZoneInfo.Utc));
    }

    // --- Locator ---------------------------------------------------------------------

    [Fact]
    public void Locator_Finds_Jsonl_Transcripts_Recursively()
    {
        var guidRoot = Path.Combine(Path.GetTempPath(), $"pulse-locator-{Guid.NewGuid():N}");
        var root = Path.Combine(guidRoot, ".claude", "projects");
        Directory.CreateDirectory(Path.Combine(root, "proj-a"));
        Directory.CreateDirectory(Path.Combine(root, "proj-b", "nested"));
        File.WriteAllText(Path.Combine(root, "proj-a", "s1.jsonl"), "{}");
        File.WriteAllText(Path.Combine(root, "proj-b", "nested", "s2.jsonl"), "{}");
        File.WriteAllText(Path.Combine(root, "proj-b", "notes.txt"), "not a transcript");
        try
        {
            var found = TranscriptLocator.FindTranscripts(root);
            Assert.Equal(2, found.Count);
        }
        finally
        {
            try { Directory.Delete(guidRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Claude_Project_Fallback_Takes_Last_Dash_Segment()
    {
        Assert.Equal("Pulse", TranscriptLocator.ProjectFromClaudeFolder(
            "/home/me/Code/-Users-me-Code-Pulse/s.jsonl"));
        // A folder whose own name contains no dash still yields its last segment:
        // it is a guess either way, and the stated cwd outranks it wherever present.
        Assert.Equal("plain", TranscriptLocator.ProjectFromClaudeFolder("/plain/s.jsonl"));
    }
}

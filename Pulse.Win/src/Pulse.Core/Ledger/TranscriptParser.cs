using System.Globalization;
using System.Security;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>What one transcript file said about itself, beside the counts.</summary>
public sealed record ScannedTranscript
{
    /// <summary>Quarter-hour key ("yyyy-MM-dd HH:mm", local) → model → tally.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, TokenTally>> Buckets { get; init; } =
        new Dictionary<string, Dictionary<string, TokenTally>>();

    /// <summary>The conversation's own name, where the CLI keeps one.</summary>
    public string? Title { get; init; }

    /// <summary>The directory it ran in, as the transcript states it. NOT decoded
    /// from the folder name: Claude Code names project folders for the path with
    /// every separator replaced by a dash, which cannot be reversed.</summary>
    public string? Cwd { get; init; }
}

/// <summary>
/// Reads the CLIs' own JSONL transcripts and adds them up; port of upstream
/// UsageLedgerReader. Scanning is kept off the price list on purpose: the scan
/// holds tokens per model per quarter-hour, and money is worked out afterwards —
/// a price change then costs nothing to apply.
/// </summary>
public static class TranscriptParser
{
    // --- Claude Code -------------------------------------------------------------

    /// <summary>
    /// Claude Code writes one JSON object per message, each assistant reply
    /// carrying the token counts for the request that produced it.
    /// Retries and resumed sessions can write the same reply twice; the message id
    /// identifies it — repeats within a file are dropped. `&lt;synthetic&gt;` models
    /// are Claude Code's placeholders for its own errors: no request was made, so
    /// there is nothing to price.
    /// </summary>
    public static ScannedTranscript ParseClaudeCode(IEnumerable<string> lines)
    {
        var buckets = new Dictionary<string, Dictionary<string, TokenTally>>();
        string? title = null;
        string? cwd = null;
        var seen = new HashSet<string>();
        // A user-set title can arrive long after the opening prompt (customTitle on
        // rename), so that is looked for on every line and the last valid one wins.
        var sawCustomTitle = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var renamed = line.Contains("\"customTitle\"", StringComparison.Ordinal);
            if (!renamed && title != null && cwd != null && !line.Contains("\"usage\"", StringComparison.Ordinal))
                continue;

            JsonElement? rootMaybe = TryParse(line);
            if (rootMaybe is not { } root) continue;

            if (cwd is null &&
                root.TryGetProperty("cwd", out var cwdElement) &&
                cwdElement.ValueKind == JsonValueKind.String &&
                cwdElement.GetString() is { } cwdText && cwdText.Length > 0)
                cwd = cwdText;

            if (renamed &&
                root.TryGetProperty("customTitle", out var customElement) &&
                customElement.ValueKind == JsonValueKind.String &&
                TitleFrom(customElement.GetString()) is { } customTitle)
            {
                title = customTitle;
                sawCustomTitle = true;
            }
            else if (!sawCustomTitle && title is null &&
                     Str(root, "type") == "user" &&
                     (!root.TryGetProperty("isSidechain", out var sidechain) || sidechain.ValueKind != JsonValueKind.True) &&
                     root.TryGetProperty("message", out var message) &&
                     message.ValueKind == JsonValueKind.Object &&
                     TextIn(message.TryGetProperty("content", out var content) ? content : default) is { } text &&
                     TitleFrom(text) is { } openingTitle)
                title = openingTitle;

            // Counts live on assistant replies that carry a usage block.
            if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
            if (Str(root, "type") != "assistant") continue;
            if (!root.TryGetProperty("message", out var assistantMessage) ||
                assistantMessage.ValueKind != JsonValueKind.Object) continue;
            if (!assistantMessage.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object) continue;
            var model = Str(assistantMessage, "model");
            if (model is null || model == "<synthetic>") continue;
            if (Str(root, "timestamp") is not { } timestamp) continue;
            if (SlotKeyFromIso(timestamp) is not { } slot) continue;

            if (Str(assistantMessage, "id") is { } id)
            {
                if (!seen.Add(id)) continue; // a retry's duplicate reply
            }

            var tally = new TokenTally(
                Input: IntOf(usage, "input_tokens"),
                CacheWrite: IntOf(usage, "cache_creation_input_tokens"),
                CacheRead: IntOf(usage, "cache_read_input_tokens"),
                Output: IntOf(usage, "output_tokens"));
            if (tally.Total <= 0) continue;

            if (!buckets.TryGetValue(slot, out var models))
                buckets[slot] = models = new Dictionary<string, TokenTally>();
            models[model] = models.GetValueOrDefault(model, new TokenTally()) + tally;
        }

        return new ScannedTranscript { Buckets = buckets, Title = title, Cwd = cwd };
    }

    // --- Codex ---------------------------------------------------------------------

    /// <summary>
    /// Codex reports a RUNNING total for the session rather than a figure per
    /// turn, so each reading is differenced against the one before it. The running
    /// total only ever climbs, which makes the differences safe to add up — and
    /// sidesteps the duplicate readings that summing Codex's own per-turn field
    /// would double-count. Codex counts cached tokens INSIDE its input figure; the
    /// price list treats them as two separate rates, so cached is split back out.
    /// </summary>
    public static ScannedTranscript ParseCodex(IEnumerable<string> lines)
    {
        var buckets = new Dictionary<string, Dictionary<string, TokenTally>>();
        string? title = null;
        string? cwd = null;
        string? model = null;
        Dictionary<string, int>? previous = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var isCount = line.Contains("\"token_count\"", StringComparison.Ordinal);
            if (!isCount && !line.Contains("\"model\"", StringComparison.Ordinal) &&
                title != null && cwd != null)
                continue;

            if (title is null || cwd is null)
            {
                if (line.Contains("\"cwd\"") || line.Contains("\"role\":\"user\""))
                {
                    // The directory is stated once in the session header; the
                    // opening prompt is a response_item whose payload is a message
                    // with the user's role on it — NOT an event_msg.
                    if (TryParse(line) is { } root &&
                        root.TryGetProperty("payload", out var payload) &&
                        payload.ValueKind == JsonValueKind.Object)
                    {
                        if (cwd is null &&
                            payload.TryGetProperty("cwd", out var cwdElement) &&
                            cwdElement.ValueKind == JsonValueKind.String &&
                            cwdElement.GetString() is { } cwdText && cwdText.Length > 0)
                            cwd = cwdText;

                        if (title is null &&
                            Str(payload, "type") == "message" &&
                            Str(payload, "role") == "user" &&
                            TextIn(payload.TryGetProperty("content", out var content) ? content : default) is { } text &&
                            TitleFrom(text) is { } openingTitle)
                            title = openingTitle;
                    }
                }
            }

            if (!isCount) continue;
            if (TryParse(line) is not { } countRoot) continue;
            if (!countRoot.TryGetProperty("payload", out var countPayload) ||
                countPayload.ValueKind != JsonValueKind.Object) continue;

            // The model can change mid-session; usage is attributed to whichever
            // was in force when the reading was taken.
            if (Str(countPayload, "model") is { } named) model = named;

            if (Str(countPayload, "type") != "token_count") continue;
            if (!countPayload.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Object ||
                !info.TryGetProperty("total_token_usage", out var totals) ||
                totals.ValueKind != JsonValueKind.Object) continue;
            if (Str(countRoot, "timestamp") is not { } timestamp) continue;
            if (SlotKeyFromIso(timestamp) is not { } slot) continue;
            if (model is null) continue;

            var current = new Dictionary<string, int>
            {
                ["input"] = IntOf(totals, "input_tokens"),
                ["cached"] = IntOf(totals, "cached_input_tokens"),
                ["cacheWrite"] = IntOf(totals, "cache_write_input_tokens"),
                ["output"] = IntOf(totals, "output_tokens"),
            };
            var delta = new Dictionary<string, int>();
            foreach (var (key, value) in current)
                delta[key] = Math.Max(value - (previous?.GetValueOrDefault(key) ?? 0), 0);
            previous = current;

            var tally = new TokenTally(
                Input: Math.Max(delta["input"] - delta["cached"], 0),
                CacheWrite: delta["cacheWrite"],
                CacheRead: delta["cached"],
                Output: delta["output"]);
            if (tally.Total <= 0) continue;

            if (!buckets.TryGetValue(slot, out var models))
                buckets[slot] = models = new Dictionary<string, TokenTally>();
            models[model] = models.GetValueOrDefault(model, new TokenTally()) + tally;
        }

        return new ScannedTranscript { Buckets = buckets, Title = title, Cwd = cwd };
    }

    // --- Pricing: buckets → days, slots and money -----------------------------------

    /// <summary>
    /// Turns buckets into days, slots and money. Shared by every reader so there
    /// is one way of turning tokens into dollars, not two that disagree.
    /// </summary>
    public static UsageLedger Price(
        IReadOnlyDictionary<string, Dictionary<string, TokenTally>> buckets,
        ModelPrices prices,
        TimeZoneInfo? timeZone = null)
    {
        if (buckets.Count == 0) return UsageLedger.EmptyLedger;
        var zone = timeZone ?? TimeZoneInfo.Local;

        var unpriced = new SortedSet<string>();
        var names = new Dictionary<string, string>();
        var slots = new List<UsageLedger.Slot>();

        var dayTokens = new Dictionary<DateOnly, int>();
        var dayCost = new Dictionary<DateOnly, double>();
        var dayUnpriced = new Dictionary<DateOnly, int>();
        var dayModels = new Dictionary<DateOnly, Dictionary<string, int>>();
        var dayModelTallies = new Dictionary<DateOnly, Dictionary<string, TokenTally>>();
        var dayModelCosts = new Dictionary<DateOnly, Dictionary<string, TokenCost>>();
        var dayTally = new Dictionary<DateOnly, TokenTally>();

        foreach (var (key, models) in buckets)
        {
            if (ParseSlotKey(key, zone) is not { } start) continue;
            var day = DateOnly.FromDateTime(start.LocalDateTime);

            int tokens = 0;
            double cost = 0;
            int unpricedTokens = 0;

            foreach (var (model, tally) in models)
            {
                tokens += tally.Total;
                if (!dayModels.TryGetValue(day, out var dm))
                    dayModels[day] = dm = new Dictionary<string, int>();
                dm[model] = dm.GetValueOrDefault(model) + tally.Total;

                if (!dayModelTallies.TryGetValue(day, out var dmt))
                    dayModelTallies[day] = dmt = new Dictionary<string, TokenTally>();
                dmt[model] = dmt.GetValueOrDefault(model, new TokenTally()) + tally;

                if (!dayTally.TryGetValue(day, out var dt))
                    dayTally[day] = dt = new TokenTally();
                dayTally[day] = dt + tally;

                if (prices.PriceFor(model) is { } price)
                {
                    var money = tally.CostBreakdown(price);
                    cost += money.Total;
                    if (!dayModelCosts.TryGetValue(day, out var dmc))
                        dayModelCosts[day] = dmc = new Dictionary<string, TokenCost>();
                    dmc[model] = dmc.GetValueOrDefault(model, new TokenCost()) + money;
                    if (price.Name is { } name) names[model] = name;
                }
                else
                {
                    unpriced.Add(model);
                    unpricedTokens += tally.Total;
                }
            }

            slots.Add(new UsageLedger.Slot { Start = start, Tokens = tokens, Cost = cost, Models = models });

            dayTokens[day] = dayTokens.GetValueOrDefault(day) + tokens;
            dayCost[day] = dayCost.GetValueOrDefault(day) + cost;
            dayUnpriced[day] = dayUnpriced.GetValueOrDefault(day) + unpricedTokens;
        }

        if (dayTokens.Count == 0) return UsageLedger.EmptyLedger;

        var byDate = new Dictionary<DateOnly, LedgerDay>();
        foreach (var (day, tokens) in dayTokens)
        {
            byDate[day] = new LedgerDay
            {
                Date = day,
                Tokens = tokens,
                Cost = dayCost.GetValueOrDefault(day),
                UnpricedTokens = dayUnpriced.GetValueOrDefault(day),
                Models = dayModels.GetValueOrDefault(day, new Dictionary<string, int>()),
                Tally = dayTally.GetValueOrDefault(day, new TokenTally()),
                ModelTallies = dayModelTallies.GetValueOrDefault(day, new Dictionary<string, TokenTally>()),
                ModelCosts = dayModelCosts.GetValueOrDefault(day, new Dictionary<string, TokenCost>()),
            };
        }

        var earliest = byDate.Keys.Min();
        var latest = byDate.Keys.Max();

        // Fill the quiet days back in: without them a fortnight off would read
        // as a weekend.
        var days = new List<LedgerDay>();
        for (var cursor = earliest; cursor <= latest; cursor = cursor.AddDays(1))
        {
            days.Add(byDate.TryGetValue(cursor, out var filled)
                ? filled
                : new LedgerDay
                {
                    Date = cursor,
                    Tokens = 0,
                    Cost = 0,
                    UnpricedTokens = 0,
                    Models = new Dictionary<string, int>(),
                });
        }

        return new UsageLedger
        {
            Days = days,
            Earliest = earliest,
            UnpricedModels = unpriced.ToList(),
            ModelNames = names,
            Slots = slots.OrderBy(slot => slot.Start).ToList(),
        };
    }

    // --- Slot keys -------------------------------------------------------------------

    /// <summary>The quarter-hour a moment falls in, local time: "yyyy-MM-dd HH:mm".</summary>
    public static string SlotKeyFor(DateTimeOffset moment, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var quarter = 15.0 * 60;
        var epoch = moment.ToUnixTimeSeconds();
        var floored = (long)(Math.Floor((double)epoch / quarter) * quarter);
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(floored), zone);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static DateTimeOffset? ParseSlotKey(string key, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        if (!DateTime.TryParseExact(key, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return null;
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    private static string? SlotKeyFromIso(string text)
    {
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return null;
        return SlotKeyFor(parsed);
    }

    // --- Line helpers ------------------------------------------------------------------

    private static JsonElement? TryParse(string line)
    {
        try
        {
            return JsonDocument.Parse(line).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The opening prompt, cut to something a row can hold. NOT the whole
    /// message: the point is to tell one conversation from another. A pasted file
    /// or a command envelope is not a title.</summary>
    public static string? TitleFrom(string? text)
    {
        if (text is null) return null;
        var cleaned = text.Replace("\n", " ").Trim();
        if (cleaned.Length == 0) return null;
        if (cleaned.StartsWith('<') || cleaned.StartsWith("Caveat:")) return null;
        return cleaned.Length <= 70 ? cleaned : cleaned[..69] + "…";
    }

    /// <summary>The first run of text in a message body: a string in the simple
    /// case, an array of typed parts in the rich one.</summary>
    public static string? TextIn(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String &&
                text.GetString() is { } value && value.Length > 0)
                return value;
        }
        return null;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int IntOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)property.GetDouble(),
            _ => 0,
        };
    }
}
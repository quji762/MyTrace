using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.CommandCode;

/// <summary>
/// Command Code's plan limits, credit pool and org spend limits; port of upstream
/// CommandCodeUsageService. Bills a credit balance in US dollars, layered with
/// rolling usage windows and per-organisation spend limits.
///
/// Four undocumented account routes on api.commandcode.ai, each Bearer auth, in
/// the order the CLI's own /usage overlay reads them: whoami (org id + spend
/// limits) �?billing/credits �?billing/subscriptions �?usage/summary.
///
/// What is deliberately NOT done: the CLI's hard-coded table of monthly credit
/// allowances per plan id. A number that lives in a client is not something the
/// provider reported. The pool used here is what the account actually said.
/// </summary>
public sealed class CommandCodeProvider : HttpUsageProviderBase
{
    public const string Host = "https://api.commandcode.ai";

    private readonly Func<string?, string?> _credentialResolver;

    public CommandCodeProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.CommandCode;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.CommandCode);

    // Single-call base plumbing is unused; the orchestration overrides ReadAsync.
    protected override string Endpoint => Host + "/alpha/whoami";

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context)
    {
        var pasted = _credentialResolver(account.AccountId);
        if (!string.IsNullOrWhiteSpace(pasted)) return pasted.Trim();
        if (!AccountScope.IsPrimary(account)) return null;

        // What `cmd auth login` wrote (~/.commandcode/auth.json �?apiKey). Only
        // the production file; the CLI's staging/local variants are not
        // credentials for the service reported on.
        var authFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".commandcode", "auth.json");
        if (!File.Exists(authFile)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(authFile));
            var key = document.RootElement.TryGetProperty("apiKey", out var apiKey) && apiKey.ValueKind == JsonValueKind.String
                ? apiKey.GetString()
                : null;
            return string.IsNullOrEmpty(key) ? null : key;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        var key = ResolveCredential(account, context);
        if (string.IsNullOrEmpty(key))
            return ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, "credential missing");

        // whoami first, and alone: the cheapest call, what says whether the key
        // is any good, and the org id that scopes the other three.
        var whoami = await GetAsync(Host + "/alpha/whoami?limits=1", key!, cancellationToken).ConfigureAwait(false);
        if (whoami.Body is null) return Failed(whoami);

        var whoamiBody = whoami.Body!.Value;
        var orgId = whoamiBody.TryGetProperty("org", out var org) && org.ValueKind == JsonValueKind.Object &&
                    org.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
        var orgSuffix = orgId is null ? "" : $"?orgId={Uri.EscapeDataString(orgId)}";

        var credits = await GetAsync(Host + "/alpha/billing/credits" + orgSuffix, key!, cancellationToken).ConfigureAwait(false);
        if (credits.Body is null) return Failed(credits);

        // A subscription this account has not got is not a failure �?a
        // pay-as-you-go balance is a complete answer �?so this may come back empty.
        var subscription = await GetAsync(Host + "/alpha/billing/subscriptions" + orgSuffix, key!, cancellationToken).ConfigureAwait(false);

        var periodStart = subscription.Body is { } sub && sub.TryGetProperty("data", out var subData) &&
                          subData.ValueKind == JsonValueKind.Object &&
                          subData.TryGetProperty("currentPeriodStart", out var cps)
            ? cps
            : (JsonElement?)null;
        var since = CommandCodeMapping.StampQuery(periodStart);
        var query = orgSuffix.Length == 0 ? "?" : orgSuffix + "&";
        if (since is not null)
            query += $"since={Uri.EscapeDataString(since)}";
        else if (query.EndsWith('&'))
            query = query[..^1];
        else if (query == "?")
            query = "";
        var summary = await GetAsync(
            Host + "/alpha/usage/summary" + query,
            key!, cancellationToken).ConfigureAwait(false);

        var windows = CommandCodeMapping.Windows(whoami.Body, credits.Body, subscription.Body, summary.Body);
        if (windows.Count == 0)
            return ProviderReadResult.Failed(ProviderReadHealth.SchemaChanged, "no limits reported");

        var planID = CommandCodeMapping.PlanID(credits.Body, subscription.Body);
        return ProviderReadResult.Ok(new ProviderUsage(
            Provider: ProviderId.CommandCode,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: context.Now,
            State: UsageState.Live,
            Plan: CommandCodeMapping.PlanName(planID),
            CreditBalance: credits.Body is { } cb ? CommandCodeMapping.Balance(cb) : null,
            CreditRemaining: credits.Body is { } cb2 ? CommandCodeMapping.Remaining(cb2) : null,
            Origin: UsageRoute.Endpoint));
    }

    private static ProviderReadResult Failed((JsonElement? Body, ProviderReadHealth Health, string? Detail) call) =>
        ProviderReadResult.Failed(call.Health, call.Detail);

    private async Task<(JsonElement? Body, ProviderReadHealth Health, string? Detail)> GetAsync(
        string url, string key, CancellationToken cancellationToken)
    {
        using var httpClient = HttpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return (null, ProviderReadHealth.ProviderUnavailable, "unreachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, ProviderReadHealth.ProviderUnavailable, "timeout");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var health = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        => ProviderReadHealth.Unauthorized,
                    System.Net.HttpStatusCode.TooManyRequests => ProviderReadHealth.RateLimited,
                    _ => ProviderReadHealth.ProviderUnavailable,
                };
                return (null, health, $"HTTP {(int)response.StatusCode}");
            }

            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return (JsonDocument.Parse(body).RootElement.Clone(), ProviderReadHealth.Healthy, null);
            }
            catch (Exception)
            {
                return (null, ProviderReadHealth.SchemaChanged, "unreadable reply");
            }
        }
    }

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now) =>
        throw new NotSupportedException("CommandCode orchestrates four calls in ReadAsync");
}

/// <summary>Static mapping core, test-driven against captured replies.</summary>
public static class CommandCodeMapping
{
    /// <summary>
    /// Shortest window first; ties keep the order they were built in (a stable
    /// sort �?the provider produces equal lengths as a matter of course).
    /// </summary>
    public static List<UsageWindow> Windows(JsonElement? whoami, JsonElement? credits, JsonElement? subscription, JsonElement? summary)
    {
        var built = new List<UsageWindow>();
        if (credits is { } c1) built.AddRange(RollingWindows(c1));
        if (whoami is { } w1) built.AddRange(OrgWindows(w1));
        if (credits is { } c2 && CreditWindow(c2, subscription, summary) is { } credit) built.Add(credit);

        return built
            .Select((window, offset) => (window, offset))
            .OrderBy(entry => (entry.window.WindowSeconds, entry.offset))
            .Select(entry => entry.window)
            .ToList();
    }

    /// <summary>The two rolling limits, when the account says it is subject to them.</summary>
    private static List<UsageWindow> RollingWindows(JsonElement credits)
    {
        var windows = new List<UsageWindow>();
        if (!credits.TryGetProperty("windowLimits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            return windows;
        if (!limits.TryGetProperty("limited", out var limited) || limited.ValueKind != JsonValueKind.True)
            return windows; // a cap reported for an account that is not window-limited is not a limit

        foreach (var (id, kind, seconds, node) in new[]
                 {
                     ("five-hour", UsageWindowKind.FiveHour, 5 * 3600, "fiveHour"),
                     ("weekly", UsageWindowKind.Weekly, 7 * 86400, "weekly"),
                 })
        {
            if (!limits.TryGetProperty(node, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            var cap = Num(window, "cap");
            var used = Num(window, "used");
            if (cap is not { } capValue || capValue <= 0 || used is not { } usedValue) continue;

            windows.Add(new UsageWindow(
                Id: id,
                Kind: kind,
                Scope: null,
                UsedFraction: Math.Clamp(usedValue / capValue, 0, 1),
                WindowSeconds: seconds,
                // Milliseconds, not seconds.
                ResetsAt: Num(window, "resetAt") is { } reset
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)reset)
                    : null,
                IsExhausted: usedValue >= capValue));
        }
        return windows;
    }

    /// <summary>The organisation's spend limits �?money, already counted as SPENT. No inversion.</summary>
    private static List<UsageWindow> OrgWindows(JsonElement whoami)
    {
        var windows = new List<UsageWindow>();
        if (!whoami.TryGetProperty("orgLimits", out var orgLimits) || orgLimits.ValueKind != JsonValueKind.Array)
            return windows;

        var seen = new Dictionary<string, int>();
        foreach (var limit in orgLimits.EnumerateArray())
        {
            var ceiling = Num(limit, "limit");
            var spent = Num(limit, "spent");
            // A ceiling of zero or less is not a denominator: `-1` is the usual
            // way to say unlimited. Reading it as "reached" would paint an
            // untouched organisation solid red.
            if (ceiling is not { } c || c <= 0 || spent is not { } s) continue;

            var scopeName = Str(limit, "scope");
            var named = Str(limit, "modelLabel") ?? Str(limit, "model");
            var scope = scopeName == "model" && !string.IsNullOrWhiteSpace(named) ? named!.Trim() : null;

            var (seconds, stated) = Interval(Str(limit, "resetInterval"));

            // Ids are built from what each limit IS, so a row keeps its identity
            // when the array comes back in another order.
            var identity = string.Join(".",
                new[] { scopeName ?? "org", Str(limit, "model"), Str(limit, "resetInterval") }
                    .Where(part => part is not null));
            var occurrence = seen.GetValueOrDefault(identity, 0);
            seen[identity] = occurrence + 1;
            var id = occurrence == 0 ? $"org.{identity}" : $"org.{identity}.{occurrence}";

            windows.Add(new UsageWindow(
                Id: id,
                Kind: UsageWindowKind.Spend,
                Scope: scope,
                UsedFraction: Math.Clamp(s / c, 0, 1),
                WindowSeconds: seconds,
                ResetsAt: Str(limit, "resetAt") is { } resetAt ? ParseStampDate(resetAt) : null,
                ReportsLength: stated,
                IsExhausted: limit.TryGetProperty("exceeded", out var ex) && ex.ValueKind == JsonValueKind.True));
        }
        return windows;
    }

    /// <summary>A reset interval as a length, and whether that length was actually STATED.</summary>
    public static (int Seconds, bool Stated) Interval(string? name) => name switch
    {
        "daily" => (86400, true),
        "weekly" => (7 * 86400, true),
        // A month is 28�?1 days, stored as a flat 30 for ordering only.
        "monthly" => (30 * 86400, false),
        // `total` is a lifetime cap: seconds are a sort key that puts it last.
        _ => (365 * 86400, false),
    };

    /// <summary>
    /// The monthly row: the plan's grant while one is running, the purchased
    /// balance when none. The grant is NOT reported by anything (upstream refuses
    /// the CLI's client-side table), so on a plan the remainder has no reported
    /// denominator and draws nothing �?a plan this build cannot size draws
    /// nothing rather than a guess.
    /// </summary>
    private static UsageWindow? CreditWindow(JsonElement credits, JsonElement? subscription, JsonElement? summary)
    {
        if (!credits.TryGetProperty("credits", out var pots) || pots.ValueKind != JsonValueKind.Object)
            return null;

        if (IsOnAPlan(credits, subscription))
        {
            // Remainder reported; denominator inferred �?would need Estimate
            // (planPrice), but the grant table was refused upstream, so no row.
            return null;
        }

        // Pool window: both halves must have been reported, and absent is not zero.
        var potsList = new[] { Num(pots, "monthlyCredits"), Num(pots, "purchasedCredits"), Num(pots, "freeCredits") };
        if (!potsList.Any(p => p is not null)) return null;
        var reportedSpend = summary is { } s ? Num(s, "totalCost") : null;
        if (reportedSpend is null) return null;

        var remaining = potsList.Where(p => p is not null).Sum(p => Math.Max(0, p!.Value));
        var spent = Math.Max(0, reportedSpend.Value);
        var pool = remaining + spent;
        // Nothing left and nothing spent is an account that said nothing about a
        // pool at all �?not the same as one that is empty.
        if (pool <= 0) return null;

        var (end, seconds) = BillingPeriod(subscription);

        return new UsageWindow(
            Id: "credits",
            Kind: UsageWindowKind.Spend,
            Scope: null,
            UsedFraction: Math.Clamp(spent / pool, 0, 1),
            WindowSeconds: seconds ?? 30 * 86400,
            ResetsAt: end,
            ReportsLength: seconds is not null,
            IsExhausted: remaining <= 0);
    }

    /// <summary>Whether a plan is paying for this account right now.</summary>
    public static bool IsOnAPlan(JsonElement credits, JsonElement? subscription)
    {
        // No answer is not the same as an answer of "none": the lookup may fail
        // without sinking the reading, so its silence is not evidence of no plan.
        if (subscription is not { } sub || !sub.TryGetProperty("data", out var data))
            return PlanID(credits, subscription) is not null;

        // It did answer; take it at its word in both directions.
        if (data.ValueKind != JsonValueKind.Object) return false;

        // A plan with no status is a reply whose shape has moved: treat as running.
        if (!data.TryGetProperty("status", out var status)) return true;
        return status.ValueKind == JsonValueKind.String &&
               status.GetString()!.Equals("active", StringComparison.OrdinalIgnoreCase);
    }

    private static (DateTimeOffset? End, int? Seconds) BillingPeriod(JsonElement? subscription)
    {
        if (subscription is not { } sub || !sub.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return (null, null);
        var start = Str(data, "currentPeriodStart") is { } startText ? ParseStampDate(startText) : null;
        var end = Str(data, "currentPeriodEnd") is { } endText ? ParseStampDate(endText) : null;
        var seconds = start is { } s && end is { } e && e > s ? (int)(e - s).TotalSeconds : (int?)null;
        return (end, seconds);
    }

    public static string? PlanID(JsonElement? credits, JsonElement? subscription)
    {
        var fromSub = subscription is { } sub && sub.TryGetProperty("data", out var data) &&
                      data.ValueKind == JsonValueKind.Object ? Str(data, "planId") : null;
        var fromCredits = credits is { } c && c.ValueKind == JsonValueKind.Object &&
                          c.TryGetProperty("credits", out var pots) && pots.ValueKind == JsonValueKind.Object
            ? Str(pots, "planId")
            : null;
        var id = fromSub ?? fromCredits;
        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>`individual-pro` �?"Individual Pro". Passed through tidied, not mapped.</summary>
    public static string? PlanName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return string.Join(" ", id.Split('-', '_')
            .Where(part => part.Length > 0)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    /// <summary>The same total as a number, for the low-balance warning. Nil where no pot was reported.</summary>
    public static CreditAmount? Remaining(JsonElement credits)
    {
        if (!credits.TryGetProperty("credits", out var pots) || pots.ValueKind != JsonValueKind.Object)
            return null;
        var potsList = new[] { Num(pots, "monthlyCredits"), Num(pots, "purchasedCredits"), Num(pots, "freeCredits") };
        if (!potsList.Any(p => p is not null)) return null;
        return new CreditAmount(potsList.Where(p => p is not null).Sum(p => Math.Max(0, p!.Value)), "USD");
    }

    public static string? Balance(JsonElement credits)
    {
        var remaining = Remaining(credits);
        return remaining is null ? null : $"${remaining.Amount:0.00}";
    }

    /// <summary>A billing-period boundary, which the reply may spell as a date string or epoch number.</summary>
    public static DateTimeOffset? ParseStampDate(string text)
    {
        if (DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;
        return null;
    }

    /// <summary>The period start handed back verbatim for the `since` query parameter.</summary>
    public static string? StampQuery(JsonElement? stamp)
    {
        if (stamp is not { } node) return null;
        if (node.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object) return null;
        return node.ValueKind is JsonValueKind.String or JsonValueKind.Number ? node.GetRawText().Trim('"') : null;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    public static double? Num(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(property.GetString(), out var d) => d,
            _ => null,
        };
    }
}

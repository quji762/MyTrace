using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Codex;

/// <summary>
/// Reads Codex's usage; port of upstream CodexUsageService (endpoint route).
/// The main path is what Codex's own client uses: OAuth credentials from
/// `~/.codex/auth.json` (Windows: `%USERPROFILE%\.codex\auth.json`) and
/// GET chatgpt.com/backend-api/wham/usage with the account id header.
///
/// `primary_window`/`secondary_window` are NOT tied to particular durations —
/// which windows exist depends on the plan. A window's kind comes from its
/// duration, never from which slot it arrived in. "Limit reached" is reported for
/// a whole GROUP, and the mark goes on the fullest window of that group only.
/// </summary>
public sealed class CodexProvider : HttpUsageProviderBase
{
    public const string EndpointUrl = "https://chatgpt.com/backend-api/wham/usage";

    private readonly Func<string?, string?> _credentialResolver;
    private readonly Func<(string AccessToken, string AccountID)?>? _cliCredentialsLocator;

    public CodexProvider(Func<string?, string?>? credentialResolver = null, Func<(string AccessToken, string AccountID)?>? cliCredentialsLocator = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
        _cliCredentialsLocator = cliCredentialsLocator;
    }

    public override ProviderId Id => ProviderId.Codex;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Codex);
    protected override string Endpoint => EndpointUrl;

    // Per-call context: concurrent primary/added reads must not share one
    // ChatGPT-Account-Id. AsyncLocal flows with the read's async chain.
    private static readonly AsyncLocal<(string Token, string AccountID)?> Borrowed = new();
    private readonly object _appServerGate = new();
    private CodexAppServerClient? _appServer;

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context)
    {
        var pasted = _credentialResolver(account.AccountId);
        if (!string.IsNullOrWhiteSpace(pasted))
        {
            // A vault token is not the CLI login. Leaving Borrowed set would
            // attach the CLI's ChatGPT-Account-Id to somebody else's token.
            Borrowed.Value = null;
            return pasted.Trim();
        }

        if (!AccountScope.IsPrimary(account))
        {
            Borrowed.Value = null;
            return null;
        }

        Borrowed.Value = _cliCredentialsLocator?.Invoke() ?? LocateCliCredentials();
        return Borrowed.Value?.Token;
    }

    public override async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        var endpoint = await base.ReadAsync(account, context, cancellationToken).ConfigureAwait(false);

        // Pinned to the endpoint, a dead token is reported rather than quietly
        // answered by the app server. Pinned to the app server, an endpoint
        // success is not the reading.
        return await RoutePinning.SelectAsync(account.Pin, endpoint, async () =>
        {
            if (!AccountScope.IsPrimary(account))
                return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "app-server unavailable");
            return await FetchViaAppServerAsync(account, context, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>The `codex app-server` fallback: the CLI's own documented protocol.</summary>
    private async Task<ProviderReadResult> FetchViaAppServerAsync(MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        try
        {
            lock (_appServerGate)
                _appServer ??= new CodexAppServerClient();
            var rateLimits = await _appServer.RateLimitsAsync(cancellationToken).ConfigureAwait(false);
            var usage = CodexMapping.ParseAppServerResponse(rateLimits, account.AccountId, context.Now);
            return usage.Windows.Count > 0
                ? ProviderReadResult.Ok(usage with { Origin = UsageRoute.AppServer })
                : ProviderReadResult.Failed(ProviderReadHealth.SchemaChanged, "no limits reported");
        }
        catch (CodexAppServerClient.AppServerException ex)
        {
            return ex.Kind switch
            {
                // Neither route is open: no usable token, and no CLI to ask.
                CodexAppServerClient.FailureKind.ExecutableNotFound =>
                    ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, "sign in required"),
                CodexAppServerClient.FailureKind.TimedOut =>
                    ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "timeout"),
                CodexAppServerClient.FailureKind.Server when IsAuth(ex.Message) =>
                    ProviderReadResult.Failed(ProviderReadHealth.Unauthorized, "sign in required"),
                _ => ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, ex.Message),
            };
        }
        catch (Exception)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "server error");
        }
    }

    private static bool IsAuth(string? message)
    {
        if (message is null) return false;
        string[] words = ["auth", "login", "sign in", "unauthor", "credential"];
        return words.Any(message.ToLowerInvariant().Contains);
    }

    protected override HttpRequestMessage BuildRequest(string credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, EndpointOrDefault);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Accept.ParseAdd("application/json");
        // Sent only when there is one. An empty header is not the same as no
        // header: it names no account, and on an added account the service could
        // answer for the OTHER login's figures.
        if (Borrowed.Value is { } borrowed && borrowed.AccountID.Length > 0)
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", borrowed.AccountID);
        return request;
    }

    /// <summary>
    /// The stored CLI login on Windows: `%USERPROFILE%\.codex\auth.json`,
    /// `tokens.access_token` + `tokens.account_id`.
    /// </summary>
    public static (string AccessToken, string AccountID)? LocateCliCredentials(string? userProfile = null)
    {
        try
        {
            var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(home, ".codex", "auth.json");
            if (!File.Exists(path)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                return null;
            var token = tokens.TryGetProperty("access_token", out var accessToken) && accessToken.ValueKind == JsonValueKind.String
                ? accessToken.GetString()
                : null;
            var account = tokens.TryGetProperty("account_id", out var accountID) && accountID.ValueKind == JsonValueKind.String
                ? accountID.GetString()
                : null;
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(account)) return null;
            return (token!, account!);
        }
        catch (Exception)
        {
            return null;
        }
    }

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now) =>
        CodexMapping.ParseUsageResponse(document.RootElement, account.AccountId, now);
}

/// <summary>Static mapping core, test-driven against captured replies.</summary>
public static class CodexMapping
{
    public static ProviderUsage ParseUsageResponse(JsonElement root, string accountId, DateTimeOffset now)
    {
        var windows = new List<UsageWindow>();

        // Account-wide limits, which the server leaves unnamed.
        var spendReached = root.TryGetProperty("spend_control", out var spendControl) &&
                           spendControl.ValueKind == JsonValueKind.Object &&
                           spendControl.TryGetProperty("reached", out var reached) &&
                           reached.ValueKind == JsonValueKind.True;

        if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
        {
            var groupSpent = IsGroupSpent(rateLimit)
                || (root.TryGetProperty("rate_limit_reached_type", out var rlrt) && rlrt.ValueKind != JsonValueKind.Null);
            windows.AddRange(MarkingSpent(HttpWindows(rateLimit, "codex", null), groupSpent || spendReached));
        }

        // Then per-model limits, which it does name.
        if (root.TryGetProperty("additional_rate_limits", out var extras) && extras.ValueKind == JsonValueKind.Array)
        {
            foreach (var extra in extras.EnumerateArray())
            {
                if (!extra.TryGetProperty("rate_limit", out var limit) || limit.ValueKind != JsonValueKind.Object)
                    continue;
                var label = Str(extra, "limit_name");
                var key = Str(extra, "metered_feature") ?? label ?? "extra";
                windows.AddRange(MarkingSpent(HttpWindows(limit, key!, label), IsGroupSpent(limit)));
            }
        }

        var plan = Str(root, "plan_type") is { } planType ? PlanName(planType) : null;
        string? creditBalance = null;
        if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
        {
            var unlimited = credits.TryGetProperty("unlimited", out var unlim) && unlim.ValueKind == JsonValueKind.True;
            if (!unlimited) creditBalance = Str(credits, "balance");
        }

        return new ProviderUsage(
            Provider: ProviderId.Codex,
            AccountId: accountId,
            Windows: windows,
            ObservedAt: now,
            State: windows.Count == 0 ? UsageState.Unavailable : UsageState.Live,
            Plan: plan,
            CreditBalance: creditBalance,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint)
        {
            Unavailability = windows.Count == 0
                ? new Unavailability(UnavailabilityKind.SchemaChanged, "no limits reported")
                : null,
        };
    }

    private static List<UsageWindow> HttpWindows(JsonElement limit, string idPrefix, string? scope)
    {
        var windows = new List<UsageWindow>();
        foreach (var slot in new[] { "primary_window", "secondary_window" })
        {
            if (!limit.TryGetProperty(slot, out var node) || node.ValueKind != JsonValueKind.Object) continue;
            var percent = Num(node, "used_percent");
            if (percent is null) continue;

            var seconds = Num(node, "limit_window_seconds") is { } s ? (int)s : (int?)null;
            var resets = Num(node, "reset_at") is { } r ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds((long)r) : null;

            windows.Add(new UsageWindow(
                Id: $"{idPrefix}.{slot}",
                Kind: seconds is { } sec ? KindForSeconds(sec) : UsageWindowKind.Other,
                Scope: scope,
                UsedFraction: Math.Clamp(percent.Value, 0, 100) / 100,
                WindowSeconds: seconds ?? 0,
                ResetsAt: resets));
        }
        return windows;
    }

    private static bool IsGroupSpent(JsonElement limit)
    {
        if (limit.TryGetProperty("limit_reached", out var lr) && lr.ValueKind == JsonValueKind.True) return true;
        if (limit.TryGetProperty("allowed", out var allowed) && allowed.ValueKind == JsonValueKind.False) return true;
        return false;
    }

    /// <summary>
    /// Marks the group's MOST-USED window as spent: "limit reached" is reported
    /// for the group, and a group can hold both a 5-hour and a weekly window with
    /// only one of them the reason.
    /// </summary>
    public static List<UsageWindow> MarkingSpent(List<UsageWindow> windows, bool spent)
    {
        if (!spent || windows.Count == 0) return windows;
        var fullestId = windows.MaxBy(w => w.UsedFraction)!.Id;
        return windows.Select(w => w.Id == fullestId ? w with { IsExhausted = true } : w).ToList();
    }

    public static UsageWindowKind KindForSeconds(int seconds) => seconds switch
    {
        18000 => UsageWindowKind.FiveHour,
        604800 => UsageWindowKind.Weekly,
        _ => UsageWindowKind.Other,
    };

    /// <summary>
    /// The shape `account/rateLimits/read` returns, which names its fields
    /// differently from the HTTP endpoint: groups keyed by limit id, each with
    /// primary/secondary windows carrying usedPercent/windowDurationMins/resetsAt.
    /// `ordinaryUsageAllowed` is the server's own last word on whether ordinary
    /// included usage may still be spent — nil means "unavailable", which is
    /// explicitly not "no": the protocol says clients must not infer recovery
    /// from percentages or reset times.
    /// </summary>
    public static ProviderUsage ParseAppServerResponse(JsonElement result, string accountId, DateTimeOffset now)
    {
        // Groups: a rateLimitsByLimitId map, or a flat rateLimits object treated
        // as the one unnamed account-wide group.
        var groups = new List<KeyValuePair<string, JsonElement>>();
        if (result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
            {
                foreach (var group in byId.EnumerateObject())
                    groups.Add(new KeyValuePair<string, JsonElement>(group.Name, group.Value.Clone()));
            }
            else if (result.TryGetProperty("rateLimits", out var flat) && flat.ValueKind == JsonValueKind.Object)
            {
                groups.Add(new KeyValuePair<string, JsonElement>("codex", flat.Clone()));
            }
        }

        // Unnamed account-wide group first, then named per-model ones in a
        // stable order, so rows don't jump around between refreshes.
        var ordered = groups
            .Select((kv, index) => (kv, index))
            .OrderBy(entry =>
            {
                var named = entry.kv.Value.ValueKind == JsonValueKind.Object &&
                            entry.kv.Value.TryGetProperty("limitName", out _);
                return (named ? 1 : 0, entry.kv.Key);
            })
            .Select(entry => entry.kv)
            .ToList();

        // The server's own last word, applying to ALL groups: a sibling of them,
        // not a member of one.
        var ordinaryRefused = result.ValueKind == JsonValueKind.Object &&
                              result.TryGetProperty("ordinaryUsageAllowed", out var oua) &&
                              oua.ValueKind == JsonValueKind.False;

        var windows = new List<UsageWindow>();
        string? plan = null;
        string? credits = null;

        foreach (var (key, group) in ordered)
        {
            if (group.ValueKind != JsonValueKind.Object) continue;
            var scope = Str(group, "limitName");

            // This group's windows, marked spent group-scoped — the flag sits on
            // the snapshot that holds the windows, never smeared across groups.
            var ofThisGroup = new List<UsageWindow>();
            foreach (var slot in new[] { "primary", "secondary" })
            {
                if (!group.TryGetProperty(slot, out var node) || node.ValueKind != JsonValueKind.Object) continue;
                var percent = Num(node, "usedPercent");
                if (percent is null) continue;

                var minutes = Num(node, "windowDurationMins") is { } m ? (int)m : (int?)null;
                var resets = Num(node, "resetsAt") is { } r
                    ? DateTimeOffset.FromUnixTimeSeconds((long)r)
                    : (DateTimeOffset?)null;

                ofThisGroup.Add(new UsageWindow(
                    Id: $"{key}.{slot}",
                    Kind: minutes is { } mins ? KindForSeconds(mins * 60) : UsageWindowKind.Other,
                    Scope: scope,
                    UsedFraction: Math.Clamp(percent.Value, 0, 100) / 100,
                    WindowSeconds: (minutes ?? 0) * 60,
                    ResetsAt: resets));
            }

            var groupSpent = (group.TryGetProperty("spendControlReached", out var scr) && scr.ValueKind == JsonValueKind.True)
                || (group.TryGetProperty("rateLimitReachedType", out var rlrt) && rlrt.ValueKind != JsonValueKind.Null)
                || ordinaryRefused;
            windows.AddRange(MarkingSpent(ofThisGroup, groupSpent));

            plan ??= Str(result, "planType") ?? Str(group, "planType");
            if (credits is null && group.TryGetProperty("credits", out var creditNode) &&
                creditNode.ValueKind == JsonValueKind.Object)
            {
                var unlimited = creditNode.TryGetProperty("unlimited", out var unlim) && unlim.ValueKind == JsonValueKind.True;
                if (!unlimited) credits = Str(creditNode, "balance");
            }
        }

        return new ProviderUsage(
            Provider: ProviderId.Codex,
            AccountId: accountId,
            Windows: windows,
            ObservedAt: now,
            State: windows.Count == 0 ? UsageState.Unavailable : UsageState.Live,
            Plan: plan is { } p ? PlanName(p) : null,
            CreditBalance: credits,
            CreditRemaining: null,
            Origin: UsageRoute.AppServer)
        {
            Unavailability = windows.Count == 0
                ? new Unavailability(UnavailabilityKind.SchemaChanged, "no limits reported")
                : null,
        };
    }

    /// <summary>What the plan is actually called, from the internal tier name.</summary>
    public static string PlanName(string raw) => raw.ToLowerInvariant() switch
    {
        "free" => "Free",
        "go" => "Go",
        "plus" => "Plus",
        "pro" => "Pro",
        "prolite" => "Pro 5x",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "edu" => "Edu",
        _ => raw,
    };

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static double? Num(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var d) => d,
            _ => null,
        };
    }
}

using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Volcengine;

/// <summary>
/// The Volcengine Coding Plan; port of upstream VolcengineUsageService.
/// Two routes, neither a browser session: the `arkcli` CLI (subprocess; lands with
/// the Windows tooling locator) and an AccessKeyID:SecretAccessKey pair signed
/// against Top OpenAPI. Keys preferred over the CLI (account identity: arkcli
/// carries an ambient SSO session that may be a different account).
///
/// Response shapes are second-hand (CodexBar's parser, upstream fixtures) — the
/// parsing is fixture-covered and the failure copy is specific.
/// </summary>
public sealed class VolcengineProvider : HttpUsageProviderBase
{
    private static readonly Uri CodingPlanUrl =
        new("https://open.volcengineapi.com/?Action=GetCodingPlanUsage&Version=2024-01-01");
    private static readonly Uri AgentPlanUrl =
        new("https://open.volcengineapi.com/?Action=GetAFPUsage&Version=2024-01-01");

    private readonly Func<string?, string?> _credentialResolver;

    public VolcengineProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.Volcengine;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Volcengine);

    protected override string Endpoint => CodingPlanUrl.ToString();

    private VolcengineCredentials? Credentials;
    private bool HasUnreadableKey;

    public override async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        var entered = _credentialResolver(account.Label);
        Credentials = VolcengineCredentials.FromEntered(entered);
        var enteredNonEmpty = !string.IsNullOrWhiteSpace(entered);
        // Something was pasted and it is not a pair: told apart from nothing
        // pasted, because the remedies are opposite.
        HasUnreadableKey = Credentials is null && enteredNonEmpty;

        if (Credentials is null)
        {
            return ProviderReadResult.Failed(
                HasUnreadableKey ? ProviderReadHealth.Unauthorized : ProviderReadHealth.CredentialExpired,
                HasUnreadableKey ? "key is not an AccessKeyID:SecretAccessKey pair" : "credential missing");
        }

        // The two actions are independent: a missing Agent Plan must not lose the
        // Coding Plan's windows. A refusal on BOTH is a refusal.
        var coding = await AskAsync(CodingPlanUrl, context, cancellationToken).ConfigureAwait(false);
        var agent = await AskAsync(AgentPlanUrl, context, cancellationToken).ConfigureAwait(false);

        if (coding.Usage is null && agent.Usage is null)
        {
            // The more authoritative refusal wins: reporting a network failure
            // while the other action said the keys were refused would let a wrong
            // key fall through to another account's figures.
            var refused = new[] { coding, agent }.FirstOrDefault(r =>
                r.Health is ProviderReadHealth.Unauthorized or ProviderReadHealth.RateLimited);
            return refused ?? coding;
        }

        var windows = (coding.Usage?.Windows ?? []).Concat(agent.Usage?.Windows ?? []).ToList();
        if (windows.Count == 0)
            return ProviderReadResult.Failed(ProviderReadHealth.SchemaChanged, "no limits reported");

        return ProviderReadResult.Ok(new ProviderUsage(
            Provider: ProviderId.Volcengine,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: context.Now,
            State: UsageState.Live,
            Plan: null,
            CreditBalance: null,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint));
    }

    protected override HttpRequestMessage BuildRequest(string credential)
    {
        // `credential` is the pinned URL; credentials are in this.Credentials.
        var url = new Uri(credential);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in VolcengineSigner.Headers(
                     "GET", url, Array.Empty<byte>(),
                     "application/x-www-form-urlencoded; charset=utf-8",
                     Credentials!, DateTimeOffset.Now))
            request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    private async Task<ProviderReadResult> AskAsync(
        Uri url, ProviderReadContext context, CancellationToken cancellationToken)
    {
        _pinnedUrl = url.ToString();
        return await base.ReadAsync(AccountStub, context, cancellationToken).ConfigureAwait(false);
    }

    private string? _pinnedUrl;
    private static readonly MonitoredAccount AccountStub = new() { Provider = ProviderId.Volcengine, AccountId = "volcengine" };

    protected override string EndpointOrDefault => _pinnedUrl ?? Endpoint;

    // The pair check happened in ReadAsync; the base guard just needs a
    // non-empty marker — never the actual secret, which travels in headers
    // built by BuildRequest.
    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context) =>
        Credentials is null ? null : "signed";

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        // Two shapes: CodingPlan (Result.QuotaUsage[Level/Percent/ResetTimestamp])
        // and AgentPlan (Result.AFPFiveHour/AFPWeekly/AFPMonthly with Quota/Used/ResetTime).
        if (root.TryGetProperty("Result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("QuotaUsage", out var quotaUsage) && quotaUsage.ValueKind == JsonValueKind.Array)
                return CodingSuccess(quotaUsage, account, now);
            return AgentSuccess(result, account, now);
        }
        throw new SchemaException("unrecognized reply shape");
    }

    private ProviderUsage CodingSuccess(JsonElement quotaUsage, MonitoredAccount account, DateTimeOffset now)
    {
        var windows = new List<UsageWindow>();
        foreach (var quota in quotaUsage.EnumerateArray())
        {
            var level = GetString(quota, "Level");
            var percent = GetDouble(quota, "Percent");
            if (level is null || percent is null) continue;
            if (VolcengineMapping.Window(
                    $"coding-plan.{level.ToLowerInvariant()}", level, percent.Value, "Coding Plan",
                    GetDouble(quota, "ResetTimestamp") is { } ts ? VolcengineMapping.FromEpoch(ts) : null) is { } window)
                windows.Add(window);
        }
        windows.Sort((a, b) => a.WindowSeconds.CompareTo(b.WindowSeconds));
        return Success(windows, account, now);
    }

    private ProviderUsage AgentSuccess(JsonElement result, MonitoredAccount account, DateTimeOffset now)
    {
        var windows = new List<UsageWindow>();
        foreach (var (label, key) in new[] { ("5h", "AFPFiveHour"), ("weekly", "AFPWeekly"), ("monthly", "AFPMonthly") })
        {
            if (!result.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            var quota = GetDouble(window, "Quota");
            var used = GetDouble(window, "Used");
            // A quota of zero is not a full window, it is a plan that has no such
            // window. Dividing by it would report 100% used of nothing.
            if (quota is not { } q || q <= 0 || used is not { } u) continue;

            if (VolcengineMapping.Window(
                    $"agent-plan.{label}", label, u / q * 100, "Agent Plan",
                    GetDouble(window, "ResetTime") is { } ts ? VolcengineMapping.FromEpoch(ts) : null) is { } w)
                windows.Add(w);
        }
        windows.Sort((a, b) => a.WindowSeconds.CompareTo(b.WindowSeconds));
        return Success(windows, account, now);
    }

    private static ProviderUsage Success(List<UsageWindow> windows, MonitoredAccount account, DateTimeOffset now) =>
        new(ProviderId.Volcengine, account.AccountId, windows, now, UsageState.Live, null, null, null, UsageRoute.Endpoint);
}

public static class VolcengineMapping
{
    /// <summary>
    /// A window whose label cannot be read is left out rather than guessed at.
    /// `reportsLength` is false for monthly on purpose: a month is 28–31 days,
    /// so 30 is a sort key and not a measurement.
    /// </summary>
    public static UsageWindow? Window(string id, string label, double usedPercent, string scope, DateTimeOffset? resetsAt)
    {
        UsageWindowKind kind;
        int seconds;
        var reportsLength = true;

        switch (label.ToLowerInvariant())
        {
            case "5h" or "5-hour" or "five_hour" or "session":
                kind = UsageWindowKind.FiveHour;
                seconds = 5 * 3600;
                break;
            case "weekly" or "week":
                kind = UsageWindowKind.Weekly;
                seconds = 7 * 86400;
                break;
            case "monthly" or "month":
                kind = UsageWindowKind.Monthly;
                seconds = 30 * 86400;
                reportsLength = false;
                break;
            default:
                return null;
        }

        var used = Math.Clamp(usedPercent / 100, 0, 1);
        return new UsageWindow(
            Id: id,
            Kind: kind,
            Scope: scope,
            UsedFraction: used,
            WindowSeconds: seconds,
            ResetsAt: resetsAt,
            ReportsLength: reportsLength,
            // Ark reports no "you are blocked" flag of its own, so the only honest
            // signal is its own figure reaching its own ceiling.
            IsExhausted: used >= 1);
    }

    /// <summary>`reset_at`/`updated_at` have shipped as ISO strings AND as numbers, the
    /// number as both seconds and milliseconds. Guessing by magnitude at 1e11.</summary>
    public static DateTimeOffset? FromEpoch(double value)
    {
        if (value <= 0) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(value >= 1e11 ? value : value * 1000));
    }
}

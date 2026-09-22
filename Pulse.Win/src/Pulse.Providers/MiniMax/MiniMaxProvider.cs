using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.MiniMax;

/// <summary>
/// The MiniMax Coding Plan's limits; port of upstream MiniMaxUsageService.
/// Two providers, one service: api.minimax.io (international) and
/// api.minimaxi.com (mainland) — separate accounts, a key for one refused by the other.
///
/// Three reply facts the parser depends on:
/// - It reports what is LEFT (`current_*_remaining_percent` at 96 means 4% spent);
///   inverted once here so downstream stays in terms of used.
/// - Numbers arrive as strings or numbers interchangeably; every figure goes
///   through a reader that takes either.
/// - Lanes that are not part of the subscription come back with status 3, zero
///   counts and 100% remaining; read literally that is a ring pinned at 0% for a
///   thing the account cannot use, so they are left out.
/// </summary>
public sealed class MiniMaxProvider : HttpUsageProviderBase
{
    private readonly ProviderId _id;
    private readonly Func<string?, string?> _credentialResolver;

    public MiniMaxProvider(ProviderId id, Func<string?, string?>? credentialResolver = null)
    {
        _id = id;
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => _id;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(_id);

    private string ApiHost => _id == ProviderId.MiniMaxCN ? "https://api.minimaxi.com" : "https://api.minimax.io";

    protected override string Endpoint => ApiHost + "/v1/token_plan/remains";

    /// <summary>The current path first, then the one it replaced.</summary>
    private string[] Endpoints =>
    [
        ApiHost + "/v1/token_plan/remains",
        ApiHost + "/v1/api/openplatform/coding_plan/remains",
    ];

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context) =>
        _credentialResolver(account.AccountId);

    // The base class sends one request to `Endpoint`; the fallback to the older
    // path lives here by overriding ReadAsync to try both paths on any failure,
    // keeping the FIRST reason (a refused key from the current path is what the
    // user can act on and must not be masked by a server error from the other).
    public override async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        var endpoints = Endpoints;
        var first = await AttemptOnce(endpoints[0], account, context, cancellationToken).ConfigureAwait(false);
        if (first.Usage is not null ||
            first.Health is not (ProviderReadHealth.ProviderUnavailable or ProviderReadHealth.SchemaChanged))
            return first;

        var second = await AttemptOnce(endpoints[1], account, context, cancellationToken).ConfigureAwait(false);
        return second.Usage is not null ? second : first;
    }

    private async Task<ProviderReadResult> AttemptOnce(
        string endpoint, MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        // Reuse the base plumbing by temporarily pinning the endpoint.
        _pinnedEndpoint = endpoint;
        try
        {
            return await base.ReadAsync(account, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pinnedEndpoint = null;
        }
    }

    private string? _pinnedEndpoint;
    protected override string EndpointOrDefault => _pinnedEndpoint ?? Endpoint;

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SchemaException("root is not an object");

        // The service's own verdict, which is not the HTTP status: a refused key
        // comes back as a perfectly good 200 with a non-zero status here.
        // 1004 is the credential one; the rest are the service having a bad day.
        if (root.TryGetProperty("base_resp", out var baseResp) && baseResp.ValueKind == JsonValueKind.Object)
        {
            var status = (int?)GetDouble(baseResp, "status_code") ?? 0;
            if (status != 0)
            {
                var said = (GetString(baseResp, "status_msg") ?? "").ToLowerInvariant();
                string[] credentialWords = ["token", "auth", "login", "cookie", "credential"];
                var credential = status == 1004 || credentialWords.Any(said.Contains);
                throw new EnvelopeException(credential ? UnavailabilityKind.Unauthorized : UnavailabilityKind.ProviderUnavailable);
            }
        }

        // The payload is not always wrapped: some replies put `data` at the root.
        var payload = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : root;

        var windows = MiniMaxMapping.Windows(payload);
        if (windows.Count == 0)
            throw new SchemaException("no limits reported");

        return new ProviderUsage(
            Provider: _id,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: now,
            State: UsageState.Live,
            Plan: MiniMaxMapping.FirstString(payload, "current_subscribe_title", "plan_name", "combo_title", "current_plan_title"),
            CreditBalance: MiniMaxMapping.Balance(payload),
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint);
    }

    /// <summary>An envelope refusal with its classified reason.</summary>
    public sealed class EnvelopeException : Exception
    {
        public UnavailabilityKind Kind { get; }

        public EnvelopeException(UnavailabilityKind kind) : base(kind.ToString()) => Kind = kind;
    }
}

/// <summary>Static mapping core so tests can drive it without a transport.</summary>
public static class MiniMaxMapping
{
    public static List<UsageWindow> Windows(JsonElement payload)
    {
        var models = payload.TryGetProperty("model_remains", out var remains) && remains.ValueKind == JsonValueKind.Array
            ? remains
            : default;

        var windows = new List<UsageWindow>();
        if (models.ValueKind != JsonValueKind.Array) return windows;

        var index = 0;
        foreach (var model in models.EnumerateArray())
        {
            var name = model.TryGetProperty("model_name", out var mn) && mn.ValueKind == JsonValueKind.String
                ? mn.GetString()?.Trim()
                : null;
            if (name is { Length: 0 }) name = null;

            // "general" is the plan itself rather than a model, so it is unscoped.
            var scope = name?.ToLowerInvariant() == "general" ? null : name;

            // The id has to be unique within one reading; position settles it
            // when the name cannot.
            var key = name ?? $"lane{index}";

            if (Interval(model, scope, key) is { } interval) windows.Add(interval);
            if (Weekly(model, scope, key) is { } weekly) windows.Add(weekly);
            index++;
        }

        windows.Sort((a, b) => a.WindowSeconds.CompareTo(b.WindowSeconds));
        return windows;
    }

    /// <summary>The short window; its length is measured from the timestamps rather than assumed.</summary>
    private static UsageWindow? Interval(JsonElement model, string? scope, string key)
    {
        if (IsUnavailable(
                Num(model, "current_interval_status"),
                Num(model, "current_interval_total_count"),
                Num(model, "current_interval_remaining_percent")))
            return null;

        var used = Spent(
            Num(model, "current_interval_remaining_percent"),
            Num(model, "current_interval_total_count"),
            Num(model, "current_interval_usage_count"));
        var start = Num(model, "start_time");
        var end = Num(model, "end_time");
        if (used is null || start is null || end is null || end <= start) return null;

        // Sub-second intervals would floor to zero and read as "0-hour limit".
        var seconds = (int)((end.Value - start.Value) / 1000);
        if (seconds <= 0) return null;

        return new UsageWindow(
            Id: $"{key}.interval",
            Kind: KindForSeconds(seconds),
            Scope: scope,
            UsedFraction: used.Value,
            WindowSeconds: seconds,
            ResetsAt: DateTimeOffset.FromUnixTimeMilliseconds((long)end.Value));
    }

    /// <summary>The weekly window names its own length, so it survives missing timestamps.</summary>
    private static UsageWindow? Weekly(JsonElement model, string? scope, string key)
    {
        if (IsUnavailable(
                Num(model, "current_weekly_status"),
                Num(model, "current_weekly_total_count"),
                Num(model, "current_weekly_remaining_percent")))
            return null;

        var used = Spent(
            Num(model, "current_weekly_remaining_percent"),
            Num(model, "current_weekly_total_count"),
            Num(model, "current_weekly_usage_count"));
        if (used is null) return null;

        return new UsageWindow(
            Id: $"{key}.weekly",
            Kind: UsageWindowKind.Weekly,
            Scope: scope,
            UsedFraction: used.Value,
            WindowSeconds: 7 * 86400,
            ResetsAt: Num(model, "weekly_end_time") is { } weeklyEnd
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)weeklyEnd)
                : null);
    }

    /// <summary>Status 3 with nothing issued and nothing spent = a lane the plan does not include.</summary>
    public static bool IsUnavailable(double? status, double? total, double? remainingPercent) =>
        status == 3 && (total ?? 0) == 0 && (remainingPercent ?? 0) >= 100;

    /// <summary>
    /// Everything here is stated as what is LEFT: 96 remaining is 4 spent, and
    /// `current_*_usage_count` is the remaining quota, not the used one —
    /// reading it as a spend inverts every figure. Percentage preferred; counts
    /// are the fallback (the older endpoint returns only those).
    /// </summary>
    public static double? Spent(double? percentRemaining, double? total, double? left)
    {
        if (percentRemaining is { } pr) return Math.Clamp(100 - pr, 0, 100) / 100;
        if (total is { } t && t > 0 && left is { } l)
            return Math.Clamp((t - l) / t, 0, 1);
        return null;
    }

    public static UsageWindowKind KindForSeconds(int seconds) => seconds switch
    {
        5 * 3600 => UsageWindowKind.FiveHour,
        7 * 86400 => UsageWindowKind.Weekly,
        30 * 86400 => UsageWindowKind.Monthly,
        _ => UsageWindowKind.Other,
    };

    public static string? FirstString(JsonElement payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty(key, out var property) &&
                property.ValueKind == JsonValueKind.String &&
                property.GetString() is { } text && text.Trim().Length > 0)
                return text.Trim();
        }
        return null;
    }

    public static string? Balance(JsonElement payload)
    {
        string[] keys = ["points_balance", "point_balance", "credits_balance", "credit_balance", "balance"];
        foreach (var key in keys)
        {
            if (Num(payload, key) is { } points && points > 0)
                return $"{points:N0} points";
        }
        return null;
    }

    /// <summary>Every figure can be a string or a number; nothing may assume which.</summary>
    public static double? Num(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(property.GetString()?.Trim(), out var d) => d,
            _ => null,
        };
    }
}

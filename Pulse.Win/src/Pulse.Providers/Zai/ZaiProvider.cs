using System.Globalization;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Zai;

/// <summary>
/// The GLM Coding Plan's limits; port of upstream ZaiUsageService.
/// Two providers, one service: Z.ai (international) and BigModel (mainland) are
/// the same company's storefronts answering the same JSON at the same path on
/// different hosts — separate accounts, separate keys.
///
/// The reply wraps its payload in a status of its own: `success` and `code` must
/// both say 200 even when HTTP did. A refused key arrives as HTTP 200 with
/// `success: false`, so reading only the status line would report an empty plan
/// rather than a bad key.
/// </summary>
public sealed class ZaiProvider : HttpUsageProviderBase
{
    private readonly ProviderId _id;
    private readonly Func<string?, string?> _credentialResolver;

    public ZaiProvider(ProviderId id, Func<string?, string?>? credentialResolver = null)
    {
        _id = id == ProviderId.GlmCoding ? ProviderId.GlmCoding : ProviderId.Zai;
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => _id;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(_id);
    protected override string Endpoint => Host + "/api/monitor/usage/quota/limit";

    private string Host => _id == ProviderId.GlmCoding ? "https://open.bigmodel.cn" : "https://api.z.ai";

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context) =>
        _credentialResolver(account.Label);

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SchemaException("root is not an object");

        // The envelope's own verdict, read before anything else.
        var success = !root.TryGetProperty("success", out var successElement) || successElement.ValueKind != JsonValueKind.False;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var c) ? c : 0;
        var msg = GetString(root, "msg");

        if (!success || code != 200)
        {
            throw new EnvelopeException(Problem(code, msg));
        }

        var hasPayload = root.TryGetProperty("data", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.Object;

        var windows = new List<UsageWindow>();
        if (hasPayload && payloadElement.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var limit in limits.EnumerateArray())
            {
                if (WindowFrom(limit, index) is { } window) windows.Add(window);
                index++;
            }
        }

        // Shortest first, so a five-hour limit is read before a weekly one.
        windows.Sort((a, b) => a.WindowSeconds.CompareTo(b.WindowSeconds));

        if (windows.Count == 0)
            throw new SchemaException("no limits reported");

        return new ProviderUsage(
            Provider: _id,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: now,
            State: UsageState.Live,
            Plan: hasPayload ? PlanLabel(payloadElement) : null,
            CreditBalance: null,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint);
    }

    /// <summary>
    /// What the envelope's refusal actually was. The mainland host answers in
    /// Chinese, so the keyword list carries both languages; Zhipu's own
    /// 1000-series is authentication.
    /// </summary>
    public static UnavailabilityKind Problem(int code, string? msg)
    {
        var said = (msg ?? "").ToLowerInvariant();

        // A working key on an account with no running subscription answers
        // `500` with this sentence; 500 alone would say the service broke.
        if (said.Contains("coding plan")) return UnavailabilityKind.NotConfigured;

        string[] authWords =
        [
            "token", "auth", "key", "unauthor", "forbidden", "credential",
            "身份验证", "鉴权", "认证", "令牌", "未授权", "无权限", "密钥",
        ];
        if (authWords.Any(said.Contains)) return UnavailabilityKind.Unauthorized;

        return code switch
        {
            401 or 403 => UnavailabilityKind.Unauthorized,
            429 => UnavailabilityKind.RateLimited,
            >= 1000 and <= 1099 => UnavailabilityKind.Unauthorized,
            _ => UnavailabilityKind.ProviderUnavailable,
        };
    }

    private UsageWindow? WindowFrom(JsonElement limit, int index)
    {
        var type = GetString(limit, "type");
        // Only these three carry a quota; anything else is left out rather than
        // shown under a heading guessed at.
        if (type is not ("TOKENS_LIMIT" or "CREDIT_LIMIT" or "TIME_LIMIT"))
            return null;

        var unit = GetInt(limit, "unit");
        var number = GetInt(limit, "number");
        if (unit is null || number is null) return null;

        var minutes = Minutes(type!, unit.Value, number.Value);
        if (minutes is null) return null;
        var used = UsedFraction(limit);
        if (used is null) return null;

        var seconds = minutes.Value * 60;
        return new UsageWindow(
            // The position is in the id because two limits can share a type and
            // a duration; ids are what pinned windows match on.
            Id: $"{_id}.{type}.{unit}-{number}.{index}",
            Kind: KindForMinutes(minutes.Value),
            // The MCP lane is a different allowance from the coding quota.
            Scope: type == "TIME_LIMIT" ? "MCP" : null,
            UsedFraction: used.Value,
            WindowSeconds: seconds,
            ResetsAt: GetDouble(limit, "nextResetTime") is { } ms && ms > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
                : null);
    }

    /// <summary>Window length from a unit code and a number of them; unknown unit drops.</summary>
    public static int? Minutes(string type, int unit, int number)
    {
        // A monthly MCP allowance is reported as "1 minute" — a marker, not a
        // duration; taken literally it would sort above a five-hour limit.
        if (type == "TIME_LIMIT" && unit == 5 && number == 1) return 30 * 24 * 60;

        var perUnit = unit switch { 1 => 1440, 3 => 60, 5 => 1, 6 => 10080, _ => 0 };
        if (number <= 0 || perUnit == 0) return null;
        return number * perUnit;
    }

    public static UsageWindowKind KindForMinutes(int minutes) => minutes switch
    {
        300 => UsageWindowKind.FiveHour,
        10080 => UsageWindowKind.Weekly,
        43200 => UsageWindowKind.Monthly,
        _ => UsageWindowKind.Other,
    };

    /// <summary>
    /// How much of the limit is gone, 0...1. `percentage` is a whole number, so
    /// counts are preferred when given: `currentValue` is the spend directly and
    /// wins; `remaining` is what is left, so spend is the difference.
    /// Nil rather than zero when nothing is reported.
    /// </summary>
    public static double? UsedFraction(JsonElement limit)
    {
        var usage = GetDouble(limit, "usage");
        if (usage is { } u && u > 0)
        {
            var remaining = GetDouble(limit, "remaining");
            var current = GetDouble(limit, "currentValue");
            double? used = null;
            if (remaining is { } r) used = Math.Max(u - r, current ?? (u - r));
            else if (current is { } c) used = c;
            if (used is { } usedValue)
                return Math.Clamp(Math.Min(usedValue, u) / u, 0, 1);
        }

        // Nil rather than zero: a limit with no figure is not a limit at 0%.
        var percentage = GetDouble(limit, "percentage");
        if (percentage is null) return null;
        return Math.Clamp(percentage.Value, 0, 100) / 100;
    }

    private static string? PlanLabel(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "planName", "plan", "plan_type", "planType", "packageName", "level" })
        {
            if (GetString(payload, name) is { } value && value.Trim().Length > 0)
                return value.Trim();
        }
        return null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(property.GetString(), out var i) => i,
            _ => null,
        };
    }

    /// <summary>An envelope refusal, carrying its classified reason to the health mapper.</summary>
    public sealed class EnvelopeException : Exception
    {
        public UnavailabilityKind Kind { get; }

        public EnvelopeException(UnavailabilityKind kind) : base(kind.ToString()) => Kind = kind;
    }
}

using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;
using Pulse.Providers.Ollama;

namespace Pulse.Providers.Xiaomi;

/// <summary>
/// Xiaomi's MiMo open platform through the console's own endpoints; port of
/// upstream XiaomiMiMoClient/XiaomiMiMoUsageService. A browser SESSION, not a key:
/// the plan and the balance sit behind the console's `api-platform_serviceToken`
/// cookie, pasted by the user (never auto-decrypted from a browser store).
///
/// The ring is the Coding Plan, not the balance: a prepaid cash balance rides
/// along as money on the card. An account with no plan is not a fault; it buys
/// tokens by the yuan, and it says so.
/// </summary>
public sealed class XiaomiMiMoProvider : HttpUsageProviderBase
{
    public const string Host = "platform.xiaomimimo.com";
    public const string Base = "https://platform.xiaomimimo.com/api/v1";
    public const string ConsoleUrl = "https://platform.xiaomimimo.com/#/console/balance";

    private readonly Func<string?, string?> _credentialResolver;

    public XiaomiMiMoProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.XiaomiMiMo;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.XiaomiMiMo);

    protected override string Endpoint => Base + "/tokenPlan/usage";

    /// <summary>The cookie names the console's own requests carry — only these are kept.</summary>
    public static readonly string[] RequiredCookies = ["api-platform_serviceToken", "userId"];
    public static readonly string[] OptionalCookies = ["api-platform_ph", "api-platform_slh"];

    /// <summary>
    /// A Cookie: header reduced to the names above, or an exception naming the
    /// problem. Takes what a browser store hands over OR what somebody pasted out
    /// of their network tab; the value is checked rather than trusted — a header
    /// assembled from an arbitrary string is header injection if a value carries
    /// a newline.
    /// </summary>
    public static string NormalizeCookie(string input)
    {
        if (input.Any(c => c < 32 || c > 126))
            throw new OllamaPageException(OllamaPageFailure.InvalidCookie);
        var header = input.Trim();
        if (header.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            header = header[7..].Trim();
        if (header.Length == 0)
            throw new OllamaPageException(OllamaPageFailure.MissingCookie);
        if (header.Length > 32_768)
            throw new OllamaPageException(OllamaPageFailure.InvalidCookie);

        var wanted = RequiredCookies.Concat(OptionalCookies).ToHashSet();
        var kept = new List<string>();
        var seen = new HashSet<string>();
        foreach (var pair in header.Split(';'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            var name = parts[0].Trim();
            var value = parts[1].Trim();
            if (!wanted.Contains(name)) continue;
            if (value.Length == 0 || value.Contains('"') || value.Contains('\\') || value.Contains(' '))
                throw new OllamaPageException(OllamaPageFailure.InvalidCookie);
            // A host-only row and a domain row for one name is normal; first wins.
            if (!seen.Add(name)) continue;
            kept.Add($"{name}={value}");
        }
        if (!RequiredCookies.All(name => seen.Contains(name)))
            throw new OllamaPageException(OllamaPageFailure.InvalidCookie);
        return string.Join("; ", kept);
    }

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context) =>
        _credentialResolver(account.Label);

    protected override HttpRequestMessage BuildRequest(string credential)
    {
        var header = NormalizeCookie(credential);
        var request = new HttpRequestMessage(HttpMethod.Get, EndpointOrDefault);
        request.Headers.TryAddWithoutValidation("Cookie", header);
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Origin", $"https://{Host}");
        request.Headers.Referrer = new Uri(ConsoleUrl);
        return request;
    }

    private string? PinnedPath;

    public override async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        var raw = ResolveCredential(account, context);
        if (string.IsNullOrWhiteSpace(raw))
            return ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, "session missing");

        // The plan is what the ring is for, so its failure is the call's failure.
        // The balance is a line on the card, so a balance route that does not
        // answer costs that line and nothing else.
        var usage = await GetAsync("tokenPlan/usage", raw!, context, cancellationToken).ConfigureAwait(false);
        var detail = await GetAsync("tokenPlan/detail", raw!, context, cancellationToken).ConfigureAwait(false);
        var balance = await GetAsync("balance", raw!, context, cancellationToken).ConfigureAwait(false);

        foreach (var call in new[] { usage, detail, balance })
        {
            if (call.Health is ProviderReadHealth.Unauthorized)
                return ProviderReadResult.Failed(ProviderReadHealth.Unauthorized, "session expired");
        }
        if (usage.Body is null && detail.Body is null && balance.Body is null)
        {
            var worst = new[] { usage, detail, balance }
                .OrderBy(c => c.Health == ProviderReadHealth.RateLimited ? 2 : c.Health == ProviderReadHealth.ProviderUnavailable ? 1 : 0)
                .First();
            return ProviderReadResult.Failed(worst.Health, worst.Detail);
        }

        var planTuple = XiaomiMapping.ParsePlan(detail.Body, usage.Body);
        if (planTuple is null)
        {
            // Not a failure: an account can buy tokens by the yuan with no plan.
            return ProviderReadResult.Failed(ProviderReadHealth.Healthy, "no coding plan");
        }

        var (planUsed, planLimit, planPeriodEnd, planCode) = planTuple.Value;
        var windows = new List<UsageWindow>
        {
            new(
                Id: "xiaomi.plan",
                Kind: UsageWindowKind.Monthly,
                Scope: null,
                UsedFraction: (double)planUsed / planLimit,
                // Thirty days is a sort key, NOT a reported length: the platform
                // states when the period ends and never how long it is.
                WindowSeconds: 30 * 86400,
                ResetsAt: planPeriodEnd,
                ReportsLength: false,
                IsExhausted: planUsed >= planLimit),
        };

        var money = balance.Body is { } bb ? XiaomiMapping.ParseBalance(bb) : null;
        return ProviderReadResult.Ok(new ProviderUsage(
            Provider: ProviderId.XiaomiMiMo,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: context.Now,
            State: UsageState.Live,
            Plan: planCode,
            CreditBalance: money is { } m ? $"{m.Amount:0.00} {m.Currency}" : null,
            CreditRemaining: null, // no allowance to compare against: not a spendable balance
            Origin: UsageRoute.WebSession));
    }

    private async Task<(JsonElement? Body, ProviderReadHealth Health, string? Detail)> GetAsync(
        string path, string cookieHeader, ProviderReadContext context, CancellationToken cancellationToken)
    {
        PinnedPath = path;
        var result = await base.ReadAsync(
            new MonitoredAccount { Provider = ProviderId.XiaomiMiMo, AccountId = "xiaomiMiMo" },
            context with { Now = context.Now },
            cancellationToken).ConfigureAwait(false);
        return (result.Usage is not null ? JsonDocument.Parse("{}").RootElement.Clone() : (JsonElement?)null,
            result.Health, result.Detail);
    }

    protected override string EndpointOrDefault => Base + (PinnedPath ?? "tokenPlan/usage");

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now) =>
        throw new NotSupportedException("routes are orchestrated in ReadAsync; mapping is in XiaomiMapping");
}

/// <summary>Static mapping core, test-driven against captured replies.</summary>
public static class XiaomiMapping
{
    /// <summary>An envelope first: the platform answers over HTTP 200 whatever happened.</summary>
    public static (int Used, int Limit, DateTimeOffset? PeriodEnd, string? Code)? ParsePlan(JsonElement? detail, JsonElement? usage)
    {
        var usageCode = usage is { } u ? Code(u) : -1;
        var usageBody = usage is { } u2 && u2.TryGetProperty("data", out var ud) && ud.ValueKind == JsonValueKind.Object ? ud : default;
        if (usageCode != 0 || usageBody.ValueKind != JsonValueKind.Object)
            return null;
        if (!usageBody.TryGetProperty("monthUsage", out var monthUsage) || monthUsage.ValueKind != JsonValueKind.Object)
            return null;
        if (!monthUsage.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;

        // The plan's own allowance is the first item; an empty list is an account
        // with no plan — nil, not a zero (a ring at 0% would say "a full month left").
        JsonElement item = default;
        var hasItem = false;
        foreach (var candidate in items.EnumerateArray())
        {
            item = candidate;
            hasItem = true;
            break;
        }
        if (!hasItem) return null;
        var used = item.TryGetProperty("used", out var usedEl) && usedEl.TryGetInt32(out var usedI) ? usedI : 0;
        var limit = item.TryGetProperty("limit", out var limitEl) && limitEl.TryGetInt32(out var limitI) ? limitI : 0;
        if (limit <= 0) return null;

        DateTimeOffset? periodEnd = null;
        string? code = null;
        if (detail is { } d && Code(d) == 0 && d.TryGetProperty("data", out var dd) && dd.ValueKind == JsonValueKind.Object)
        {
            // An expired plan reports last month's numbers until renewed: not drawn.
            if (dd.TryGetProperty("expired", out var expired) && expired.ValueKind == JsonValueKind.True)
                return null;
            periodEnd = dd.TryGetProperty("currentPeriodEnd", out var cpe) && cpe.ValueKind == JsonValueKind.String
                ? ParseConsoleDate(cpe.GetString())
                : null;
            code = dd.TryGetProperty("planCode", out var pc) && pc.ValueKind == JsonValueKind.String ? pc.GetString() : null;
        }

        return (used, limit, periodEnd, code);
    }

    /// <summary>The prepaid balance as a line on the card, in the reply's own currency.</summary>
    public static (double Amount, string Currency)? ParseBalance(JsonElement balance)
    {
        if (Code(balance) != 0) return null;
        if (!balance.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        var amountText = data.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        var currency = data.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (amountText is null || !double.TryParse(amountText, out var amount)) return null;
        if (string.IsNullOrWhiteSpace(currency)) return null;
        return (amount, currency!.Trim());
    }

    /// <summary>The console's own format, in UTC — not ISO-8601.</summary>
    public static DateTimeOffset? ParseConsoleDate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (System.DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            return new DateTimeOffset(parsed, TimeSpan.Zero);
        return null;
    }

    private static int Code(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) ? c : -1;
}

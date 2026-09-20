using System.Globalization;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.DeepSeek;

/// <summary>
/// DeepSeek prepaid balance provider; port of upstream DeepSeekUsageService.
/// Official documented API: GET https://api.deepseek.com/user/balance, Bearer auth.
/// All numeric fields are strings; absent/unparseable amounts are absent, never zero
/// (zero would paint a full red ring). is_available=false is the only exhaustion source.
/// </summary>
public sealed class DeepSeekProvider : HttpUsageProviderBase
{
    public const string EndpointUrl = "https://api.deepseek.com/user/balance";

    private readonly Func<string?, string?> _credentialResolver;

    public DeepSeekProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.DeepSeek;

    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.DeepSeek);

    protected override string Endpoint => EndpointUrl;

    /// <summary>Credential = user-pasted API key stored on the account label slot.</summary>
    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context) =>
        _credentialResolver(account.Label);

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SchemaException("root is not an object");

        // is_available is the only exhaustion signal; never infer from balance == 0.
        var isAvailable = true;
        if (root.TryGetProperty("is_available", out var availableElement) &&
            availableElement.ValueKind == JsonValueKind.True || availableElement.ValueKind == JsonValueKind.False)
            isAvailable = availableElement.GetBoolean();

        var balances = new List<(string Currency, double? Amount)>();
        if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
        {
            foreach (var info in infos.EnumerateArray())
            {
                var currency = GetString(info, "currency") ?? "USD";
                var total = GetString(info, "total_balance");
                var amount = ParseAmount(total);
                balances.Add((currency, amount));
            }
        }

        // Currency selection mirrors upstream: user's chosen currency first (via account
        // label slot suffix), else first with a nonzero balance, else first.
        var selected = SelectBalance(balances);
        string? creditBalance = null;
        CreditAmount? creditRemaining = null;
        if (selected is { } chosen)
        {
            creditBalance = chosen.Amount is { } amount
                ? amount.ToString("0.00", CultureInfo.InvariantCulture)
                : null; // absent, not zero
            if (chosen.Amount is { } a)
                creditRemaining = new CreditAmount(a, chosen.Currency);
        }

        // Balance window: no reset, no reported length; windowSeconds is a sort key only.
        // When the provider reports unavailability, the balance is not a usable quota
        // reading: the window carries the exhausted flag and no numeric credit remains.
        var window = new UsageWindow(
            Id: "balance",
            Kind: UsageWindowKind.Balance,
            Scope: null,
            UsedFraction: 0.0,
            WindowSeconds: 30 * 86400,
            ResetsAt: null,
            ReportsLength: false,
            Estimate: new UsageEstimate(UsageEstimateSource.SinceTopUp),
            IsExhausted: !isAvailable);

        return new ProviderUsage(
            Provider: ProviderId.DeepSeek,
            AccountId: account.AccountId,
            Windows: new[] { window },
            ObservedAt: now,
            State: UsageState.Live,
            Plan: null,
            CreditBalance: creditBalance,
            CreditRemaining: creditRemaining,
            Origin: UsageRoute.Endpoint);
    }

    /// <summary>Upstream rule: an unparseable/absent amount is absent — never coerce to 0.</summary>
    public static double? ParseAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) &&
            !double.IsNaN(amount) && !double.IsInfinity(amount))
            return amount;
        return null;
    }

    public static (string Currency, double? Amount)? SelectBalance(IReadOnlyList<(string Currency, double? Amount)> balances)
    {
        if (balances.Count == 0) return null;
        var withBalance = balances.FirstOrDefault(b => b.Amount is { } a && a > 0);
        if (withBalance != default) return withBalance;
        return balances[0];
    }
}

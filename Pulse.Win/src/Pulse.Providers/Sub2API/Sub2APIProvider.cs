using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Sub2API;

/// <summary>
/// Sub2API subscription and wallet balance. Port of upstream Sub2APIUsageService.
/// GET /api/user/self with Bearer key. Reports subscription quota and wallet balance.
/// </summary>
public sealed class Sub2APIProvider : HttpUsageProviderBase
{
    private readonly Func<string?, string?> _credentialResolver;
    private readonly string _baseUrl;

    public Sub2APIProvider(Func<string?, string?>? credentialResolver = null, string baseUrl = "https://sub2api.com")
    {
        _credentialResolver = credentialResolver ?? (key => key);
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public override ProviderId Id => ProviderId.Sub2API;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Sub2API);
    protected override string Endpoint => $"{_baseUrl}/api/user/self";

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context)
    {
        var pasted = _credentialResolver(account.AccountId);
        return string.IsNullOrWhiteSpace(pasted) ? null : pasted.Trim();
    }

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new SchemaException("root is not an object");
        var windows = new List<UsageWindow>();

        if (root.TryGetProperty("subscription", out var sub) && sub.ValueKind == JsonValueKind.Object)
        {
            var used = GetDouble(sub, "usedValue") ?? GetDouble(sub, "used_value") ?? GetDouble(sub, "used");
            var total = GetDouble(sub, "limitValue") ?? GetDouble(sub, "limit_value") ?? GetDouble(sub, "total");
            if (total is { } t && t > 0 && used is { } u)
                windows.Add(new UsageWindow("subscription", UsageWindowKind.Monthly, "Subscription",
                    Math.Clamp(u / t, 0, 1), 30 * 86_400, null, false, null, u >= t));
        }

        if (root.TryGetProperty("wallet", out var wallet) && wallet.ValueKind == JsonValueKind.Object)
        {
            var balance = GetDouble(wallet, "balance");
            if (balance is { } b)
                windows.Add(new UsageWindow("wallet", UsageWindowKind.Balance, "Wallet",
                    0, 0, null, false, null, b <= 0));
        }

        if (windows.Count == 0) throw new SchemaException("no limits reported");
        return new ProviderUsage(ProviderId.Sub2API, account.AccountId, windows,
            now, UsageState.Live, null, null, null, UsageRoute.Endpoint);
    }

    private static double? ReadDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    }
}

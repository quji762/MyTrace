using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.NewAPI;

/// <summary>
/// New API (one-api fork) quota and balance. Port of upstream NewAPIUsageService.
/// GET /api/user/self with Bearer key. Reports token quota and balance.
/// </summary>
public sealed class NewAPIProvider : HttpUsageProviderBase
{
    private readonly Func<string?, string?> _credentialResolver;
    private readonly string _baseUrl;

    public NewAPIProvider(Func<string?, string?>? credentialResolver = null, string baseUrl = "https://newapi.dev")
    {
        _credentialResolver = credentialResolver ?? (key => key);
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public override ProviderId Id => ProviderId.NewAPI;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.NewAPI);
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
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new SchemaException("missing data object");

        var windows = new List<UsageWindow>();
        var used = GetDouble(data, "used_quota") ?? GetDouble(data, "usedQuota");
        var total = GetDouble(data, "total_quota") ?? GetDouble(data, "totalQuota") ?? GetDouble(data, "quota");
        if (total is { } t && t > 0 && used is { } u)
            windows.Add(new UsageWindow("quota", UsageWindowKind.Monthly, "Token Quota",
                Math.Clamp(u / t, 0, 1), 30 * 86_400, null, false, null, u >= t));

        var balance = GetDouble(data, "balance");
        if (balance is { } b)
            windows.Add(new UsageWindow("balance", UsageWindowKind.Balance, "Balance",
                0, 0, null, false, null, b <= 0));

        if (windows.Count == 0) throw new SchemaException("no limits reported");
        return new ProviderUsage(ProviderId.NewAPI, account.AccountId, windows,
            now, UsageState.Live, GetString(data, "plan"), null, null, UsageRoute.Endpoint);
    }

    private static double? ReadDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    }

    private static string? ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

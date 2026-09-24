using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Qoder;

/// <summary>
/// Qoder subscription credits, read through its account page API.
/// Port of upstream QoderUsageService.
/// The credential is a browser session token the user pastes (not a CLI-managed
/// key): it is stored in the local DPAPI vault and sent only as an
/// Authorization: Bearer header over HTTPS to qoder.com or qoder.com.cn.
/// totalQuota is the account's own; sharedQuota is a team pool. Two rings, never one sum.
/// </summary>
public sealed class QoderProvider : HttpUsageProviderBase
{
    private readonly Func<string?, string?> _credentialResolver;
    private readonly bool _useChina;

    public QoderProvider(Func<string?, string?>? credentialResolver = null, bool useChina = false)
    {
        _credentialResolver = credentialResolver ?? (key => key);
        _useChina = useChina;
    }

    public override ProviderId Id => ProviderId.Qoder;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Qoder);
    protected override string Endpoint =>
        _useChina ? "https://qoder.com.cn/api/v2/me/usages/big_model_credits"
                  : "https://qoder.com/api/v2/me/usages/big_model_credits";

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

        if (root.TryGetProperty("totalQuota", out var tq) && tq.ValueKind == JsonValueKind.Object)
            AddPool(windows, tq, "total", "Total");
        if (root.TryGetProperty("sharedQuota", out var sq) && sq.ValueKind == JsonValueKind.Object)
            AddPool(windows, sq, "shared", "Shared");

        if (windows.Count == 0) throw new SchemaException("no limits reported");
        return new ProviderUsage(ProviderId.Qoder, account.AccountId, windows,
            now, UsageState.Live, GetString(root, "planName"), null, null, UsageRoute.WebSession);
    }

    private static void AddPool(List<UsageWindow> windows, JsonElement pool, string id, string scope)
    {
        if (!pool.TryGetProperty("quotaSummary", out var s) || s.ValueKind != JsonValueKind.Object) return;
        var used = GetDouble(s, "usedValue") ?? GetDouble(s, "used_value");
        var limit = GetDouble(s, "limitValue") ?? GetDouble(s, "limit_value");
        if (limit is not { } l || l <= 0 || used is not { } u) return;
        windows.Add(new UsageWindow(id, UsageWindowKind.Monthly, scope,
            Math.Clamp(u / l, 0, 1), 30 * 86_400, null, false, null, u >= l));
    }

    private static double? ReadDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    }

    private static string? ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

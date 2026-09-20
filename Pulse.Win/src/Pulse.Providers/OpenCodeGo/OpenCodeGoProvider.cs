using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.OpenCodeGo;

/// <summary>
/// OpenCode Go provider; port of upstream OpenCodeGoUsageService.
/// GET https://opencode.ai/zen/go/v1/usage (undocumented; can change) with Bearer key.
/// Three fixed windows: rolling -> fiveHour/5h, weekly -> weekly/7d, monthly -> monthly/30d.
/// percent is 0...100 used. isExhausted = status != "ok".
/// </summary>
public sealed class OpenCodeGoProvider : HttpUsageProviderBase
{
    public const string EndpointUrl = "https://opencode.ai/zen/go/v1/usage";

    private readonly Func<string?, string?> _credentialResolver;

    public OpenCodeGoProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.OpenCodeGo;

    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.OpenCodeGo);

    protected override string Endpoint => EndpointUrl;

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context) =>
        _credentialResolver(account.Label);

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SchemaException("root is not an object");
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw new SchemaException("missing usage object");

        var windows = new List<UsageWindow>();
        AddWindow(windows, "rolling", UsageWindowKind.FiveHour, 5 * 3600, usage);
        AddWindow(windows, "weekly", UsageWindowKind.Weekly, 7 * 86400, usage);
        AddWindow(windows, "monthly", UsageWindowKind.Monthly, 30 * 86400, usage);

        // Shortest window first (upstream ordering).
        windows.Sort((a, b) => a.WindowSeconds.CompareTo(b.WindowSeconds));

        return new ProviderUsage(
            Provider: ProviderId.OpenCodeGo,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: now,
            State: UsageState.Live,
            Plan: null,
            CreditBalance: null,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint);
    }

    private static void AddWindow(List<UsageWindow> windows, string key, UsageWindowKind kind, int seconds, JsonElement usage)
    {
        if (!usage.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Object)
            return;

        var percent = GetDouble(element, "percent"); // 0...100 used
        var status = GetString(element, "status") ?? "ok";
        var reset = ParseTimestamp(GetString(element, "resetsAt"));

        windows.Add(new UsageWindow(
            Id: key,
            Kind: kind,
            Scope: null,
            UsedFraction: percent is { } p ? PercentNormalization.FromPercent(p) : 0.0,
            WindowSeconds: seconds,
            ResetsAt: reset,
            IsExhausted: !status.Equals("ok", StringComparison.OrdinalIgnoreCase)));
    }
}

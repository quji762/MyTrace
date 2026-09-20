using System.Globalization;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.Grok;

/// <summary>
/// The Grok account's weekly pool; port of upstream GrokUsageService.
/// The credential is the CLI's stored OIDC login (`~/.grok/auth.json` on macOS;
/// on Windows the user pastes the token, or the locator supplies it once the CLI
/// exists). GET cli-chat-proxy.grok.com/v1/billing?format=credits with the CLI's
/// `x-xai-token-auth: xai-grok-cli` header — without it the proxy answers the
/// enterprise credit shape instead.
///
/// The reply's `productUsage` breakdown is shares of ONE weekly pool across every
/// Grok product, so there is one window, not per-product windows.
/// An absent percentage means zero (proto3 implicit presence): a zero is simply
/// left out of the reply, so a missing figure reads as 0% used here — the OPPOSITE
/// of GrokBot's rule, where absent means unset.
/// </summary>
public sealed class GrokProvider : HttpUsageProviderBase
{
    public const string EndpointUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    public const string SettingsUrl = "https://cli-chat-proxy.grok.com/v1/settings";

    private readonly Func<string?, string?> _credentialResolver;

    public GrokProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.Grok;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Grok);
    protected override string Endpoint => EndpointUrl;

    protected override IReadOnlyDictionary<string, string> ExtraHeaders() =>
        new Dictionary<string, string>
        {
            // What the CLI sends. Without it the proxy answers the enterprise
            // credit shape, whose `monthlyLimit` is zero on a personal plan.
            ["x-xai-token-auth"] = "xai-grok-cli",
        };

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context)
    {
        var pasted = _credentialResolver(account.Label);
        if (!string.IsNullOrWhiteSpace(pasted)) return pasted.Trim();

        // Borrow the Windows CLI's login if it is there (mirrors ~/.grok/auth.json).
        var authFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "auth.json");
        if (!File.Exists(authFile)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(authFile));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // Keyed by issuer/client id; may hold several entries — take the
            // freshest unexpired one; an undated entry is a fallback, never a winner.
            string? bestToken = null;
            var bestExpiry = DateTimeOffset.MinValue;
            var sawEntry = false;

            foreach (var entry in root.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var token = entry.Value.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
                    ? keyElement.GetString()
                    : null;
                if (string.IsNullOrEmpty(token)) continue;
                sawEntry = true;

                if (!entry.Value.TryGetProperty("expires_at", out var expiryElement) ||
                    expiryElement.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(expiryElement.GetString(), out var expiry))
                {
                    bestToken ??= token; // undated: fallback only
                    continue;
                }

                if (expiry <= DateTimeOffset.Now) continue; // aged out
                if (bestToken is null || expiry > bestExpiry)
                {
                    bestToken = token;
                    bestExpiry = expiry;
                }
            }

            return bestToken ?? (sawEntry ? null : null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SchemaException("root is not an object");
        if (!root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
            throw new SchemaException("missing config");

        var window = GrokMapping.Window(config, now);
        if (window is null)
            throw new SchemaException("no limits reported");

        return new ProviderUsage(
            Provider: ProviderId.Grok,
            AccountId: account.AccountId,
            Windows: new[] { window },
            ObservedAt: now,
            State: UsageState.Live,
            Plan: null, // the plan name comes from the settings call, not the billing reply
            CreditBalance: null,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint);
    }
}

public static class GrokMapping
{
    /// <summary>
    /// Both ends of the period are stated, so the length is measured rather than
    /// assumed: ReportsLength stays true. An omitted percentage is a zero the
    /// serialiser dropped — but only inside a period actually running.
    /// </summary>
    public static UsageWindow? Window(JsonElement config, DateTimeOffset now)
    {
        var start = ParseDate(Str(config, "billingPeriodStart")) ?? ParseDate(Obj(config, "currentPeriod", "start"));
        var end = ParseDate(Obj(config, "currentPeriod", "end")) ?? ParseDate(Str(config, "billingPeriodEnd"));
        if (start is null || end is null || end <= start) return null;

        var seconds = (int)(end.Value - start.Value).TotalSeconds;
        var percent = Num(config, "creditUsagePercent") ?? ((start <= now && now <= end) ? 0 : (double?)null);
        if (percent is null) return null;

        return new UsageWindow(
            Id: "grok-pool",
            Kind: KindForSeconds(seconds),
            Scope: null,
            UsedFraction: Math.Clamp(percent.Value / 100, 0, 1),
            WindowSeconds: seconds,
            ResetsAt: end);
    }

    /// <summary>Named from the length the reply gave, not its `type` string.</summary>
    public static UsageWindowKind KindForSeconds(int seconds) => seconds switch
    {
        >= 6 * 86400 and <= 8 * 86400 => UsageWindowKind.Weekly,
        >= 27 * 86400 and <= 32 * 86400 => UsageWindowKind.Monthly,
        _ => UsageWindowKind.Other,
    };

    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        // Period stamps carry a +00:00 offset rather than a Z, and fractional seconds.
        if (DateTimeOffset.TryParse(text, CultureInfoCompat.Iso, DateTimeStyles.None, out var parsed))
            return parsed;
        return null;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static string? Obj(JsonElement element, string outer, string inner)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(outer, out var o) || o.ValueKind != JsonValueKind.Object) return null;
        return Str(o, inner);
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

internal static class CultureInfoCompat
{
    public static CultureInfo Iso { get; } = CultureInfo.InvariantCulture;
}

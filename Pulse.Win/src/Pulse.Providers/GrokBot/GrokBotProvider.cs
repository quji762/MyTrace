using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.GrokBot;

/// <summary>
/// Grok Bot's weekly allowance; port of upstream GrokBotUsageService.
/// A ring of its own though the credential is Cursor's: xAI's Grok Bot is sold
/// through Cursor and billed against the Cursor account. POST cursor.com/api/
/// dashboard/get-sand-usage-status (Cursor's internal name for Grok Bot is "Sand").
///
/// Reply facts the parser depends on:
/// - The reply states NO reset and no length (measured upstream): the seven days
///   are a sort key, never a length to divide by (ReportsLength: false).
/// - An absent `usagePercent` is NOT a zero here — declared `opt: true` in the
///   schema, explicit presence, absent means unset. The OPPOSITE of Grok's rule.
/// - Nothing included is not nothing used: an account with no allowance answers
///   0% too — the account must say it has one before 0% means nothing used.
/// </summary>
public sealed class GrokBotProvider : HttpUsageProviderBase
{
    public const string EndpointUrl = "https://cursor.com/api/dashboard/get-sand-usage-status";

    private readonly Func<string?, string?> _credentialResolver;

    public GrokBotProvider(Func<string?, string?>? credentialResolver = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
    }

    public override ProviderId Id => ProviderId.GrokBot;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.GrokBot);
    protected override string Endpoint => EndpointUrl;

    private string? CredentialValue;

    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context)
    {
        var stored = _credentialResolver(account.Label);
        if (string.IsNullOrWhiteSpace(stored)) return null;

        var value = stored.Trim();
        if (value.StartsWith("WorkosCursorSessionToken=", StringComparison.OrdinalIgnoreCase))
            value = value["WorkosCursorSessionToken=".Length..].Trim();
        if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            value = value["Cookie:".Length..].Trim();
        return value;
    }

    /// <summary>This one is a POST with a JSON body and the Origin header.</summary>
    public override async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        CredentialValue = ResolveCredential(account, context);
        if (string.IsNullOrEmpty(CredentialValue))
            return ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, "credential missing");

        var result = await base.ReadAsync(account, context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    protected override System.Net.Http.HttpRequestMessage BuildRequest(string credential)
    {
        var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, EndpointOrDefault);
        request.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={Uri.EscapeDataString(credential)}");
        request.Headers.Accept.ParseAdd("application/json");
        // The dashboard's own call sends it, and an endpoint that checks the
        // origin refuses a request without one.
        request.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
        request.Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SchemaException("root is not an object");

        var window = GrokBotMapping.Window(root);
        if (window is null)
            throw new SchemaException("no limits reported");

        return new ProviderUsage(
            Provider: ProviderId.GrokBot,
            AccountId: account.AccountId,
            Windows: new[] { window },
            ObservedAt: now,
            State: UsageState.Live,
            Plan: GrokBotMapping.GetPlanLabel(root),
            CreditBalance: null, // the reply prices the upgrade, never what is left
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint);
    }
}

public static class GrokBotMapping
{
    /// <summary>The weekly window, when the account actually has an allowance.</summary>
    public static UsageWindow? Window(JsonElement reply)
    {
        bool? pooled = GetBool(reply, "usesPooledEnterpriseAllowance");
        bool? limitZero = GetBool(reply, "includedLimitZero");
        bool? hasLimit = GetBool(reply, "hasNonZeroIncludedLimit");
        var percent = Num(reply, "usagePercent");

        if (pooled == true || limitZero == true || hasLimit != true || percent is null)
            return null;

        return new UsageWindow(
            Id: "grokBot",
            Kind: UsageWindowKind.Weekly,
            Scope: null,
            UsedFraction: Math.Clamp(percent.Value / 100, 0, 1),
            // Seven days are a sort key, never a length to divide by.
            WindowSeconds: 7 * 86400,
            ResetsAt: ParseDate(GetString(reply, "nextResetTimestampUtc")),
            ReportsLength: false,
            IsExhausted: percent >= 100);
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    /// <summary>`2026-…Z` with milliseconds, or nothing.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    public static string? GetPlanLabel(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("grokPlanLabel", out var property)) return null;
        if (property.ValueKind != JsonValueKind.String) return null;
        var label = property.GetString();
        return string.IsNullOrWhiteSpace(label) ? null : label.Trim();
    }

    private static bool? GetBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
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

using System.Globalization;
using System.Text.Json;
using Pulse.Core.Providers;

namespace Pulse.Providers.Kiro;

/// <summary>
/// Parses Kiro's ACP usage reply into usage windows.
/// Port of upstream KiroUsageService.windows(from:).
///
/// Each bounded credit pool becomes a monthly window. The reset date is shown,
/// but the ring does not infer a fixed 30-day duration from a date-only reset
/// (ReportsLength = false). Each window is identified by Kiro's resource type
/// rather than its position in the reply, so a reordered or newly inserted pool
/// does not move a saved pin, reset history, or alert state onto another allowance.
/// </summary>
public static class KiroUsageParser
{
    /// <summary>Top-level ACP reply envelope.</summary>
    public sealed record Result(bool Success, string? Message, Payload? Data);

    /// <summary>Usage payload inside a successful reply.</summary>
    public sealed record Payload(string? PlanName, string? BillingCycleReset, List<Breakdown> UsageBreakdowns);

    /// <summary>One bounded credit pool.</summary>
    public sealed record Breakdown(
        string? ResourceType,
        string? DisplayName,
        double? Used,
        double? Limit,
        double? Percentage,
        bool? HasLimit);

    /// <summary>Decode the ACP result object into the typed envelope.</summary>
    public static Result Decode(JsonElement root)
    {
        var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;

        Payload? payload = null;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            var planName = data.TryGetProperty("planName", out var pn) && pn.ValueKind == JsonValueKind.String
                ? pn.GetString()
                : null;
            var billingCycleReset = data.TryGetProperty("billingCycleReset", out var bcr) && bcr.ValueKind == JsonValueKind.String
                ? bcr.GetString()
                : null;

            var breakdowns = new List<Breakdown>();
            if (data.TryGetProperty("usageBreakdowns", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    breakdowns.Add(new Breakdown(
                        ResourceType: GetString(item, "resourceType"),
                        DisplayName: GetString(item, "displayName"),
                        Used: GetDouble(item, "used"),
                        Limit: GetDouble(item, "limit"),
                        Percentage: GetDouble(item, "percentage"),
                        HasLimit: item.TryGetProperty("hasLimit", out var hl) && hl.ValueKind == JsonValueKind.True
                            ? true
                            : item.TryGetProperty("hasLimit", out var hl2) && hl2.ValueKind == JsonValueKind.False
                                ? false
                                : (bool?)null));
                }
            }
            payload = new Payload(planName, billingCycleReset, breakdowns);
        }

        return new Result(success, message, payload);
    }

    /// <summary>Map every bounded credit pool to a usage window. Malformed pools are skipped, never invented.</summary>
    public static IReadOnlyList<UsageWindow> Windows(Payload payload)
    {
        var reset = ParseDate(payload.BillingCycleReset);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var windows = new List<UsageWindow>();

        foreach (var item in payload.UsageBreakdowns)
        {
            // hasLimit == false means an unbounded pool: no ring to draw.
            if (item.HasLimit == false) continue;
            if (item.Limit is not { } limit || !double.IsFinite(limit) || limit <= 0) continue;

            double used;
            if (item.Used is { } reported && double.IsFinite(reported))
            {
                used = reported;
            }
            else if (item.Percentage is { } percent && double.IsFinite(percent))
            {
                used = percent / 100.0 * limit;
            }
            else
            {
                continue; // neither used nor percentage: nothing measured
            }

            // Kiro's resource type is the pool's provider-owned identity.
            // Array position is not: the CLI may reorder pools or insert a new
            // one without changing the allowance a saved pin or alert refers to.
            var resource = StableIdComponent(item.ResourceType)
                ?? StableIdComponent(item.DisplayName)
                ?? "usage";
            var occurrence = occurrences.GetValueOrDefault(resource);
            occurrences[resource] = occurrence + 1;
            var id = occurrence == 0 ? resource : $"{resource}.{occurrence + 1}";

            windows.Add(new UsageWindow(
                Id: id,
                Kind: UsageWindowKind.Monthly,
                Scope: item.DisplayName ?? item.ResourceType,
                UsedFraction: Math.Clamp(used / limit, 0, 1),
                WindowSeconds: 30 * 86_400,
                ResetsAt: reset,
                ReportsLength: false,
                IsExhausted: used >= limit));
        }

        return windows;
    }

    private static string? StableIdComponent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant();
    }

    /// <summary>Kiro reports dates as yyyy-MM-dd in UTC.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return new DateTimeOffset(dt, TimeSpan.Zero);
        return null;
    }

    /// <summary>Map ACP failures to provider-read health with a clear detail.</summary>
    public static (ProviderReadHealth Health, string Detail) ClassifyFailure(KiroAcpClient.AcpException ex) => ex.Kind switch
    {
        KiroAcpClient.FailureKind.ExecutableNotFound =>
            (ProviderReadHealth.ProviderUnavailable, "Kiro CLI isn't installed."),
        KiroAcpClient.FailureKind.Server when IsVersionError(ex.Message) =>
            (ProviderReadHealth.UnsupportedPlatform, "Update Kiro CLI to read subscription usage."),
        KiroAcpClient.FailureKind.Server when IsAuthError(ex.Message) =>
            (ProviderReadHealth.Unauthorized, "Sign in to Kiro CLI to see usage."),
        KiroAcpClient.FailureKind.TimedOut =>
            (ProviderReadHealth.ProviderUnavailable, "timeout"),
        KiroAcpClient.FailureKind.Closed =>
            (ProviderReadHealth.ProviderUnavailable, "connection closed"),
        KiroAcpClient.FailureKind.Server =>
            (ProviderReadHealth.SchemaChanged, ex.Message ?? "unreadable reply"),
        _ => (ProviderReadHealth.ProviderUnavailable, ex.Message ?? "error"),
    };

    /// <summary>Map an unsuccessful envelope message to health.</summary>
    public static (ProviderReadHealth Health, string Detail) ClassifyMessage(string? message)
    {
        if (IsAuthError(message))
            return (ProviderReadHealth.Unauthorized, "Sign in to Kiro CLI to see usage.");
        if (IsVersionError(message))
            return (ProviderReadHealth.UnsupportedPlatform, "Update Kiro CLI to read subscription usage.");
        return (ProviderReadHealth.SchemaChanged, "unreadable reply");
    }

    private static bool IsAuthError(string? message)
    {
        if (message is null) return false;
        var text = message.ToLowerInvariant();
        return text.Contains("sign in") || text.Contains("not authenticated") || text.Contains("login");
    }

    private static bool IsVersionError(string? message)
    {
        if (message is null) return false;
        var text = message.ToLowerInvariant();
        return text.Contains("method not found") || text.Contains("unsupported") || text.Contains("agent-engine");
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }
}

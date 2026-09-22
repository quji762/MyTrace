using System.Text.Json;
using System.Text.Json.Serialization;
using Pulse.Core.Providers;
using Pulse.Core.Usage;

namespace Pulse.Core;

/// <summary>
/// `Pulse --json` — the cache contract status lines poll. Reads the last-good
/// files the running app banked under usage-cache; never fetches, never writes,
/// never localizes tokens (kind/scope/source are stable; label is the user's).
/// </summary>
public static class JsonReport
{
    public static string DefaultCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "usage-cache");

    public static string Emit(string? cacheDirectory = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var directory = cacheDirectory ?? DefaultCacheDirectory();
        var accounts = new List<object>();

        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json")
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var usage = DurableUsageCache.Load(file, at);
                if (usage is not null)
                    accounts.Add(AccountJson(usage, at));
            }
        }

        return JsonSerializer.Serialize(new
        {
            generatedAt = at.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            accounts,
        }, new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    }

    private static object AccountJson(ProviderUsage usage, DateTimeOffset now)
    {
        var id = usage.AccountId;
        var primary = string.Equals(id, usage.Provider.ToString(), StringComparison.Ordinal);
        var windows = usage.Windows.Select(w => WindowJson(w)).ToList();
        var headline = usage.Windows.Count > 0 ? windows[0] : null;

        long? age = usage.ObservedAt is { } observed
            ? Math.Max(0, (long)(now - observed).TotalSeconds)
            : null;

        return new
        {
            id,
            provider = usage.Provider.ToString(),
            name = usage.Provider.ToString(),
            label = primary ? usage.Provider.ToString() : id,
            plan = usage.Plan,
            creditBalance = usage.CreditBalance,
            observedAt = usage.ObservedAt?.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ageSeconds = age,
            source = RouteToken(usage.Origin),
            settingsURL = "pulse://account/" + Uri.EscapeDataString(id),
            headline,
            windows,
        };
    }

    private static object WindowJson(UsageWindow window)
    {
        var fraction = Math.Clamp(window.UsedFraction, 0, 1);
        return new
        {
            id = window.Id,
            kind = KindToken(window),
            scope = window.Scope,
            usedPercent = DisplayPercent(fraction),
            usedFraction = fraction,
            exhausted = window.IsExhausted,
            windowSeconds = window.WindowSeconds,
            reportsLength = window.ReportsLength,
            estimated = window.Estimate is not null,
            estimatedFrom = EstimateToken(window.Estimate),
            resetsAt = window.ResetsAt?.ToString("yyyy-MM-ddTHH:mm:sszzz"),
        };
    }

    /// <summary>Display rule: anything used never reads 0%, not-quite-full never 100%.</summary>
    public static int DisplayPercent(double usedFraction)
    {
        var used = Math.Clamp(usedFraction, 0, 1);
        if (used <= 0) return 0;
        if (used >= 1) return 100;
        var pct = (int)Math.Round(used * 100);
        if (pct < 1) return 1;
        if (pct > 99) return 99;
        return pct;
    }

    private static string KindToken(UsageWindow window) => window.Kind switch
    {
        UsageWindowKind.FiveHour => "fiveHour",
        UsageWindowKind.Daily => "daily",
        UsageWindowKind.Weekly => "weekly",
        UsageWindowKind.Monthly => "monthly",
        UsageWindowKind.Spend => "spend",
        UsageWindowKind.Balance => "balance",
        UsageWindowKind.Messages => "messages",
        _ => window.WindowSeconds > 0 ? $"other:{window.WindowSeconds}" : "other",
    };

    private static string? EstimateToken(UsageEstimate? estimate) => estimate?.Source switch
    {
        UsageEstimateSource.PlanPrice => "planPrice",
        UsageEstimateSource.SinceTopUp => "sinceTopUp",
        UsageEstimateSource.YourBudget => "yourBudget",
        _ => null,
    };

    private static string? RouteToken(UsageRoute? route) => route switch
    {
        UsageRoute.Endpoint => "endpoint",
        UsageRoute.StatusLine => "statusLine",
        UsageRoute.DesktopSession => "desktopSession",
        UsageRoute.AppServer => "appServer",
        UsageRoute.LanguageServer => "languageServer",
        UsageRoute.WebSession => "webSession",
        UsageRoute.ArkCLI => "arkCLI",
        UsageRoute.AppCache => null,
        _ => null,
    };
}

using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pulse.Core.Providers;
using Pulse.Core.Usage;

namespace Pulse.Providers.Local;

/// <summary>
/// Credentials that already live on the machine. Each reader runs only when the
/// pasted slot is empty, and only against a path the caller names — tests pass
/// a fixture directory, never the real profile.
/// </summary>
public static class CursorEditorLogin
{
    public const string TokenKey = "cursorAuth/accessToken";

    public static string? SessionCookie(string databasePath, DateTimeOffset now)
    {
        var token = ReadItem(databasePath, TokenKey);
        return token is null ? null : CookieFromToken(token, now);
    }

    public static string? CookieFromToken(string token, DateTimeOffset now)
    {
        if (!TryPayload(token, out var payload)) return null;
        if (!payload.TryGetProperty("sub", out var sub) || sub.GetString() is not { Length: > 0 } subject)
            return null;
        if (!payload.TryGetProperty("exp", out var expElement)) return null;
        long exp;
        if (expElement.TryGetInt64(out var whole)) exp = whole;
        else if (expElement.TryGetDouble(out var fractional)) exp = (long)fractional;
        else return null;

        var expiry = DateTimeOffset.FromUnixTimeSeconds(exp);
        if (expiry <= now.AddMinutes(1)) return null;

        var account = subject.Split('|', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(account)) return null;
        return $"{account}::{token}";
    }

    private static bool TryPayload(string token, out JsonElement payload)
    {
        payload = default;
        var parts = token.Split('.');
        if (parts.Length < 2) return false;
        try
        {
            var json = Encoding.UTF8.GetString(Base64Url(parts[1]));
            using var document = JsonDocument.Parse(json);
            payload = document.RootElement.Clone();
            return payload.ValueKind == JsonValueKind.Object;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] Base64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }

    public static string? ReadItem(string databasePath, string key)
    {
        if (!File.Exists(databasePath)) return null;
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1";
            command.Parameters.AddWithValue("$key", key);
            var value = command.ExecuteScalar();
            return value as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static class OpenCodeAuthFile
{
    public static string? ReadKey(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("opencode-go", out var entry)) return null;
            if (!entry.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
                return null;
            var text = key.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static class GlmKeyFile
{
    public static readonly string[] RelativePaths =
    [
        Path.Combine(".coding-relay", "glm-api-key"),
        Path.Combine(".config", "bigmodel", "api_key"),
        Path.Combine(".config", "zhipu", "api_key"),
    ];

    public static string? ReadKey(string home)
    {
        foreach (var relative in RelativePaths)
        {
            var path = Path.Combine(home, relative);
            if (!File.Exists(path)) continue;
            try
            {
                var line = File.ReadLines(path).FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(line)) return line;
            }
            catch (Exception)
            {
                continue;
            }
        }

        return null;
    }
}

public static class DevinPlanDatabase
{
    public sealed record Credential(string PlanName, string Raw);

    public static Credential? Read(string databasePath)
    {
        if (!File.Exists(databasePath)) return null;
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT value FROM ItemTable
                WHERE key LIKE 'windsurf.reactSettings.cachedPlanInfoData%'
                   OR key LIKE 'windsurf.settings.cachedPlanInfo%'
                LIMIT 1
                """;
            var raw = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("planName", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
                return null;
            return new Credential(name.GetString()!, raw);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static class VolcengineAccess
{
    public static bool IsKeyPair(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted)) return false;
        var text = pasted.Trim();
        // A Windows path such as C:\bin\arkcli.exe also contains a colon.
        // That is a CLI location, not an AccessKeyID:SecretAccessKey pair.
        if (text.Contains('\\') || text.Contains('/')) return false;
        if (text.Length >= 2 && text[1] == ':' && char.IsLetter(text[0])) return false;
        var parts = text.Split(':', 2);
        return parts.Length == 2
               && parts[0].Length > 1
               && parts[1].Length > 0
               && !parts[0].Contains(' ');
    }

    /// <summary>
    /// A pasted AK:SK wins. The CLI is attempted only when no pair is stored
    /// and one of the candidate binaries can actually be invoked.
    /// </summary>
    public static string? Choose(string? pasted, IEnumerable<string> cliCandidates, Func<string, bool> canInvoke)
    {
        if (IsKeyPair(pasted)) return pasted!.Trim();
        foreach (var candidate in cliCandidates)
        {
            if (canInvoke(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// `arkcli usage plan --format json`. A subscribed product's periods are
    /// used percentages. An item with no periods is skipped.
    /// </summary>
    public static ProviderUsage? ParseCli(string? json, string accountId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;
            var windows = new List<UsageWindow>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("subscribed", out var subscribed) && subscribed.ValueKind == JsonValueKind.False)
                    continue;
                var product = item.TryGetProperty("product", out var productElement) ? productElement.GetString() : null;
                var scope = product?.ToLowerInvariant() switch
                {
                    "coding-plan" => "Coding Plan",
                    "agent-plan" => "Agent Plan",
                    "coding-plan-team" => "Coding Plan · Team",
                    "agent-plan-team" => "Agent Plan · Team",
                    _ => null,
                };
                if (scope is null || !item.TryGetProperty("periods", out var periods) || periods.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var period in periods.EnumerateArray())
                {
                    var label = period.TryGetProperty("label", out var labelElement) ? labelElement.GetString() : null;
                    if (string.IsNullOrEmpty(label) || !period.TryGetProperty("percent", out var percentElement)
                        || !percentElement.TryGetDouble(out var percent))
                        continue;
                    windows.Add(new UsageWindow(
                        Id: $"{product}.{label}",
                        Kind: UsageWindowKind.Other,
                        Scope: scope,
                        UsedFraction: Math.Clamp(percent / 100, 0, 1),
                        WindowSeconds: 0,
                        ResetsAt: null,
                        ReportsLength: false));
                }
            }

            if (windows.Count == 0) return null;
            return new ProviderUsage(
                ProviderId.Volcengine, accountId, windows, now, UsageState.Live,
                null, null, null, UsageRoute.ArkCLI);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

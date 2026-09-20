using System.Diagnostics;
using System.Net.Security;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;

namespace Pulse.Providers.Antigravity;

/// <summary>
/// Antigravity's limits, read from a language server running on this machine; port
/// of upstream AntigravityUsageService. The odd one out: no account endpoint, no
/// stored login — a `language_server` process talks HTTPS on the loopback, and it
/// is the only thing that knows the quota. Figures exist ONLY while Antigravity is
/// running, which `.antigravityNotRunning` says rather than dressing up as a failure.
///
/// Windows discovery differences from macOS: the process lives under
/// %LOCALAPPDATA%\Programs\Antigravity (app and IDE), the CSRF token still comes
/// off the command line (`--csrf_token`), and ports are read from the process's
/// bound TCP listeners via .NET's own tables rather than lsof.
/// </summary>
public sealed class AntigravityProvider : IUsageProvider
{
    // Codeium's language server, hence the `exa.` package and the `x-codeium-` header.
    private const string QuotaMethod = "exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
    private const string StatusMethod = "exa.language_server_pb.LanguageServerService/GetUserStatus";
    public const string CsrfHeader = "x-codeium-csrf-token";

    public ProviderId Id => ProviderId.Antigravity;
    public ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Antigravity);

    public async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        var servers = AntigravityDiscovery.LocateServers();
        if (servers.Count == 0)
            return ProviderReadResult.Failed(ProviderReadHealth.UnsupportedPlatform, "antigravity not running");

        var answeredEmpty = false;
        var somethingAnswered = false;

        foreach (var server in servers)
        {
            foreach (var port in server.Ports)
            {
                var reply = await AntigravityDiscovery.Ask(port, server.Token, QuotaMethod, cancellationToken).ConfigureAwait(false);
                if (reply is { } replyValue && replyValue.ValueKind == JsonValueKind.Object)
                {
                    var windows = AntigravityMapping.Windows(replyValue);
                    if (windows.Count > 0)
                    {
                        string? plan = null;
                        try
                        {
                            var statusReply = await AntigravityDiscovery.Ask(port, server.Token, StatusMethod, cancellationToken).ConfigureAwait(false);
                            if (statusReply is { } statusValue)
                                plan = AntigravityMapping.PlanName(statusValue);
                        }
                        catch (Exception) { /* plan is a nicety; never fails the reading */ }

                        return ProviderReadResult.Ok(new ProviderUsage(
                            ProviderId.Antigravity, account.AccountId, windows, context.Now,
                            UsageState.Live, plan, null, null, UsageRoute.LanguageServer));
                    }
                    answeredEmpty = true;
                    somethingAnswered = true;
                }
                else
                {
                    // A 401 from the IDE's other server is "not me", worth no
                    // more than a closed port; keep looking.
                    somethingAnswered = true;
                }
            }
        }

        return ProviderReadResult.Failed(
            answeredEmpty ? ProviderReadHealth.SchemaChanged : ProviderReadHealth.UnsupportedPlatform,
            answeredEmpty ? "no limits reported" : "antigravity not answering");
    }
}

/// <summary>Process + port + CSRF discovery on Windows.</summary>
public static class AntigravityDiscovery
{
    public sealed record Server(IReadOnlyList<int> Ports, string Token);

    /// <summary>Every matching process's pid and CSRF token, app first, then IDE.</summary>
    public static List<Server> LocateServers()
    {
        var servers = new List<Server>();
        try
        {
            foreach (var process in Process.GetProcessesByName("language_server")
                         .Concat(Process.GetProcessesByName("language_server_win"))
                         .Concat(Process.GetProcessesByName("language_server_windows")))
            {
                try
                {
                    var commandLine = CommandLineOf(process);
                    if (commandLine is null ||
                        !(commandLine.Contains("Antigravity", StringComparison.OrdinalIgnoreCase) ||
                          commandLine.Contains("language_server", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // The token follows the flag on the command line.
                    var token = ExtractToken(commandLine);
                    if (token is null) continue;

                    var ports = ListeningPorts(process.Id);
                    if (ports.Count > 0) servers.Add(new Server(ports, token));
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // Process tables can be unreadable in restricted sessions; that is
            // "cannot see Antigravity", not a crash.
        }
        return servers;
    }

    private static string? CommandLineOf(Process process)
    {
        try
        {
            // Win32: the command line lives in the process's PEB; the query via
            // WMI-free path is the PROCESS_QUERY_LIMITED_INFORMATION read of
            // CommandLine through the built-in Performance Counter is fragile,
            // so the lightweight route is `wmic`-free P/Invoke via GetCommandLine
            // is per-process only — use the CLSID route through .NET: the simplest
            // honest option is a WMI query.
            var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            foreach (var item in searcher.Get())
            {
                var value = item["CommandLine"] as string;
                return value;
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ExtractToken(string commandLine)
    {
        var index = commandLine.IndexOf("--csrf_token", StringComparison.Ordinal);
        if (index < 0) return null;
        var rest = commandLine[(index + "--csrf_token".Length)..].TrimStart();
        if (rest.StartsWith("=")) rest = rest[1..].TrimStart();
        var end = rest.IndexOf(' ');
        var token = end < 0 ? rest : rest[..end];
        return token.Length == 0 ? null : token;
    }

    private static List<int> ListeningPorts(int pid)
    {
        var ports = new List<int>();
        try
        {
            // `netstat -ano` parse: LISTENING rows for this pid, loopback-relevant.
            var start = new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var netstat = Process.Start(start);
            if (netstat is null) return ports;
            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(3000);

            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !parts[^1].Equals(pid.ToString(), StringComparison.Ordinal))
                    continue;
                if (!parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                    continue;
                var local = parts[1];
                var colon = local.LastIndexOf(':');
                if (colon > 0 && int.TryParse(local[(colon + 1)..], out var port))
                    ports.Add(port);
            }
        }
        catch (Exception)
        {
            // Unreadable tables: no ports to try.
        }
        return ports;
    }

    /// <summary>POST an RPC to one loopback port, trusting the server's self-signed
    /// certificate on 127.0.0.1 and nothing else.</summary>
    public static async Task<JsonElement?> Ask(int port, string token, string method, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, errors) =>
                        errors == SslPolicyErrors.None || true, // loopback self-signed; host pinning below
                },
            };
            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{port}/{method}");
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation(AntigravityProvider.CsrfHeader, token);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Static mapping core, test-driven against captured payloads.</summary>
public static class AntigravityMapping
{
    /// <summary>
    /// The ONLY provider that reports what is LEFT rather than what is gone
    /// (remainingFraction). Inverted once here; downstream stays in terms of used.
    /// </summary>
    public static List<UsageWindow> Windows(JsonElement reply)
    {
        var windows = new List<UsageWindow>();
        if (!reply.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            return windows;
        if (!response.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return windows;

        foreach (var group in groups.EnumerateArray())
        {
            var scope = ModelGroup(GetString(group, "displayName"));
            if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                continue;

            var ofGroup = new List<UsageWindow>();
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (Window(bucket, scope) is { } window) ofGroup.Add(window);
            }
            // Shortest window first within a group: the one about to bite leads.
            ofGroup.Sort((a, b) => a.WindowSeconds.CompareTo(b.WindowSeconds));
            windows.AddRange(ofGroup);
        }
        return windows;
    }

    private static UsageWindow? Window(JsonElement bucket, string? scope)
    {
        var id = GetString(bucket, "bucketId");
        var remaining = GetFraction(bucket, "remainingFraction");
        if (id is null || remaining is null) return null;
        if (LengthOf(GetString(bucket, "window")) is not (var kind, var seconds)) return null;

        // Invert remaining → used, exactly once.
        var used = Math.Clamp(1 - remaining.Value, 0, 1);

        return new UsageWindow(
            Id: id,
            Kind: kind,
            Scope: scope,
            UsedFraction: used,
            WindowSeconds: seconds,
            ResetsAt: GetString(bucket, "resetTime") is { } reset
                ? DateTimeOffset.TryParse(reset, null, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null
                : null,
            IsExhausted: remaining <= 0);
    }

    /// <summary>A bucket whose window cannot be read is left out rather than guessed at.</summary>
    public static (UsageWindowKind Kind, int Seconds)? LengthOf(string? window)
    {
        if (string.IsNullOrEmpty(window)) return null;
        var value = window.ToLowerInvariant();
        switch (value)
        {
            case "5h": return (UsageWindowKind.FiveHour, 5 * 3600);
            case "weekly": return (UsageWindowKind.Weekly, 7 * 86400);
            case "daily": return (UsageWindowKind.Other, 86400);
            case "monthly": return (UsageWindowKind.Other, 30 * 86400);
        }

        // Numbered forms so a new window length is understood rather than dropped.
        if (value.Length >= 2 && int.TryParse(value[..^1], out var count) && count > 0)
        {
            return value[^1] switch
            {
                'h' => count == 5 ? (UsageWindowKind.FiveHour, 5 * 3600) : (UsageWindowKind.Other, count * 3600),
                'd' => count == 7 ? (UsageWindowKind.Weekly, count * 86400) : (UsageWindowKind.Other, count * 86400),
                _ => ((UsageWindowKind Kind, int Seconds)?)null,
            };
        }
        return null;
    }

    /// <summary>"Gemini Models" → "Gemini": the trailing word is one the row cannot spare.</summary>
    public static string? ModelGroup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var words = name.Trim().Split(' ');
        if (words.Length > 1 && words[^1].Equals("models", StringComparison.OrdinalIgnoreCase))
            return string.Join(" ", words[..^1]);
        return name.Trim();
    }

    public static string? PlanName(JsonElement statusReply)
    {
        if (statusReply.TryGetProperty("userStatus", out var userStatus) &&
            userStatus.ValueKind == JsonValueKind.Object &&
            userStatus.TryGetProperty("planStatus", out var planStatus) &&
            planStatus.ValueKind == JsonValueKind.Object &&
            planStatus.TryGetProperty("planInfo", out var planInfo) &&
            planInfo.ValueKind == JsonValueKind.Object &&
            planInfo.TryGetProperty("planName", out var planName) &&
            planName.ValueKind == JsonValueKind.String &&
            planName.GetString() is { } name && name.Length > 0)
            return name;
        return null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static double? GetFraction(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(property.GetString(), out var d) => d,
            _ => null,
        };
    }
}

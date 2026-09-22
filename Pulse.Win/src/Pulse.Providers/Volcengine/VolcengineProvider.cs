using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;
using Pulse.Providers.Local;

namespace Pulse.Providers.Volcengine;

/// <summary>
/// The Volcengine Coding Plan; port of upstream VolcengineUsageService.
/// Two routes, neither a browser session: the `arkcli` CLI (subprocess; lands with
/// the Windows tooling locator) and an AccessKeyID:SecretAccessKey pair signed
/// against Top OpenAPI. Keys preferred over the CLI (account identity: arkcli
/// carries an ambient SSO session that may be a different account).
///
/// Response shapes are second-hand (CodexBar's parser, upstream fixtures) �?the
/// parsing is fixture-covered and the failure copy is specific.
/// </summary>
public sealed class VolcengineProvider : HttpUsageProviderBase
{
    private static readonly Uri CodingPlanUrl =
        new("https://open.volcengineapi.com/?Action=GetCodingPlanUsage&Version=2024-01-01");
    private static readonly Uri AgentPlanUrl =
        new("https://open.volcengineapi.com/?Action=GetAFPUsage&Version=2024-01-01");

    private readonly Func<string?, string?> _credentialResolver;
    private readonly Func<IEnumerable<string>> _candidates;
    private readonly Func<string, bool> _canInvoke;
    private readonly Func<string, CancellationToken, Task<string?>> _runCli;

    public VolcengineProvider(
        Func<string?, string?>? credentialResolver = null,
        Func<IEnumerable<string>>? candidates = null,
        Func<string, bool>? canInvoke = null,
        Func<string, CancellationToken, Task<string?>>? runCli = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
        _candidates = candidates ?? CliCandidates;
        _canInvoke = canInvoke ?? CanInvoke;
        _runCli = runCli ?? RunCli;
    }

    public override ProviderId Id => ProviderId.Volcengine;
    public override ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Volcengine);

    protected override string Endpoint => CodingPlanUrl.ToString();

    private VolcengineCredentials? Credentials;
    private bool HasUnreadableKey;

    private static IEnumerable<string> CliCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                yield return Path.Combine(directory, "arkcli.exe");
        }

        yield return Path.Combine(home, ".local", "bin", "arkcli.exe");
    }

    private static bool CanInvoke(string path) => File.Exists(path);

    /// <summary>How long <c>arkcli</c> gets before the refresh worker kills it.</summary>
    private static readonly TimeSpan CliDeadline = TimeSpan.FromSeconds(15);

    /// <summary>Bytes kept from stdout. Past this the pipe is still drained.</summary>
    private const int OutputCeiling = 512 * 1024;

    /// <summary>
    /// Both pipes are drained together, and the child is killed when the
    /// deadline passes. Reading stdout alone deadlocks once stderr fills the
    /// pipe, and waiting without a bound parks every provider's refresh.
    /// </summary>
    private static async Task<string?> RunCli(string executable, CancellationToken cancellationToken)
    {
        Process? process = null;
        Task<byte[]>? stdout = null;
        Task<byte[]>? stderr = null;
        try
        {
            process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "usage plan --format json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!process.Start()) return null;
            // A prompt gets EOF. Leaving stdin open would park a CLI that asks.
            try { process.StandardInput.Close(); }
            catch (Exception) { }

            stdout = Drain(process.StandardOutput.BaseStream, keep: true);
            stderr = Drain(process.StandardError.BaseStream, keep: false);
            var pipes = Task.WhenAll(stdout, stderr);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(CliDeadline);
            var wait = Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
            var completed = await Task.WhenAny(pipes, wait).ConfigureAwait(false);
            var pipesClosed = completed == pipes && pipes.IsCompletedSuccessfully;
            if (!pipesClosed)
                Kill(process);
            deadline.Cancel();
            try { await wait.ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            if (!pipesClosed)
            {
                await Task.WhenAny(pipes, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                return null;
            }

            try
            {
                using var exitBound = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(exitBound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                return null;
            }

            if (!process.HasExited || process.ExitCode != 0) return null;
            return Encoding.UTF8.GetString(stdout.Result);
        }
        catch (Exception)
        {
            if (process is not null) Kill(process);
            return null;
        }
        finally
        {
            if (stdout is not null && stderr is not null)
            {
                await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(TimeSpan.FromSeconds(1)))
                    .ConfigureAwait(false);
            }

            try { process?.Dispose(); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Read until EOF. Bytes past the ceiling are discarded, not left in the
    /// pipe �?stopping the read is the same deadlock.
    /// </summary>
    private static async Task<byte[]> Drain(Stream stream, bool keep)
    {
        var buffer = new byte[8192];
        using var kept = keep ? new MemoryStream() : null;
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;
            }

            if (read == 0) break;
            if (kept is not null && kept.Length < OutputCeiling)
            {
                var take = (int)Math.Min(read, OutputCeiling - kept.Length);
                kept.Write(buffer, 0, take);
            }
        }

        return kept?.ToArray() ?? [];
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or the handle cannot be signalled.
        }
    }

    public override async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
    {
        var entered = _credentialResolver(account.AccountId);
        var choice = VolcengineAccess.Choose(entered, _candidates(), _canInvoke);
        Credentials = VolcengineCredentials.FromEntered(VolcengineAccess.IsKeyPair(choice) ? choice : null);
        var enteredNonEmpty = !string.IsNullOrWhiteSpace(entered);
        // Something was pasted and it is not a pair: told apart from nothing
        // pasted, because the remedies are opposite. A located CLI is not a
        // malformed key.
        HasUnreadableKey = Credentials is null && enteredNonEmpty && !VolcengineAccess.IsKeyPair(choice);
        if (Credentials is null && choice is not null && !VolcengineAccess.IsKeyPair(choice))
        {
            var json = await _runCli(choice, cancellationToken).ConfigureAwait(false);
            var usage = VolcengineAccess.ParseCli(json, account.AccountId, context.Now);
            return usage is null
                ? ProviderReadResult.Failed(ProviderReadHealth.HelperNotRunning, choice)
                : ProviderReadResult.Ok(usage);
        }

        if (Credentials is null)
        {
            return ProviderReadResult.Failed(
                HasUnreadableKey ? ProviderReadHealth.Unauthorized : ProviderReadHealth.CredentialExpired,
                HasUnreadableKey ? "key is not an AccessKeyID:SecretAccessKey pair" : "credential missing");
        }

        // The two actions are independent: a missing Agent Plan must not lose the
        // Coding Plan's windows. A refusal on BOTH is a refusal.
        var coding = await AskAsync(CodingPlanUrl, context, cancellationToken).ConfigureAwait(false);
        var agent = await AskAsync(AgentPlanUrl, context, cancellationToken).ConfigureAwait(false);

        if (coding.Usage is null && agent.Usage is null)
        {
            // The more authoritative refusal wins: reporting a network failure
            // while the other action said the keys were refused would let a wrong
            // key fall through to another account's figures.
            var refused = new[] { coding, agent }.FirstOrDefault(r =>
                r.Health is ProviderReadHealth.Unauthorized or ProviderReadHealth.RateLimited);
            return refused ?? coding;
        }

        var windows = (coding.Usage?.Windows ?? []).Concat(agent.Usage?.Windows ?? []).ToList();
        if (windows.Count == 0)
            return ProviderReadResult.Failed(ProviderReadHealth.SchemaChanged, "no limits reported");

        return ProviderReadResult.Ok(new ProviderUsage(
            Provider: ProviderId.Volcengine,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: context.Now,
            State: UsageState.Live,
            Plan: null,
            CreditBalance: null,
            CreditRemaining: null,
            Origin: UsageRoute.Endpoint));
    }

    protected override HttpRequestMessage BuildRequest(string credential)
    {
        // `credential` is the pinned URL; credentials are in this.Credentials.
        var url = new Uri(credential);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in VolcengineSigner.Headers(
                     "GET", url, Array.Empty<byte>(),
                     "application/x-www-form-urlencoded; charset=utf-8",
                     Credentials!, DateTimeOffset.Now))
            request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    private async Task<ProviderReadResult> AskAsync(
        Uri url, ProviderReadContext context, CancellationToken cancellationToken)
    {
        _pinnedUrl = url.ToString();
        return await base.ReadAsync(AccountStub, context, cancellationToken).ConfigureAwait(false);
    }

    private string? _pinnedUrl;
    private static readonly MonitoredAccount AccountStub = new() { Provider = ProviderId.Volcengine, AccountId = "volcengine" };

    protected override string EndpointOrDefault => _pinnedUrl ?? Endpoint;

    // The pair check happened in ReadAsync; the base guard just needs a
    // non-empty marker �?never the actual secret, which travels in headers
    // built by BuildRequest.
    protected override string? ResolveCredential(MonitoredAccount account, ProviderReadContext context) =>
        Credentials is null ? null : "signed";

    protected override ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        // Two shapes: CodingPlan (Result.QuotaUsage[Level/Percent/ResetTimestamp])
        // and AgentPlan (Result.AFPFiveHour/AFPWeekly/AFPMonthly with Quota/Used/ResetTime).
        if (root.TryGetProperty("Result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("QuotaUsage", out var quotaUsage) && quotaUsage.ValueKind == JsonValueKind.Array)
                return CodingSuccess(quotaUsage, account, now);
            return AgentSuccess(result, account, now);
        }
        throw new SchemaException("unrecognized reply shape");
    }

    private ProviderUsage CodingSuccess(JsonElement quotaUsage, MonitoredAccount account, DateTimeOffset now)
    {
        var windows = new List<UsageWindow>();
        foreach (var quota in quotaUsage.EnumerateArray())
        {
            var level = GetString(quota, "Level");
            var percent = GetDouble(quota, "Percent");
            if (level is null || percent is null) continue;
            if (VolcengineMapping.Window(
                    $"coding-plan.{level.ToLowerInvariant()}", level, percent.Value, "Coding Plan",
                    GetDouble(quota, "ResetTimestamp") is { } ts ? VolcengineMapping.FromEpoch(ts) : null) is { } window)
                windows.Add(window);
        }
        windows.Sort((a, b) => a.WindowSeconds.CompareTo(b.WindowSeconds));
        return Success(windows, account, now);
    }

    private ProviderUsage AgentSuccess(JsonElement result, MonitoredAccount account, DateTimeOffset now)
    {
        var windows = new List<UsageWindow>();
        foreach (var (label, key) in new[] { ("5h", "AFPFiveHour"), ("weekly", "AFPWeekly"), ("monthly", "AFPMonthly") })
        {
            if (!result.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            var quota = GetDouble(window, "Quota");
            var used = GetDouble(window, "Used");
            // A quota of zero is not a full window, it is a plan that has no such
            // window. Dividing by it would report 100% used of nothing.
            if (quota is not { } q || q <= 0 || used is not { } u) continue;

            if (VolcengineMapping.Window(
                    $"agent-plan.{label}", label, u / q * 100, "Agent Plan",
                    GetDouble(window, "ResetTime") is { } ts ? VolcengineMapping.FromEpoch(ts) : null) is { } w)
                windows.Add(w);
        }
        windows.Sort((a, b) => a.WindowSeconds.CompareTo(b.WindowSeconds));
        return Success(windows, account, now);
    }

    private static ProviderUsage Success(List<UsageWindow> windows, MonitoredAccount account, DateTimeOffset now) =>
        new(ProviderId.Volcengine, account.AccountId, windows, now, UsageState.Live, null, null, null, UsageRoute.Endpoint);
}

public static class VolcengineMapping
{
    /// <summary>
    /// A window whose label cannot be read is left out rather than guessed at.
    /// `reportsLength` is false for monthly on purpose: a month is 28�?1 days,
    /// so 30 is a sort key and not a measurement.
    /// </summary>
    public static UsageWindow? Window(string id, string label, double usedPercent, string scope, DateTimeOffset? resetsAt)
    {
        UsageWindowKind kind;
        int seconds;
        var reportsLength = true;

        switch (label.ToLowerInvariant())
        {
            case "5h" or "5-hour" or "five_hour" or "session":
                kind = UsageWindowKind.FiveHour;
                seconds = 5 * 3600;
                break;
            case "weekly" or "week":
                kind = UsageWindowKind.Weekly;
                seconds = 7 * 86400;
                break;
            case "monthly" or "month":
                kind = UsageWindowKind.Monthly;
                seconds = 30 * 86400;
                reportsLength = false;
                break;
            default:
                return null;
        }

        var used = Math.Clamp(usedPercent / 100, 0, 1);
        return new UsageWindow(
            Id: id,
            Kind: kind,
            Scope: scope,
            UsedFraction: used,
            WindowSeconds: seconds,
            ResetsAt: resetsAt,
            ReportsLength: reportsLength,
            // Ark reports no "you are blocked" flag of its own, so the only honest
            // signal is its own figure reaching its own ceiling.
            // Volcengine reports Percent 100 as a full window (their figure).
            IsExhausted: usedPercent >= 100);
    }

    /// <summary>`reset_at`/`updated_at` have shipped as ISO strings AND as numbers, the
    /// number as both seconds and milliseconds. Guessing by magnitude at 1e11.</summary>
    public static DateTimeOffset? FromEpoch(double value)
    {
        if (value <= 0) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(value >= 1e11 ? value : value * 1000));
    }
}

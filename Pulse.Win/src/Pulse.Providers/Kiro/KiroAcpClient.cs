using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Pulse.Providers.Kiro;

/// <summary>
/// A short-lived connection to Kiro CLI's native Agent Client Protocol.
/// Port of upstream KiroACPClient (Sources/Pulse/Providers/KiroACPClient.swift).
///
/// Authentication stays inside Kiro. Pulse starts the CLI, completes the ACP
/// handshake, asks for account usage, and then tears the helper down. Messages
/// are newline-delimited JSON; stderr is drained separately so a noisy helper
/// cannot fill its pipe and deadlock the request.
/// </summary>
public sealed class KiroAcpClient : IDisposable
{
    private readonly object _lock = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private int _nextId = 1;
    private readonly Dictionary<int, PendingRequest> _pending = new();
    private readonly string? _executableOverride;
    private readonly TimeSpan _requestTimeout;

    private sealed class PendingRequest(TaskCompletionSource<JsonElement> completion, CancellationTokenSource timeoutCts)
    {
        public TaskCompletionSource<JsonElement> Completion { get; } = completion;
        public CancellationTokenSource TimeoutCts { get; } = timeoutCts;
    }

    public enum FailureKind
    {
        ExecutableNotFound,
        StartFailed,
        TimedOut,
        Closed,
        Server,
    }

    public sealed class AcpException(FailureKind kind, string? message = null) : Exception(message ?? kind.ToString())
    {
        public FailureKind Kind { get; } = kind;
    }

    /// <summary>Tests inject an isolated helper without changing PATH or touching a login.</summary>
    public KiroAcpClient(string? executable = null, TimeSpan? requestTimeout = null)
    {
        _executableOverride = executable;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>Start the helper, handshake, fetch usage, and tear down.</summary>
    public async Task<JsonElement> UsageAsync(CancellationToken cancellationToken = default)
    {
        Start();
        try
        {
            // Kiro installs its auth connection while handling initialize.
            // Sending getUsage before that response arrives races with setConnection().
            _ = await SendAsync("initialize", new Dictionary<string, object>
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new Dictionary<string, object>(),
                ["clientInfo"] = new Dictionary<string, object> { ["name"] = "Pulse", ["version"] = "0.1" },
            }, cancellationToken).ConfigureAwait(false);
            return await SendAsync("_kiro/account/getUsage", new Dictionary<string, object>(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ShutDown();
        }
    }

    public void Dispose() => ShutDown();

    public void ShutDown()
    {
        lock (_lock)
        {
            if (_stdin is { } stdin)
            {
                try { stdin.Dispose(); } catch (Exception) { }
                _stdin = null;
            }
            if (_process is { } process)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
                process.Dispose();
                _process = null;
            }
            FailAllPending(FailureKind.Closed);
        }
    }

    /// <summary>
    /// Where `kiro-cli` tends to live on Windows. Known install locations are
    /// checked before PATH, so an executable planted earlier in PATH cannot
    /// shadow the real helper; PATH stays the fallback for custom installs.
    /// </summary>
    public static string? LocateKiro()
    {
        var candidates = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        candidates.AddRange(
        [
            Path.Combine(home, "AppData", "Roaming", "npm", "kiro-cli.cmd"),
            Path.Combine(home, "AppData", "Roaming", "npm", "kiro-cli.exe"),
            Path.Combine(home, ".volta", "bin", "kiro-cli.exe"),
            Path.Combine(home, ".bun", "bin", "kiro-cli.exe"),
            Path.Combine(home, ".local", "bin", "kiro-cli.exe"),
        ]);

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(dir.Trim(), "kiro-cli.exe"));
                candidates.Add(Path.Combine(dir.Trim(), "kiro-cli.cmd"));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private void Start()
    {
        lock (_lock)
        {
            // Before starting another one: the last one's reader must not survive.
            if (_process is { } old)
            {
                try { if (!old.HasExited) old.Kill(entireProcessTree: true); } catch (Exception) { }
                old.Dispose();
            }
            _process = null;
            _stdin = null;
            FailAllPending(FailureKind.Closed);

            var executable = _executableOverride ?? LocateKiro()
                ?? throw new AcpException(FailureKind.ExecutableNotFound);
            // Overrides must be absolute: a relative path would resolve against
            // the working directory, not the user's install.
            if (!Path.IsPathRooted(executable))
                throw new AcpException(FailureKind.ExecutableNotFound);

            var start = new ProcessStartInfo(executable, "acp --agent-engine v3 --auth-method cli")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true, // drained, never read: a full pipe blocks the child
                CreateNoWindow = true,
            };

            Process started;
            try
            {
                started = Process.Start(start) ?? throw new AcpException(FailureKind.StartFailed);
            }
            catch (Exception)
            {
                throw new AcpException(FailureKind.StartFailed);
            }

            var process = started;
            _process = process;
            _stdin = process.StandardInput;

            // Drain stderr so a chatty child cannot fill the pipe and block.
            var stderr = process.StandardError;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await stderr.ReadLineAsync().ConfigureAwait(false) is not null) { }
                }
                catch (Exception) { }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    var reader = process.StandardOutput;
                    while (true)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line is null) break; // EOF: the helper exited
                        Consume(line, process);
                    }
                }
                catch (Exception) { /* the reader is being torn down */ }
                ReaderClosed(process);
            });
        }
    }

    /// <summary>EOF on stdout: the helper has exited. Fail everything still waiting.</summary>
    private void ReaderClosed(Process process)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_process, process)) return; // a restart replaced it
            if (_process is { } dying)
            {
                try { if (!dying.HasExited) dying.Kill(entireProcessTree: true); } catch (Exception) { }
                dying.Dispose();
            }
            _process = null;
            _stdin = null;
            FailAllPending(FailureKind.Closed);
        }
    }

    private void FailAllPending(FailureKind kind)
    {
        foreach (var (id, request) in _pending.ToArray())
        {
            _pending.Remove(id);
            request.TimeoutCts.Cancel();
            request.TimeoutCts.Dispose();
            request.Completion.TrySetException(new AcpException(kind));
        }
    }

    private async Task<JsonElement> SendAsync(string method, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        int id;
        TaskCompletionSource<JsonElement> completion;
        CancellationTokenSource timeoutCts;
        lock (_lock)
        {
            if (_stdin is null) throw new AcpException(FailureKind.Closed);
            id = _nextId++;
            completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            timeoutCts = new CancellationTokenSource();
            _pending[id] = new PendingRequest(completion, timeoutCts);
        }

        // Each pending request owns its timeout; cancelled when the request finishes.
        // IDs belong to the client, not the child process: a timeout already queued
        // must never find a new request under its old ID.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_requestTimeout, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            Finish(id, new AcpException(FailureKind.TimedOut));
        }, CancellationToken.None);

        var message = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        });

        try
        {
            lock (_lock)
            {
                if (_stdin is not { } stdin) throw new AcpException(FailureKind.Closed);
                stdin.WriteLine(message);
                stdin.Flush();
            }
        }
        catch (Exception)
        {
            ShutDown();
            throw new AcpException(FailureKind.Closed);
        }

        using var registration = cancellationToken.Register(() =>
            Finish(id, new AcpException(FailureKind.Closed, "cancelled")));
        return await completion.Task.ConfigureAwait(false);
    }

    private void Consume(string line, Process process)
    {
        // Stale reader after a restart must not complete the new pending ids.
        lock (_lock)
        {
            if (!ReferenceEquals(_process, process)) return;
        }

        if (line.Length == 0) return;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
            {
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    var errorText = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() ?? "unknown"
                        : "unknown";
                    Finish(id, new AcpException(FailureKind.Server, errorText));
                    return;
                }

                JsonElement result = root.TryGetProperty("result", out var resultElement)
                    ? resultElement.Clone()
                    : JsonSerializer.SerializeToElement(new { });
                Finish(id, result);
            }
        }
    }

    private void Finish(int id, JsonElement result) => Finish(id, (object)result);

    private void Finish(int id, Exception exception) => Finish(id, (object)exception);

    private void Finish(int id, object resultOrException)
    {
        PendingRequest? request;
        lock (_lock)
        {
            if (!_pending.Remove(id, out request)) return;
        }
        request.TimeoutCts.Cancel();
        request.TimeoutCts.Dispose();
        if (resultOrException is Exception ex)
            request.Completion.TrySetException(ex);
        else
            request.Completion.TrySetResult((JsonElement)resultOrException);
    }
}

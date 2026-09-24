using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Pulse.Providers.Codex;

/// <summary>
/// A JSON-RPC client for `codex app-server`; port of upstream CodexAppServer.
/// This is Codex's own protocol, and the reason this app doesn't have to hold
/// Codex credentials for the fallback route: the app server is already signed in,
/// so asking it for figures avoids reading auth.json and calling an undocumented
/// HTTP endpoint. It also pushes `account/rateLimits/updated` when numbers move.
///
/// The cost is a resident child process, started lazily on the first request and
/// restarted if it dies. EOF on stdout terminates (and kills) the child �?a pipe
/// handler left on a closed pipe is a busy loop, the failure mode upstream's
/// issue #25 was.
/// </summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly object _lock = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private int _nextId = 1;
    // Each pending request owns its timeout CTS: cancelled when the request
    // finishes, so a stale timer can never complete a newer request that
    // reused the same identifier after a helper restart (upstream d35cb00).
    private readonly Dictionary<int, PendingRequest> _pending = new();
    private readonly StringBuilder _lineBuffer = new();
    private Action? _onRateLimitsChanged;

    private sealed class PendingRequest(TaskCompletionSource<JsonElement?> completion, CancellationTokenSource timeoutCts)
    {
        public TaskCompletionSource<JsonElement?> Completion { get; } = completion;
        public CancellationTokenSource TimeoutCts { get; } = timeoutCts;
    }

    public enum FailureKind
    {
        ExecutableNotFound,
        StartFailed,
        TimedOut,
        Server,
    }

    public sealed class AppServerException(FailureKind kind, string? message = null) : Exception(message ?? kind.ToString())
    {
        public FailureKind Kind { get; } = kind;
    }

    public CodexAppServerClient(Action? onRateLimitsChanged = null)
    {
        _onRateLimitsChanged = onRateLimitsChanged;
    }

    /// <summary>The account's limits, as `account/rateLimits/read` reports them.</summary>
    public Task<JsonElement> RateLimitsAsync(CancellationToken cancellationToken = default) =>
        SendAsync("account/rateLimits/read", cancellationToken);

    /// <summary>The account's token history, as `account/usage/read` reports it.</summary>
    public Task<JsonElement> AccountUsageAsync(CancellationToken cancellationToken = default) =>
        SendAsync("account/usage/read", cancellationToken);

    /// <summary>Where `codex` tends to live on Windows: PATH, npm global, volta, bun.</summary>
    public static string? LocateCodex()
    {
        var candidates = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                candidates.Add(Path.Combine(dir.Trim(), "codex.exe"));
        }

        candidates.AddRange(
        [
            Path.Combine(home, "AppData", "Roaming", "npm", "codex.cmd"),
            Path.Combine(home, "AppData", "Roaming", "npm", "codex.exe"),
            Path.Combine(home, ".volta", "bin", "codex.exe"),
            Path.Combine(home, ".bun", "bin", "codex.exe"),
            Path.Combine(home, ".local", "bin", "codex.exe"),
        ]);

        return candidates.FirstOrDefault(File.Exists);
    }

    private void EnsureRunning()
    {
        lock (_lock)
        {
            EnsureRunningCore();
        }
    }

    private void EnsureRunningCore()
    {
        if (_process is { } existing && !existing.HasExited) return;

        // Before starting another one: the last one's reader must not survive.
        _stdin = null;
        _stdout = null;
        if (_process is { } old)
        {
            try { if (!old.HasExited) old.Kill(true); } catch (Exception) { }
            old.Dispose();
        }
        _process = null;
        _lineBuffer.Clear();

        var executable = LocateCodex() ?? throw new AppServerException(FailureKind.ExecutableNotFound);
        if (!Path.IsPathRooted(executable))
            throw new AppServerException(FailureKind.ExecutableNotFound);

        var start = new ProcessStartInfo(executable, "app-server")
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
            started = Process.Start(start) ?? throw new AppServerException(FailureKind.StartFailed);
        }
        catch (Exception)
        {
            throw new AppServerException(FailureKind.StartFailed);
        }

        var process = started;
        _process = process;
        if (!_exitHooked)
        {
            _exitHooked = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true); } catch (Exception) { }
            };
        }
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        FailAllPending();
        // Do NOT reset _nextId: IDs belong to the client, not the child process.
        // A timeout already queued must never find a new request under its old ID.

        // Drain stderr so a chatty child cannot fill the pipe and block.
        var stderr = process.StandardError;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await stderr.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                }
            }
            catch (Exception)
            {
            }
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

        // The protocol opens with a handshake before anything else is accepted.
        // Keep the task so RPC callers can await it (fire-and-forget races).
        var handshakeProcess = process;
        var handshakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handshake = handshakeTcs.Task;
        _ = Task.Run(async () =>
        {
            try
            {
                await SendCoreAsync("initialize", new Dictionary<string, object>
                {
                    ["clientInfo"] = new Dictionary<string, object>
                    {
                        ["name"] = "PulseWin", ["title"] = "PulseWin", ["version"] = "0.1",
                    },
                }, CancellationToken.None).ConfigureAwait(false);
                Notify("initialized");
                handshakeTcs.TrySetResult(true);
                handshakeTcs.TrySetResult(true);
            }
            catch (Exception)
            {
                var owned = false;
                lock (_lock)
                {
                    if (ReferenceEquals(_handshake, handshakeTcs.Task))
                        _handshake = Task.CompletedTask;
                    if (_process is { } dying && ReferenceEquals(_process, handshakeProcess))
                    {
                        owned = true;
                        _process = null;
                        _stdin = null;
                        _stdout = null;
                        try { if (!dying.HasExited) dying.Kill(entireProcessTree: true); } catch (Exception) { }
                        dying.Dispose();
                    }
                }
                if (owned) FailAllPending();
            }
        });
        
    }

    private bool _exitHooked;
    private Task _handshake = Task.CompletedTask;

    /// <summary>EOF on stdout: the helper has exited, or is exiting. Anything still
    /// waiting is failed now rather than at the timeout, and the handles are dropped
    /// so the next request starts a fresh helper.</summary>
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
            _stdout = null;
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            FailAllPending();
        }
    }

    private void FailAllPending()
    {
        List<PendingRequest> pending;
        lock (_lock)
        {
            pending = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var request in pending)
        {
            request.TimeoutCts.Cancel();
            request.TimeoutCts.Dispose();
            request.Completion.TrySetException(new AppServerException(FailureKind.StartFailed));
        }
    }


    private async Task<JsonElement> SendAsync(string method, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            EnsureRunning();
            var current = _process;
            await _handshake.ConfigureAwait(false);
            if (ReferenceEquals(_process, current))
            {
                var result = await SendCoreAsync(method, new Dictionary<string, object>(), cancellationToken).ConfigureAwait(false);
                return result ?? JsonSerializer.SerializeToElement(new { });
            }
        }
        throw new AppServerException(FailureKind.StartFailed);
    }

    private async Task<JsonElement?> SendCoreAsync(string method, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        int id;
        TaskCompletionSource<JsonElement?> completion;
        CancellationTokenSource timeoutCts;
        lock (_lock)
        {
            if (_stdin is null) throw new AppServerException(FailureKind.StartFailed);
            id = _nextId++;
            completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            timeoutCts = new CancellationTokenSource();
            _pending[id] = new PendingRequest(completion, timeoutCts);
        }

        // Each pending request owns its 20-second timeout, cancelled when the
        // request finishes or the connection closes. Old timeout callbacks and
        // queued data from a closed pipe cannot affect the next helper.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            FinishRequest(id, new AppServerException(FailureKind.TimedOut));
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
                if (_stdin is not { } stdin) throw new AppServerException(FailureKind.StartFailed);
                stdin.WriteLine(message);
                stdin.Flush();
            }
        }
        catch (Exception)
        {
            FinishRequest(id, new AppServerException(FailureKind.StartFailed));
            // The helper is gone; the next call starts a fresh one.
            lock (_lock)
            {
                if (_process is { } dying)
                {
                    try { if (!dying.HasExited) dying.Kill(entireProcessTree: true); } catch (Exception) { }
                    dying.Dispose();
                }
                _process = null;
                _stdin = null;
                _stdout = null;
            }
            throw new AppServerException(FailureKind.StartFailed);
        }

        using var registration = cancellationToken.Register(() =>
            FinishRequest(id, new AppServerException(FailureKind.StartFailed, "cancelled")));
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Complete a pending request and cancel its timeout. Idempotent.</summary>
    private void FinishRequest(int id, Exception exception)
    {
        PendingRequest? request;
        lock (_lock)
        {
            if (!_pending.Remove(id, out request)) return;
        }
        request.TimeoutCts.Cancel();
        request.TimeoutCts.Dispose();
        request.Completion.TrySetException(exception);
    }

    private void FinishRequest(int id, JsonElement? result)
    {
        PendingRequest? request;
        lock (_lock)
        {
            if (!_pending.Remove(id, out request)) return;
        }
        request.TimeoutCts.Cancel();
        request.TimeoutCts.Dispose();
        request.Completion.TrySetResult(result);
    }

    private void Notify(string method)
    {
        var message = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = new Dictionary<string, object>(),
        });
        try
        {
            lock (_lock)
            {
                _stdin?.WriteLine(message);
                _stdin?.Flush();
            }
        }
        catch (Exception) { }
    }

    /// <summary>Messages arrive as newline-delimited JSON; ReadLineAsync already
    /// hands complete lines, so each one is a message.</summary>
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
                JsonElement? result = null;
                string? errorText = null;

                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    errorText = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString()
                        : "unknown";
                }
                else if (root.TryGetProperty("result", out var resultElement) &&
                         resultElement.ValueKind == JsonValueKind.Object)
                {
                    result = resultElement.Clone();
                }

                TaskCompletionSource<JsonElement?>? continuation;
                PendingRequest? request;
                lock (_lock)
                {
                    _pending.Remove(id, out request);
                    continuation = request?.Completion;
                }
                if (continuation is null) return;

                // Cancel the timeout: the request has its answer.
                request!.TimeoutCts.Cancel();
                request.TimeoutCts.Dispose();

                if (errorText is not null)
                    continuation.TrySetException(new AppServerException(FailureKind.Server, errorText));
                else
                    continuation.TrySetResult(result);
                return;
            }

            if (root.TryGetProperty("method", out var methodElement) &&
                methodElement.GetString() == "account/rateLimits/updated")
                _onRateLimitsChanged?.Invoke();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            try { _process?.Kill(entireProcessTree: true); } catch (Exception) { }
            _process?.Dispose();
            if (_process is { } dying)
            {
                try { if (!dying.HasExited) dying.Kill(entireProcessTree: true); } catch (Exception) { }
                dying.Dispose();
            }
            _process = null;
            _stdin?.Dispose();
            _stdin = null;
            FailAllPending();
        }
        return ValueTask.CompletedTask;
    }
}
using System.Text;
using System.Text.RegularExpressions;

namespace Pulse.Diagnostics;

/// <summary>
/// Log sink abstraction; production sinks write rolling local files (7-day retention,
/// 20 MB cap per the privacy spec), test sinks capture in memory.
/// </summary>
public interface ILogSink
{
    void Write(DateTimeOffset timestamp, string level, string message);
}

/// <summary>
/// Redaction-enforced logger. Every message passes the scrubber before reaching sinks;
/// this is the single choke point so no code path can log a raw credential.
/// Default level Information; never log Authorization headers, cookies, tokens, bodies.
/// </summary>
public sealed class RedactingLogger : Pulse.Core.Refresh.ILogger
{
    private readonly List<ILogSink> _sinks;
    private readonly SecretScrubber _scrubber;
    private readonly bool _debugEnabled;

    public RedactingLogger(IEnumerable<ILogSink> sinks, bool debugEnabled = false, SecretScrubber? scrubber = null)
    {
        _sinks = sinks.ToList();
        _debugEnabled = debugEnabled;
        _scrubber = scrubber ?? new SecretScrubber();
    }

    /// <summary>Register a literal credential value that must never reach any sink.</summary>
    public void RegisterSecret(string secret) => _scrubber.RegisterSecret(secret);

    public void Log(string message) => Info(message);

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    /// <summary>Debug logs require the user-enabled debug mode; it auto-disables after 24h in production settings.</summary>
    public void Debug(string message)
    {
        if (_debugEnabled) Write("DEBUG", message);
    }

    private void Write(string level, string message)
    {
        var redacted = _scrubber.Scrub(message);
        var timestamp = DateTimeOffset.Now;
        foreach (var sink in _sinks)
            sink.Write(timestamp, level, redacted);
    }
}

/// <summary>
/// Scrubs secret-shaped content from log text: bearer tokens, authorization headers,
/// cookies, and registered canary values. Deny-by-default patterns; extend via
/// <see cref="RegisterSecret"/> when a credential value must never appear in logs.
/// </summary>
public sealed partial class SecretScrubber
{
    private readonly HashSet<string> _literalSecrets = new(StringComparer.Ordinal);
    private readonly object _secretLock = new();

    [GeneratedRegex(@"(?i)authorization\s*[:=]\s*\S.*")]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)(set-)?cookie\s*[:=]\s*\S.*")]
    private static partial Regex CookiePattern();

    [GeneratedRegex(@"(?i)(api[_-]?key|access[_-]?token|refresh[_-]?token)\s*[:=]\s*\S+")]
    private static partial Regex KeyTokenPattern();

    public void RegisterSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        lock (_secretLock) _literalSecrets.Add(secret);
    }

    public string Scrub(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        // Snapshot under the lock: registrations can arrive from the UI thread
        // while a background refresh is scrubbing.
        string[] literals;
        lock (_secretLock) literals = _literalSecrets.ToArray();

        var sb = new StringBuilder(message);
        foreach (var secret in literals)
        {
            sb.Replace(secret, "[REDACTED]");
        }

        sb.ReplaceRegex(AuthorizationPattern(), "Authorization: [REDACTED]");
        sb.ReplaceRegex(BearerPattern(), "Bearer [REDACTED]");
        sb.ReplaceRegex(CookiePattern(), "Cookie: [REDACTED]");
        sb.ReplaceRegex(KeyTokenPattern(), "[REDACTED]");
        return sb.ToString();
    }
}

internal static class StringBuilderRegexExtensions
{
    public static void ReplaceRegex(this StringBuilder sb, Regex pattern, string replacement)
    {
        var result = pattern.Replace(sb.ToString(), replacement);
        sb.Clear();
        sb.Append(result);
    }
}

/// <summary>In-memory sink for tests and debug UI.</summary>
public sealed class InMemoryLogSink : ILogSink
{
    private readonly object _lock = new();
    private readonly List<string> _lines = new();

    public event Action<string>? LineWritten;

    public IReadOnlyList<string> Lines { get { lock (_lock) return _lines.ToArray(); } }

    public void Write(DateTimeOffset timestamp, string level, string message)
    {
        var line = $"{timestamp:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (_lock) _lines.Add(line);
        LineWritten?.Invoke(line);
    }
}

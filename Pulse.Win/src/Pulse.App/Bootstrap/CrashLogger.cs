using System.IO;
using System.Text;
using Pulse.Diagnostics;

namespace Pulse.App.Bootstrap;

/// <summary>
/// Global crash logger. Hooks AppDomain.UnhandledException,
/// DispatcherUnhandledException, and TaskScheduler.UnobservedTaskException,
/// writing full stack traces to a dedicated crash log so a flash-exit is
/// diagnosable. Never re-throws: once logged, the process may continue or
/// exit depending on the exception's severity flag.
///
/// Crash text passes through the shared <see cref="SecretScrubber"/> before it
/// reaches disk: an exception message may echo a credential, and this path
/// bypasses RedactingLogger. <see cref="RegisterSecret"/> feeds stored
/// credential literals to that scrubber; the app's file logger shares the same
/// instance, so one registration covers both sinks.
/// </summary>
public static class CrashLogger
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly object DispatcherGate = new();
    private static readonly SecretScrubber SharedScrubber = new();
    private static readonly List<DateTimeOffset> DispatcherCrashes = new();
    private static string _directory = null!;
    private static bool _installed;

    /// <summary>The scrubber shared with the app's RedactingLogger sinks.</summary>
    public static SecretScrubber Scrubber => SharedScrubber;

    public static string LogDirectory => _directory;

    /// <summary>Install global handlers. Call once, as early as possible in Main/OnStartup.</summary>
    public static void Install(string? directory = null)
    {
        if (_installed) return;
        _installed = true;
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseWin", "logs");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Write("AppDomain.UnhandledException", ex, args.IsTerminating);
        };

        System.Windows.Application.Current?.DispatcherUnhandledException += (_, args) =>
        {
            Write("DispatcherUnhandledException", args.Exception, isTerminating: false);
            // A handler that keeps failing (a poisoned template, a rendering
            // loop) must not become an infinite crash loop: after too many
            // dispatcher exceptions inside a minute, stop swallowing them and
            // let the process die visibly instead of spinning forever.
            var now = DateTimeOffset.UtcNow;
            lock (DispatcherGate)
            {
                DispatcherCrashes.RemoveAll(at => now - at > TimeSpan.FromSeconds(60));
                DispatcherCrashes.Add(now);
                if (DispatcherCrashes.Count > 32) return;
            }

            args.Handled = true; // prevent flash-exit; the app stays up
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Write("TaskScheduler.UnobservedTaskException", args.Exception, isTerminating: false);
            args.SetObserved();
        };
    }

    /// <summary>Register a credential literal that must never reach any log file.</summary>
    public static void RegisterSecret(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret))
            SharedScrubber.RegisterSecret(secret);
    }

    /// <summary>Write one crash entry. Safe to call from any thread; never throws.</summary>
    public static void Write(string source, Exception? exception, bool isTerminating)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(new string('=', 80));
            sb.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}  terminating={isTerminating}");
            sb.AppendLine($"OS: {Environment.OSVersion}  64-bit: {Environment.Is64BitProcess}");
            sb.AppendLine($"CLR: {Environment.Version}");
            if (exception is not null)
            {
                sb.AppendLine($"Type: {exception.GetType().FullName}");
                sb.AppendLine($"Message: {exception.Message}");
                sb.AppendLine("Stack:");
                sb.AppendLine(exception.StackTrace ?? "(no stack)");
                if (exception.InnerException is { } inner)
                {
                    sb.AppendLine($"--- Inner: {inner.GetType().FullName}: {inner.Message}");
                    sb.AppendLine(inner.StackTrace ?? "(no stack)");
                }
            }
            else
            {
                sb.AppendLine("Exception: (null)");
            }
            sb.AppendLine();

            var entry = SharedScrubber.Scrub(sb.ToString());
            lock (Gate)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, "crash.log");
                TrimIfNeeded(path, entry.Length);
                File.AppendAllText(path, entry, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Last-resort logger must never throw.
        }
    }

    /// <summary>Over the cap, keep the newest half (at least 100 lines) — same policy as FileLogSink.</summary>
    private static void TrimIfNeeded(string path, int incomingBytes)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= MaxBytes) return;
            var lines = File.ReadAllLines(path);
            var keep = Math.Max(lines.Length / 2, 100);
            if (keep >= lines.Length) return;
            File.WriteAllLines(path, lines[^keep..], Encoding.UTF8);
        }
        catch (Exception)
        {
            // A failed trim only means the file stays larger; never crash the logger.
        }
    }
}

using System.Text;

namespace Pulse.Diagnostics;

/// <summary>
/// Rolling file log sink. Writes to a daily file under the given directory,
/// capped at 5 MB per file (oldest lines dropped on overflow), 7-day retention.
/// Never throws: a logging failure must not crash the caller.
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly string _directory;
    private readonly object _lock = new();
    private readonly long _maxBytes;

    public FileLogSink(string directory, long maxBytes = 5 * 1024 * 1024)
    {
        _directory = directory;
        _maxBytes = maxBytes;
    }

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "logs");

    public void Write(DateTimeOffset timestamp, string level, string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"pulse-{timestamp:yyyyMMdd}.log");
                var line = $"{timestamp:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";

                // If the file is over the cap, drop the oldest half.
                if (File.Exists(path) && new FileInfo(path).Length + line.Length > _maxBytes)
                    TrimFile(path);

                File.AppendAllText(path, line, Encoding.UTF8);
                CleanupOldFiles(timestamp);
            }
        }
        catch (Exception)
        {
            // Never let logging crash the app.
        }
    }

    private static void TrimFile(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var keep = lines.Length / 2;
            if (keep < 100) keep = 100;
            if (keep >= lines.Length) return;
            File.WriteAllLines(path, lines[^keep..], Encoding.UTF8);
        }
        catch (Exception) { }
    }

    private void CleanupOldFiles(DateTimeOffset now)
    {
        try
        {
            var cutoff = now.AddDays(-7).Date;
            foreach (var file in Directory.EnumerateFiles(_directory, "pulse-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length >= 12 && DateTime.TryParseExact(name[6..], "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var date))
                {
                    if (date < cutoff)
                        File.Delete(file);
                }
            }
        }
        catch (Exception) { }
    }

    public void Dispose() { }
}

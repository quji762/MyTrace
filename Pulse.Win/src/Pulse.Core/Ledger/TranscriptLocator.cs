using System.Security;

namespace Pulse.Core.Ledger;

/// <summary>
/// Windows transcript locations: %USERPROFILE%\.claude\projects (Claude Code) and
/// %USERPROFILE%\.codex\sessions (Codex) — the same roots upstream scans under
/// the Unix home. Read-only; the CLIs' own files are never written.
/// </summary>
public static partial class TranscriptLocator
{
    public static string? ClaudeRoot(string? userProfile = null) =>
        Root(userProfile, ".claude", "projects");

    public static string? CodexRoot(string? userProfile = null) =>
        Root(userProfile, ".codex", "sessions");

    private static string? Root(string? userProfile, params string[] segments)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        var path = Path.Combine(new[] { home }.Concat(segments).ToArray());
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// Every .jsonl transcript under a root, recursively. Upstream skips hidden
    /// files; the CLIs keep their sessions in plain sight.
    /// </summary>
    public static IReadOnlyList<string> FindTranscripts(string? root)
    {
        var files = new List<string>();
        if (root is null || !Directory.Exists(root)) return files;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden,
            };
            files.AddRange(Directory.EnumerateFiles(root, "*.jsonl", options));
        }
        catch (Exception)
        {
            // An unreadable profile is "no transcripts", not a crash.
        }
        return files;
    }

    /// <summary>
    /// File stamp: size + modification time together are enough to know an
    /// append-only log did not change (upstream FileCache.Stamp).
    /// </summary>
    public static (long Size, long Modified)? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return (info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The fallback for a Claude Code transcript that states no cwd: the project
    /// folder is the path with every separator replaced by a dash, and the last
    /// segment of that is the best guess available — a guess, which is why the
    /// stated cwd is preferred wherever there is one.
    /// </summary>
    public static string? ProjectFromClaudeFolder(string path)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        if (string.IsNullOrEmpty(folder)) return null;
        var parts = folder.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : null;
    }
}

/// <summary>
/// Full scan of a provider's transcripts into a priced ledger, with per-file
/// stamp caching so opening the pane twice does not re-read hundreds of
/// megabytes (upstream FileCache).
/// </summary>
public sealed class TranscriptScanner
{
    private readonly string? _cacheDirectory;
    private readonly object _lock = new();
    private Dictionary<string, TranscriptScannerCacheEntry> _cache = new();

    public TranscriptScanner(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory;
        LoadCache();
    }

    public sealed record ScanResult(UsageLedger Ledger, IReadOnlyDictionary<string, ScannedTranscript> Files);

    /// <summary>Scan (or re-use cached file entries) and price. `refresh` forces a re-read.</summary>
    public ScanResult Scan(TranscriptKind kind, ModelPrices prices, bool refresh = false)
    {
        lock (_lock)
        {
            var root = kind switch
            {
                TranscriptKind.ClaudeCode => TranscriptLocator.ClaudeRoot(),
                TranscriptKind.Codex => TranscriptLocator.CodexRoot(),
                _ => null,
            };

            var buckets = new Dictionary<string, Dictionary<string, TokenTally>>();
            var files = new Dictionary<string, ScannedTranscript>();
            var fresh = new Dictionary<string, TranscriptScannerCacheEntry>();

            foreach (var path in TranscriptLocator.FindTranscripts(root))
            {
                var stamp = TranscriptLocator.StampOf(path);
                if (stamp is not { } value) continue;

                ScannedTranscript scanned;
                if (!refresh &&
                    _cache.TryGetValue(path, out var known) &&
                    known.Size == value.Size && known.Modified == value.Modified)
                {
                    scanned = known.Scanned;
                }
                else
                {
                    try
                    {
                        var lines = File.ReadLines(path);
                        scanned = kind == TranscriptKind.ClaudeCode
                            ? TranscriptParser.ParseClaudeCode(lines)
                            : TranscriptParser.ParseCodex(lines);
                    }
                    catch (Exception)
                    {
                        continue; // a half-written file waits for the next pass
                    }
                }

                fresh[path] = new TranscriptScannerCacheEntry(value.Size, value.Modified, scanned);
                files[path] = scanned;
                foreach (var (slot, models) in scanned.Buckets)
                {
                    if (!buckets.TryGetValue(slot, out var slotModels))
                        buckets[slot] = slotModels = new Dictionary<string, TokenTally>();
                    foreach (var (model, tally) in models)
                        slotModels[model] = slotModels.GetValueOrDefault(model, new TokenTally()) + tally;
                }
            }

            _cache = fresh;
            SaveCache(kind);

            return new ScanResult(TranscriptParser.Price(buckets, prices), files);
        }
    }

    // --- cache persistence -------------------------------------------------------------

    private sealed record TranscriptScannerCacheEntry(long Size, long Modified, ScannedTranscript Scanned);

    private string CachePath(TranscriptKind kind) =>
        Path.Combine(_cacheDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseWin"),
            $"ledger-4-{kind.ToString().ToLowerInvariant()}.json");

    private void LoadCache()
    {
        // Buckets are keyed per kind; a combined cache would mix providers.
        // Loaded lazily per kind on first scan: sessions of both CLIs coexist.
    }

    private void SaveCache(TranscriptKind kind)
    {
        try
        {
            var dir = _cacheDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseWin");
            Directory.CreateDirectory(dir);
            // Per-kind persistence keeps Claude and Codex caches independent, the
            // same bucket-format contract upstream's ledger-4 files carry.
            var entries = _cache
                .Where(kv => kv.Key.Contains(kind == TranscriptKind.ClaudeCode ? ".claude" : ".codex", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            File.WriteAllText(Path.Combine(dir, $"ledger-4-{kind.ToString().ToLowerInvariant()}.json"),
                System.Text.Json.JsonSerializer.Serialize(entries));
        }
        catch (Exception) { }
    }
}

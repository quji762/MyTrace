using Pulse.Core.Ledger;
using Pulse.Core.Providers;

namespace Pulse.Core.Refresh;

/// <summary>
/// Newest Claude Code or Codex transcript write. The refresh interval uses it
/// as "someone is working on this machine". The scan is cached briefly so a
/// pass over every provider does not walk the trees once each.
///
/// Only providers in <paramref name="scanOnly"/> are walked: "disabled providers
/// are not fetched" covers local transcript reads too (upstream cf11072).
/// </summary>
public static class LocalActivity
{
    private static readonly object Gate = new();
    private static DateTimeOffset _checkedAt;
    private static DateTimeOffset? _latest;
    private static IReadOnlySet<ProviderId>? _lastScanOnly;

    /// <summary>
    /// Newest transcript write among the given providers (or all transcript
    /// providers when <paramref name="scanOnly"/> is null).
    /// </summary>
    public static DateTimeOffset? LatestTranscriptWrite(IReadOnlySet<ProviderId>? scanOnly = null)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            // Cache is only valid for the same scanOnly set: a cached result
            // from ClaudeCode-only must not answer a Codex-only query.
            if (now - _checkedAt < TimeSpan.FromSeconds(30) && ScanOnlyEquals(_lastScanOnly, scanOnly))
                return _latest;
            _latest = Scan(scanOnly);
            _checkedAt = now;
            _lastScanOnly = scanOnly;
            return _latest;
        }
    }

    private static bool ScanOnlyEquals(IReadOnlySet<ProviderId>? a, IReadOnlySet<ProviderId>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.SetEquals(b);
    }

    private static DateTimeOffset? Scan(IReadOnlySet<ProviderId>? scanOnly)
    {
        DateTimeOffset? best = null;

        // Only Claude Code and Codex keep local transcripts for activity.
        if (scanOnly is null || scanOnly.Contains(ProviderId.ClaudeCode))
            best = MaxWrite(TranscriptLocator.ClaudeRoot(), best);
        if (scanOnly is null || scanOnly.Contains(ProviderId.Codex))
            best = MaxWrite(TranscriptLocator.CodexRoot(), best);

        return best;
    }

    private static DateTimeOffset? MaxWrite(string? root, DateTimeOffset? best)
    {
        if (root is null) return best;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            }))
            {
                DateTimeOffset write;
                try
                {
                    write = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                }
                catch (Exception)
                {
                    continue;
                }

                if (best is null || write > best) best = write;
            }
        }
        catch (Exception)
        {
            // An unreadable profile is "no activity", not a failed refresh.
        }

        return best;
    }
}

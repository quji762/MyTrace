using Pulse.Core.Ledger;

namespace Pulse.Core.Refresh;

/// <summary>
/// Newest Claude Code or Codex transcript write. The refresh interval uses it
/// as "someone is working on this machine". The scan is cached briefly so a
/// pass over every provider does not walk the trees once each.
/// </summary>
public static class LocalActivity
{
    private static readonly object Gate = new();
    private static DateTimeOffset _checkedAt;
    private static DateTimeOffset? _latest;

    public static DateTimeOffset? LatestTranscriptWrite()
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _checkedAt < TimeSpan.FromSeconds(30)) return _latest;
            _latest = Scan();
            _checkedAt = now;
            return _latest;
        }
    }

    private static DateTimeOffset? Scan()
    {
        DateTimeOffset? best = null;
        foreach (var root in new[] { TranscriptLocator.ClaudeRoot(), TranscriptLocator.CodexRoot() })
        {
            if (root is null) continue;
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
        }

        return best;
    }
}

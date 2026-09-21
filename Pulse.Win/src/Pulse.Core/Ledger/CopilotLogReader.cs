using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Copilot's three independent stores read as one client id; port of upstream
/// CopilotLogReader. The three lanes are collected in a fixed order — OTEL,
/// then desktop, then VS Code — and each later lane is filtered against the
/// earlier ones, so a session logged in more than one place is counted once.
///
/// Lane dispatch: `~/.copilot/otel` (OTEL JSONL), `~/.copilot/data.db` +
/// `session-state` (desktop), and `Code/User/workspaceStorage` trees (VS Code).
///
/// The desktop row is a LIFETIME total and OTEL is per span: when a session
/// appears in both and the desktop total is larger, OTEL is a known subset —
/// the scopes are not equal enough to subtract, so no remainder is invented and
/// the OTEL records for that session are marked partial. VS Code is filtered by
/// dedup key or by the same (session, instant) pair.
/// </summary>
public static class CopilotLogReader
{
    /// <summary>Every root Copilot's records are read from, named even when absent.</summary>
    public static IReadOnlyList<string> Roots(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = string.IsNullOrEmpty(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(userProfile, "AppData", "Roaming");

        var roots = new List<string>
        {
            Path.Combine(home, ".copilot", "otel"),
            Path.Combine(home, ".copilot", "data.db"),
            Path.Combine(home, ".copilot", "session-state"),
            Path.Combine(appData, "Code", "User", "workspaceStorage"),
            Path.Combine(home, ".config", "Code", "User", "workspaceStorage"),
        };
        return roots;
    }

    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var roots = Roots(userProfile);

        // Lane one: OTEL is the authority for every session it names.
        var otelRoot = roots.FirstOrDefault(IsOtelRoot);
        var otel = otelRoot is { } o && Directory.Exists(o)
            ? CopilotOtelReader.RecordsFromRoots(new[] { o })
            : new List<AgentUsageRecord>();

        // Lane two: the desktop database is read before either lane is filtered.
        var dbRoot = roots.FirstOrDefault(IsDesktopDatabase);
        var desktopRecords = dbRoot is { } db && File.Exists(db)
            ? CopilotDesktopReader.Records(db, Path.Combine(Path.GetDirectoryName(db)!, "session-state"))
            : new List<AgentUsageRecord>();

        // The desktop row is a lifetime total; OTEL is per span. A session in
        // both with a larger desktop total means OTEL is a known subset: no
        // remainder is invented, the OTEL records are marked partial. A dropped
        // desktop record that was itself partial carries its doubt onto the
        // OTEL records it displaced.
        var desktopTotals = Totals(desktopRecords);
        var otelTotals = Totals(otel);
        var desktopPartialSessions = desktopRecords
            .Where(r => r.IsPartial && r.SessionID is not null)
            .Select(r => r.SessionID!)
            .ToHashSet();
        var partlyCovered = otelTotals
            .Where(entry => desktopTotals.TryGetValue(entry.Key, out var lifetime) && lifetime > entry.Value)
            .Select(entry => entry.Key)
            .Union(desktopPartialSessions)
            .ToHashSet();
        if (partlyCovered.Count > 0)
        {
            otel = otel.Select(record =>
            {
                if (record.SessionID is { } session && partlyCovered.Contains(session))
                    return record with { IsPartial = true };
                return record;
            }).ToList();
        }

        // A session OTEL named is dropped from the desktop lane.
        var otelSessions = otel.Where(r => r.SessionID is not null).Select(r => r.SessionID!).ToHashSet();
        var desktop = desktopRecords.Where(r => r.SessionID is null || !otelSessions.Contains(r.SessionID)).ToList();

        // Lane three: VS Code is filtered against everything accumulated so
        // far, by dedup key or by the same session at the same instant.
        var accumulated = otel.Concat(desktop).ToList();
        var identities = accumulated.Where(r => r.DeduplicationID is not null).Select(r => r.DeduplicationID!).ToHashSet();
        var instants = accumulated
            .Where(r => r.SessionID is not null)
            .Select(r => (Session: r.SessionID!, Instant: r.Timestamp))
            .ToHashSet();

        var vscodeRoot = roots.FirstOrDefault(IsVsCodeRoot);
        var vscode = vscodeRoot is { } vs && Directory.Exists(vs)
            ? CopilotVsCodeReader.RecordsFromRoot(vs).Where(record =>
            {
                if (record.DeduplicationID is { } id && identities.Contains(id)) return false;
                if (record.SessionID is { } session && instants.Contains((session, record.Timestamp))) return false;
                return true;
            }).ToList()
            : new List<AgentUsageRecord>();

        return accumulated.Concat(vscode).OrderBy(r => r.Timestamp).ThenBy(r => r.DeduplicationID ?? "", StringComparer.Ordinal).ToList();
    }

    /// <summary>Every record's counted total, grouped by session. Checked and
    /// saturating: a hostile count cannot trap the sum that only decides
    /// whether a dropped desktop session left work behind.</summary>
    private static Dictionary<string, long> Totals(IReadOnlyList<AgentUsageRecord> records)
    {
        var totals = new Dictionary<string, long>();
        foreach (var record in records)
        {
            if (record.SessionID is not { } session) continue;
            var tally = record.Tally;
            var add = (long)Math.Max(tally.Input, 0) + Math.Max(tally.CacheWrite, 0) +
                      Math.Max(tally.CacheRead, 0) + Math.Max(tally.Output, 0) +
                      Math.Max(record.UnclassifiedTokens, 0);
            var sum = totals.GetValueOrDefault(session);
            totals[session] = long.MaxValue - sum > add ? sum + add : long.MaxValue;
        }
        return totals;
    }

    private static bool IsOtelRoot(string path) =>
        Path.GetFileName(path) == "otel";

    /// <summary>Public static helper for tests and the combined pane wiring.</summary>
    public static bool IsOtelRootStatic(string path) => IsOtelRoot(path);

    private static bool IsDesktopDatabase(string path) =>
        Path.GetFileName(path) == "data.db";

    private static bool IsVsCodeRoot(string path)
    {
        var normalized = path.Replace('\\', '/');
        return Path.GetFileName(normalized) == "workspaceStorage" &&
               Path.GetFileName(Path.GetDirectoryName(normalized)) == "User";
    }

    /// <summary>Copilot's own name for the agent that ran a span, normalized.
    /// The label is carried as the session's name: it is stated metadata, not
    /// something guessed from a path.</summary>
    public static string? AgentLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw == "github.copilot.default") return "GitHub Copilot";

        if (raw.StartsWith("github.copilot.", StringComparison.Ordinal))
        {
            var rest = raw["github.copilot.".Length..];
            if (rest.Length == 0) return "GitHub Copilot";
            return string.Join("-", rest.Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(Titlecased));
        }

        if (raw.Contains(':'))
            return string.Join(": ", raw.Split(':', StringSplitOptions.RemoveEmptyEntries).Select(Titlecased));
        return raw;
    }

    private static string Titlecased(string part)
    {
        if (part.Length == 0) return "";
        return char.ToUpperInvariant(part[0]) + part[1..];
    }
}

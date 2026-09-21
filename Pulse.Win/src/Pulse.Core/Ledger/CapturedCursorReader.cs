using System.Globalization;
using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Cursor's usage cache: a dashboard export of `get-filtered-usage-events`,
/// plus the older CSV shape it replaced; port of upstream CapturedCursorReader.
/// This is NOT a native Cursor file — the cache is written by an export step
/// needing the user's Cursor authentication; it is read from the standard cache
/// folder or the `UsageImports/cursor` import folder.
///
/// Shape is PROVED, not assumed from the name: a JSON file is read when its root
/// holds a `usageEventsDisplay` array, and a CSV when its header names the date,
/// model and four counter columns. Anything else is left alone; a `usage.backup`
/// stem is the one name-based exclusion (a stale copy must not read as live).
///
/// A session only exists where the export names one — a JSON `conversationId` or
/// a CSV `Cloud Agent ID`; events with neither count their tokens and create no
/// session row rather than being given a synthetic id. Equal rows inside one
/// file are not merged, but overlapping exports of one scope reconcile by
/// content multiset; the JSON lane is authoritative over the CSV inside its
/// date range. No cost is carried into a record.
/// </summary>
public static class CapturedCursorReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = string.IsNullOrEmpty(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(userProfile, "AppData", "Roaming");
        var roots = new[]
        {
            // Native cache folder and the import folder, at every depth they occur.
            Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "UsageImports", "cursor"),
            Path.Combine(home, ".config", "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "UsageImports", "cursor"),
        };
        return RecordsFromRoots(roots);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoots(IEnumerable<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(f => IsCandidate(Path.GetFileName(f)))
                    .OrderBy(f => f, StringComparer.Ordinal));
            }
            catch (Exception) { }
        }

        // Grouped by SCOPE, not by folder: a native `usage.<account>` name
        // states a real account; anything else shares one import scope so two
        // differently named exports of one account reconcile rather than being
        // treated as two accounts.
        var jsonByScope = new Dictionary<string, List<string>>();
        var csvByScope = new Dictionary<string, List<string>>();
        foreach (var file in files)
        {
            var key = Scope(Path.GetFileName(file));
            var target = Path.GetExtension(file).ToLowerInvariant() == ".json" ? jsonByScope : csvByScope;
            if (!target.TryGetValue(key, out var list))
                target[key] = list = new List<string>();
            list.Add(file);
        }

        var records = new List<AgentUsageRecord>();
        foreach (var key in jsonByScope.Keys.Union(csvByScope.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var account = key.StartsWith("account:", StringComparison.Ordinal) ? key["account:".Length..] : null;
            records.AddRange(RecordsForScope(
                account,
                jsonByScope.GetValueOrDefault(key) ?? new List<string>(),
                csvByScope.GetValueOrDefault(key) ?? new List<string>()));
        }
        return records;
    }

    private static IReadOnlyList<AgentUsageRecord> RecordsForScope(
        string? account, IReadOnlyList<string> json, IReadOnlyList<string> csv)
    {
        // A byte-identical file dropped twice is one export; the lanes dedup
        // separately so one cannot hide the other.
        var jsonFiles = ReplayDistinct(json);
        var csvFiles = ReplayDistinct(csv);
        var label = account ?? "unscoped";

        var jsonPerFile = new List<IReadOnlyList<AgentUsageRecord>>();
        foreach (var file in jsonFiles)
        {
            if (JsonRecords(file, label) is { } parsed) jsonPerFile.Add(parsed);
        }
        var csvPerFile = new List<IReadOnlyList<AgentUsageRecord>>();
        foreach (var file in csvFiles)
        {
            if (CsvRecords(file, label) is { } parsed) csvPerFile.Add(parsed);
        }

        // Overlapping exports of one scope are reconciled by content multiset.
        var jsonLane = CapturedSupport.Reconcile(jsonPerFile, Signature);
        var csvLane = CapturedSupport.Reconcile(csvPerFile, Signature);

        var chosen = new List<AgentUsageRecord>();
        var partial = jsonLane.Overlapped || csvLane.Overlapped;

        if (jsonLane.Records.Count > 0)
        {
            chosen.AddRange(jsonLane.Records);
            // JSON is the authoritative lane: a CSV row inside its date range
            // is unverifiable overlap and is not added; a row outside is kept.
            // Either way the account is marked incomplete.
            if (jsonLane.Records.Count > 0)
            {
                var first = jsonLane.Records.Min(r => r.Timestamp);
                var last = jsonLane.Records.Max(r => r.Timestamp);
                var inside = 0;
                foreach (var record in csvLane.Records)
                {
                    if (record.Timestamp < first || record.Timestamp > last) chosen.Add(record);
                    else inside++;
                }
                if (inside > 0) partial = true;
            }
        }
        else
        {
            // An empty or unreadable JSON must not suppress a valid CSV.
            chosen.AddRange(csvLane.Records);
        }

        // A file that declared no account cannot be scoped: totals not claimed complete.
        if (account is null) partial = true;

        return partial ? chosen.Select(r => r with { IsPartial = true }).ToList() : chosen;
    }

    /// <summary>A row's content identity, used only to reconcile overlapping exports.</summary>
    private static string Signature(AgentUsageRecord record) =>
        $"{record.SessionID ?? ""}:{record.Timestamp.ToUnixTimeMilliseconds()}:{record.Model}:"
        + $"{record.Tally.Input}:{record.Tally.CacheWrite}:{record.Tally.CacheRead}:{record.Tally.Output}";

    private static IReadOnlyList<string> ReplayDistinct(IReadOnlyList<string> files)
    {
        var seen = new HashSet<string>();
        var distinct = new List<string>();
        foreach (var file in files)
        {
            if (CapturedSupport.Digest(file) is not { } digest) continue;
            if (seen.Add(digest)) distinct.Add(file);
        }
        return distinct;
    }

    /// <summary>Nil when the file is not a Cursor JSON at all; an empty list
    /// when it is one that happens to hold no usable events.</summary>
    private static IReadOnlyList<AgentUsageRecord>? JsonRecords(string file, string account)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone(); }
        catch (Exception) { return null; }
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("usageEventsDisplay", out var events) ||
            events.ValueKind != JsonValueKind.Array)
            return null;

        var records = new List<AgentUsageRecord>();
        foreach (var @event in events.EnumerateArray())
        {
            if (@event.ValueKind != JsonValueKind.Object) continue;
            // A blank model names nothing; no time means the event cannot be
            // placed on a day. Both are skipped, never guessed.
            var model = Str(@event, "model");
            var at = Millis(@event, "timestamp");
            if (model is null || at is not { } timestamp || timestamp.ToUnixTimeMilliseconds() <= 0) continue;

            var usage = @event.TryGetProperty("tokenUsage", out var tu) && tu.ValueKind == JsonValueKind.Object ? tu : default;
            var tally = new TokenTally(
                Input: IntOf(usage, "inputTokens"),
                CacheWrite: IntOf(usage, "cacheWriteTokens"),
                CacheRead: IntOf(usage, "cacheReadTokens"),
                Output: IntOf(usage, "outputTokens"));
            if (tally.Total <= 0) continue;

            // Only an export-stated conversation is a session. The account is
            // folded in so two accounts' identical ids do not collide.
            var conversation = Str(@event, "conversationId");
            records.Add(new AgentUsageRecord
            {
                Timestamp = timestamp,
                Model = model,
                Tally = tally,
                SessionID = conversation is { } conv ? $"cursor:{account}:{conv}" : null,
            });
        }
        return records;
    }

    /// <summary>Nil when the header is not a Cursor CSV; otherwise the rows.</summary>
    private static IReadOnlyList<AgentUsageRecord>? CsvRecords(string file, string account)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception) { return null; }
        var rows = CapturedCsv.Rows(text)
            .Where(row => row.Any(cell => cell.Trim().Length > 0)).ToList();

        var headerIndex = rows.FindIndex(row =>
        {
            var names = row.Select(c => c.Trim().ToLowerInvariant()).ToList();
            return names.Contains("date") && names.Contains("model");
        });
        if (headerIndex < 0) return null;

        // Column NAMES, not positions: the header moved through three shapes,
        // and reading by name keeps a v1 export from being read as a v3 one.
        var header = rows[headerIndex].Select(c => c.Trim().ToLowerInvariant()).ToList();
        int Column(string name) => header.IndexOf(name);

        var dateColumn = Column("date");
        var modelColumn = Column("model");
        var inputColumn = Column("input (w/o cache write)");
        var cacheWriteColumn = Column("input (w/ cache write)");
        var cacheReadColumn = Column("cache read");
        var outputColumn = Column("output tokens");
        if (dateColumn < 0 || modelColumn < 0 || inputColumn < 0 ||
            cacheWriteColumn < 0 || cacheReadColumn < 0 || outputColumn < 0)
            return null;
        var cloudColumn = Column("cloud agent id");
        var widest = new[] { dateColumn, modelColumn, inputColumn, cacheWriteColumn, cacheReadColumn, outputColumn }.Max();

        var records = new List<AgentUsageRecord>();
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            // A short row is a broken record, not a row of zeroes.
            if (row.Count <= widest) continue;
            var rawDate = row[dateColumn].Trim();
            if (CsvDate(rawDate) is not { } at || at.ToUnixTimeMilliseconds() <= 0) continue;
            var model = row[modelColumn].Trim();
            if (model.Length == 0) continue;

            // Independent buckets, as Cursor's own comment says: `w/o cache
            // write` is fresh input, `w/ cache write` is the cache-write bucket.
            // Total Tokens is deliberately not read.
            var tally = new TokenTally(
                Input: CsvCount(row[inputColumn]),
                CacheWrite: CsvCount(row[cacheWriteColumn]),
                CacheRead: CsvCount(row[cacheReadColumn]),
                Output: CsvCount(row[outputColumn]));
            if (tally.Total <= 0) continue;

            string? sessionId = null;
            if (cloudColumn >= 0 && cloudColumn < row.Count && row[cloudColumn].Trim() is { } cloud && cloud.Length > 0)
                sessionId = $"cursor:{account}:cloud:{cloud}";
            // A legacy usage report states a day, not the quarter-hour a
            // request started in: aggregate timing.
            records.Add(new AgentUsageRecord
            {
                Timestamp = at,
                Model = model,
                Tally = tally,
                SessionID = sessionId,
                IsAggregate = true,
            });
        }
        return records;
    }

    /// <summary>Any .json/.csv is a candidate; the schema decides. A native
    /// backup is the one name-based exclusion: a stale copy of the same
    /// account's cache would otherwise be read as live.</summary>
    public static bool IsCandidate(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not ".json" and not ".csv") return false;
        var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        return !stem.StartsWith("usage.backup");
    }

    /// <summary>A native name states a real account (`usage.&lt;account&gt;.&lt;ext&gt;`,
    /// or `usage.&lt;ext&gt;` for the active one); an arbitrary import declares no
    /// account and shares one import scope, keeping two differently named
    /// exports of one account inside reconciliation.</summary>
    public static string Scope(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var parts = stem.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[0].Equals("usage", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2 && CapturedSupport.Slug(parts[1]) is { } account)
                return $"account:{account}";
            return "account:active";
        }
        return "import";
    }

    /// <summary>A counter in a CSV cell: a quoted thousands separator
    /// (`"1,000"`) is a formatting artefact, so the comma is dropped first.</summary>
    private static int CsvCount(string raw) =>
        int.TryParse(raw.Replace(",", ""), out var value) ? value : 0;

    /// <summary>The CSV date-time in several ISO-ish spellings. A bare
    /// yyyy-MM-dd is midnight local, so its calendar day is what the report
    /// states. No fallback to the clock or the file's mtime.</summary>
    private static DateTimeOffset? CsvDate(string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var iso))
            return iso;
        foreach (var format in new[] { "yyyy-MM-dd HH:mm:ssZ", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" })
        {
            if (DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
        }
        return null;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int IntOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)property.GetDouble(),
            _ => 0,
        };
    }

    private static DateTimeOffset? Millis(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var millis) && millis > 0 =>
                DateTimeOffset.FromUnixTimeMilliseconds(millis),
            JsonValueKind.String when long.TryParse(property.GetString(), out var millis) && millis > 0 =>
                DateTimeOffset.FromUnixTimeMilliseconds(millis),
            _ => null,
        };
    }
}

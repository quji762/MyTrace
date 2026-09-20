using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// The Cline CLI's session store; port of upstream ClineCLIReader.
/// A root holds one directory per session with a `&lt;session&gt;.messages.json`
/// transcript and a sibling `&lt;session&gt;.json` manifest. Only assistant messages
/// carrying a `metrics` object count. The store's `inputTokens` is
/// CACHE-INCLUSIVE, so fresh input is what is left after the two cache counts
/// are removed — clamped at zero rather than allowed to go negative.
///
/// Roots come from the environment first, in the order the CLI itself checks
/// them; blank or whitespace values are ignored rather than treated as a path.
/// A missing `ts` is not backfilled from a file's date: the honest result is
/// fewer records rather than a wrong hour.
/// </summary>
public static class ClineCliReader
{
    /// <summary>Roots in the order the CLI itself checks them.</summary>
    public static IReadOnlyList<string> Roots(string? userProfile = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? FromEnv(string name)
        {
            var value = environment is null ? Environment.GetEnvironmentVariable(name) : environment.GetValueOrDefault(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        var home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (FromEnv("CLINE_SESSION_DATA_DIR") is { } sessionDir)
            return [sessionDir];
        if (FromEnv("CLINE_DATA_DIR") is { } dataDir)
            return [Path.Combine(dataDir, "sessions")];
        if (FromEnv("CLINE_DIR") is { } clineDir)
            return [Path.Combine(clineDir, "data", "sessions")];
        return string.IsNullOrEmpty(home)
            ? Array.Empty<string>()
            : [Path.Combine(home, ".cline", "data", "sessions")];
    }

    public static IReadOnlyList<AgentUsageRecord> Records(
        IReadOnlyList<string>? roots = null, string? userProfile = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        roots ??= Roots(userProfile, environment);

        var files = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "*.messages.json", SearchOption.AllDirectories));
            }
            catch (Exception) { }
        }

        return files.OrderBy(f => f, StringComparer.Ordinal).SelectMany(Parse).ToList();
    }

    private static IEnumerable<AgentUsageRecord> Parse(string file)
    {
        var fileName = Path.GetFileName(file);
        var stem = fileName[..^".messages.json".Length];

        JsonElement? rootMaybe = TryRead(file);
        if (rootMaybe is not { } root || root.ValueKind != JsonValueKind.Object) yield break;

        var manifestPath = Path.Combine(Path.GetDirectoryName(file)!, $"{stem}.json");
        var manifest = TryRead(manifestPath);

        var session = NonBlank(Str(root, "sessionId"))
            ?? (manifest is { } m ? NonBlank(Str(m, "sessionId")) : null)
            ?? stem;
        var project = ProjectFrom(
            manifest is { } mf ? Str(mf, "workspace_root") : null
            ?? (manifest is { } m2 ? Str(m2, "cwd") : null));
        var title = manifest is { } m3 &&
                    m3.TryGetProperty("metadata", out var metadata) &&
                    metadata.ValueKind == JsonValueKind.Object
            ? NonBlank(Str(metadata, "title"))
            : null;
        var manifestModel = manifest is { } m4 ? NonBlank(Str(m4, "model")) : null;

        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            yield break;

        var index = 0;
        foreach (var message in messages.EnumerateArray())
        {
            var position = index++;
            if (message.ValueKind != JsonValueKind.Object) continue;
            if (Str(message, "role") != "assistant") continue;
            if (!message.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object) continue;
            if (message.TryGetProperty("ts", out var ts) &&
                ts.ValueKind == JsonValueKind.Number &&
                ts.TryGetInt64(out var millis) && millis > 0)
            {
                // A missing ts is not backfilled from the file's date.
            }
            else continue;

            int cacheRead = IntOf(metrics, "cacheReadTokens");
            int cacheWrite = IntOf(metrics, "cacheWriteTokens");
            int inputTokens = IntOf(metrics, "inputTokens");
            var tally = new TokenTally(
                Input: Math.Max(0, inputTokens - cacheRead - cacheWrite),
                CacheWrite: cacheWrite,
                CacheRead: cacheRead,
                Output: IntOf(metrics, "outputTokens"));
            if (tally.Total <= 0) continue;

            var model = message.TryGetProperty("modelInfo", out var modelInfo) &&
                        modelInfo.ValueKind == JsonValueKind.Object
                ? NonBlank(Str(modelInfo, "id"))
                : null;
            model ??= manifestModel;
            if (model is null) continue;

            // The message's own id is the store's identity; a metadata-less
            // message still has a stable line position.
            var messageID = NonBlank(Str(message, "id")) ?? $"line{position}";
            yield return new AgentUsageRecord
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(millis),
                Model = model,
                Tally = tally,
                SessionID = session,
                Title = title,
                Project = project,
                DeduplicationID = $"cline:{session}:{messageID}",
            };
        }
    }

    /// <summary>Last path segment, where a project was named by a directory.</summary>
    private static string? ProjectFrom(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static JsonElement? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    internal static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

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
}

/// <summary>
/// Amp's threads, where the same calls are described twice; port of upstream
/// AmpSessionReader. A `T-*.json` thread holds assistant messages with their own
/// usage AND a `usageLedger.events` series. The ledger is the primary record; an
/// assistant message is matched to a ledger event by toMessageId first and by
/// equal model + equal tokens second, and a matched message is NOT emitted again.
/// Only when the ledger is empty (or a message has no event) does the message
/// stand alone. That reconciliation is the whole point: emitting both sides would
/// double every call.
///
/// A message's own time is a ledger event's RFC3339 stamp. Amp records no time on
/// a message, so a message without a matching stamped event can only be placed at
/// the thread's own report time, marked isAggregate rather than given a
/// fabricated per-message time. With no real time at all the call is not
/// emitted: inventing a timestamp from the message id is not a reading.
/// </summary>
public static class AmpSessionReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? root = null)
    {
        var result = new List<AgentUsageRecord>();
        var incomplete = false;

        root ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "amp");
        if (!Directory.Exists(root)) return result;

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(root, "T-*.json", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var file in files)
        {
            var parsed = Thread(file);
            result.AddRange(parsed.Records);
            incomplete |= parsed.Incomplete;
        }
        return incomplete ? result.Select(MarkPartial).ToList() : result;
    }

    public static (IReadOnlyList<AgentUsageRecord> Records, bool Incomplete) Thread(string path)
    {
        JsonElement? rootMaybe = null;
        try
        {
            if (File.Exists(path))
                rootMaybe = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        }
        catch (JsonException) { }
        catch (IOException) { }
        if (rootMaybe is not { } root || root.ValueKind != JsonValueKind.Object)
            return (Array.Empty<AgentUsageRecord>(), false);

        var threadID = Str(root, "id") ?? Path.GetFileNameWithoutExtension(path);
        DateTimeOffset? created = root.TryGetProperty("created", out var createdElement) &&
                                  createdElement.TryGetInt64(out var millis) && millis > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(millis)
            : null;

        var (messageCalls, droppedMessages) = Messages(root);
        var (events, droppedEvents) = LedgerEvents(root);
        var incomplete = droppedMessages || droppedEvents;
        var consumed = new bool[events.Count];
        var unmatched = new List<Call>();

        foreach (var call in messageCalls)
        {
            var matched = false;
            if (call.MessageID is { } id)
            {
                for (var i = 0; i < events.Count; i++)
                {
                    if (consumed[i] || events[i].ToMessageID != id) continue;
                    consumed[i] = true;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                for (var i = 0; i < events.Count; i++)
                {
                    if (consumed[i] || events[i].Model != call.Model || events[i].Tally != call.Tally) continue;
                    consumed[i] = true;
                    matched = true;
                    break;
                }
            }
            if (!matched) unmatched.Add(call);
        }

        var records = new List<AgentUsageRecord>();
        for (var index = 0; index < events.Count; index++)
        {
            var @event = events[index];
            if (@event.Timestamp is not { } timestamp)
            {
                if (created is not { } fallback) { incomplete = true; continue; }
                timestamp = fallback;
            }
            var suffix = @event.ToMessageID?.ToString()
                ?? @event.FromMessageID?.ToString()
                ?? index.ToString();
            records.Add(new AgentUsageRecord
            {
                Timestamp = timestamp,
                Model = @event.Model,
                Tally = @event.Tally,
                SessionID = threadID,
                DeduplicationID = $"amp:{threadID}:event:{suffix}",
                IsAggregate = @event.Timestamp is null,
            });
        }

        foreach (var call in unmatched)
        {
            if (created is not { } createdFallback) { incomplete = true; continue; }
            records.Add(new AgentUsageRecord
            {
                Timestamp = createdFallback,
                Model = call.Model,
                Tally = call.Tally,
                SessionID = threadID,
                DeduplicationID = $"amp:{threadID}:message:{call.MessageID?.ToString() ?? "0"}",
                IsAggregate = true,
            });
        }
        return (records, incomplete);
    }

    private static (List<Call> Calls, bool Dropped) Messages(JsonElement root)
    {
        var calls = new List<Call>();
        var dropped = false;
        if (!root.TryGetProperty("messages", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return (calls, dropped);

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (Str(row, "role") != "assistant") continue;
            if (!row.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;

            var tally = new TokenTally(
                Input: Count(usage, "inputTokens"),
                CacheWrite: Count(usage, "cacheCreationInputTokens"),
                CacheRead: Count(usage, "cacheReadInputTokens"),
                Output: Count(usage, "outputTokens"));
            if (tally.Total <= 0) continue;
            var model = Str(usage, "model");
            if (model is null) { dropped = true; continue; }
            calls.Add(new Call(model, tally, CountOpt(row, "messageId")));
        }
        return (calls, dropped);
    }

    private static (List<Event> Events, bool Dropped) LedgerEvents(JsonElement root)
    {
        var events = new List<Event>();
        var dropped = false;
        if (!root.TryGetProperty("usageLedger", out var ledger) ||
            ledger.ValueKind != JsonValueKind.Object ||
            !ledger.TryGetProperty("events", out var rows) ||
            rows.ValueKind != JsonValueKind.Array)
            return (events, dropped);

        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var position = index++;
            if (row.ValueKind != JsonValueKind.Object) continue;
            row.TryGetProperty("tokens", out var tokensElement);
            var tokens = tokensElement.ValueKind == JsonValueKind.Object ? tokensElement : default;

            var tally = new TokenTally(
                Input: Count(tokens, "input"),
                CacheWrite: Count(tokens, "cacheCreationInputTokens"),
                CacheRead: Count(tokens, "cacheReadInputTokens"),
                Output: Count(tokens, "output"));
            if (tally.Total <= 0) continue;
            var model = Str(row, "model");
            if (model is null) { dropped = true; continue; }
            events.Add(new Event(
                Timestamp: Str(row, "timestamp") is { } stamp &&
                           DateTimeOffset.TryParse(stamp, null, System.Globalization.DateTimeStyles.None, out var parsed)
                    ? parsed
                    : null,
                Model: model,
                Tally: tally,
                ToMessageID: CountOpt(row, "toMessageId"),
                FromMessageID: CountOpt(row, "fromMessageId"),
                Index: position));
        }
        return (events, dropped);
    }

    private static AgentUsageRecord MarkPartial(AgentUsageRecord record) => record with { IsPartial = true };

    private sealed record Call(string Model, TokenTally Tally, int? MessageID);

    private sealed record Event(DateTimeOffset? Timestamp, string Model, TokenTally Tally, int? ToMessageID, int? FromMessageID, int Index);

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int Count(JsonElement element, string name)
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

    private static int? CountOpt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)property.GetDouble(),
            _ => null,
        };
    }
}

using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// VS Code's Copilot chat-session logs; port of upstream CopilotVSCodeReader.
/// `&lt;workspaceStorage&gt;/&lt;hash&gt;/chatSessions/&lt;uuid&gt;.jsonl`, the workspace
/// named by the sibling `&lt;hash&gt;/workspace.json`. The file is not a list of
/// requests but an APPEND/PATCH LOG: an early line carries the request array,
/// later lines append to it, and still later lines fill in a streamed response at
/// a nested path. Requests are reconstructed in order before anything is read.
///
/// Only Copilot's own requests count: one is Copilot-originated when it resolved
/// a model or its modelId is a `copilot/` id. Prompt tokens are fresh input with
/// no cache figure beside them; the thinking tokens a tool-call round reports are
/// output work and are folded into output once.
///
/// A request with no timestamp is skipped, not dated at the epoch — the format
/// carries no session- or report-level date to borrow, and inventing the epoch
/// would both place real work at 1970 and fold two different untimed requests
/// into one (the key is the instant).
///
/// Two requests are two requests, even at the same instant: the format's own key
/// is session:timestamp, and a genuine collision takes a `#n` suffix so real
/// work is not folded away.
/// </summary>
public static class CopilotVsCodeReader
{
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null)
    {
        // Windows layout: %APPDATA%\Code\User\workspaceStorage\<hash>\chatSessions.
        var appData = userProfile is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(userProfile, "AppData", "Roaming");
        if (string.IsNullOrEmpty(appData)) return Array.Empty<AgentUsageRecord>();

        var storageRoot = Path.Combine(appData, "Code", "User", "workspaceStorage");
        return !Directory.Exists(storageRoot) ? Array.Empty<AgentUsageRecord>() : RecordsFromRoot(storageRoot);
    }

    public static IReadOnlyList<AgentUsageRecord> RecordsFromRoot(string storageRoot)
    {
        var records = new List<AgentUsageRecord>();
        try
        {
            foreach (var hashDirectory in Directory.EnumerateDirectories(storageRoot))
            {
                var chatSessions = Path.Combine(hashDirectory, "chatSessions");
                if (!Directory.Exists(chatSessions)) continue;
                var workspace = WorkspaceName(hashDirectory);
                foreach (var file in Directory.EnumerateFiles(chatSessions, "*.jsonl"))
                    records.AddRange(RecordsFrom(file, workspace));
            }
        }
        catch (Exception)
        {
            return records; // an unreadable storage tree reads short, not fatal
        }
        return records;
    }

    private static IEnumerable<AgentUsageRecord> RecordsFrom(string file, string? workspace)
    {
        var session = Path.GetFileNameWithoutExtension(file);
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (IOException) { yield break; }

        var requests = Reconstruct(lines);
        var used = new HashSet<string>();
        foreach (var request in requests)
        {
            if (Record(request, session, workspace) is not { } built) continue;
            var id = UniqueID(built.DeduplicationID, used);
            if (id is not null) built = built with { DeduplicationID = id };
            yield return built;
        }
    }

    /// <summary>Keeps the format's own key when unique; a genuine collision takes
    /// a #n suffix, so two distinct requests sharing a session and a millisecond
    /// are both counted.</summary>
    private static string? UniqueID(string? baseId, HashSet<string> used)
    {
        if (baseId is null) return null;
        if (used.Add(baseId)) return baseId;
        var suffix = 2;
        while (used.Contains($"{baseId}#{suffix}")) suffix++;
        var id = $"{baseId}#{suffix}";
        used.Add(id);
        return id;
    }

    private static AgentUsageRecord? Record(JsonElement request, string session, string? workspace)
    {
        JsonElement metadata = default;
        if (request.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("metadata", out var metadataElement) &&
            metadataElement.ValueKind == JsonValueKind.Object)
            metadata = metadataElement;

        var resolved = Str(metadata, "resolvedModel");
        var modelId = Str(request, "modelId");

        // Not a Copilot request: an editor-served model with neither a resolved
        // model nor the product's own id prefix.
        if (resolved is null && !(modelId?.StartsWith("copilot/", StringComparison.Ordinal) ?? false))
            return null;
        var model = resolved ?? Stripped(modelId) ?? "auto";

        var prompt = IntOf(request, "promptTokens");
        prompt = prompt != 0 ? prompt : IntOf(metadata, "promptTokens");
        var completion = IntOf(request, "completionTokens");
        completion = completion != 0 ? completion : IntOf(metadata, "outputTokens");
        var thinking = ThinkingTokens(metadata);

        // Missing time is not a date: an untimed request is skipped rather than
        // placed at the epoch or on the clock.
        DateTimeOffset? timestamp = null;
        if (request.TryGetProperty("timestamp", out var t1) && t1.ValueKind == JsonValueKind.Number &&
            t1.TryGetInt64(out var ms1))
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms1);
        if (timestamp is null && metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("timestamp", out var t2) && t2.ValueKind == JsonValueKind.Number &&
            t2.TryGetInt64(out var ms2))
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms2);
        if (timestamp is null) return null;

        var tally = new TokenTally(Input: prompt, Output: completion + thinking);
        if (tally.Total <= 0) return null;

        return new AgentUsageRecord
        {
            Timestamp = timestamp.Value,
            Model = model,
            Tally = tally,
            SessionID = session,
            SessionName = session,
            Project = workspace,
            DeduplicationID = $"copilot-vscode:{session}:{timestamp.Value.ToUnixTimeMilliseconds()}",
        };
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

    private static string? Stripped(string? modelID)
    {
        if (modelID is null || !modelID.StartsWith("copilot/", StringComparison.Ordinal)) return null;
        var tail = modelID["copilot/".Length..];
        return tail.Length == 0 ? null : tail;
    }

    private static int ThinkingTokens(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("toolCallRounds", out var rounds) ||
            rounds.ValueKind != JsonValueKind.Array)
            return 0;
        var total = 0;
        foreach (var round in rounds.EnumerateArray())
        {
            if (round.ValueKind == JsonValueKind.Object &&
                round.TryGetProperty("thinking", out var thinking) &&
                thinking.ValueKind == JsonValueKind.Object &&
                thinking.TryGetProperty("tokens", out var tokens) &&
                tokens.ValueKind == JsonValueKind.Number &&
                tokens.TryGetInt32(out var count) && count > 0)
                total += count;
        }
        return total;
    }

    private static string? WorkspaceName(string hashDirectory)
    {
        var workspaceFile = Path.Combine(hashDirectory, "workspace.json");
        if (!File.Exists(workspaceFile)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(workspaceFile));
            var uri = Str(document.RootElement, "folder") ?? Str(document.RootElement, "workspace");
            if (uri is null) return null;
            // A file URI's last path segment; a plain path keeps its own.
            if (Uri.TryCreate(uri, UriKind.Absolute, out var url) && url.IsFile)
                return Path.GetFileName(url.LocalPath.TrimEnd('/', '\\'));
            return Path.GetFileName(uri.TrimEnd('/', '\\'));
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

    // --- reconstructing the append/patch log --------------------------------------------

    /// <summary>Replays the file's lines into the request array they describe.
    /// kind 0: set v.requests; kind 1: patch at path k (only writes into
    /// `requests` count); kind 2: append v to requests.</summary>
    public static List<JsonElement> Reconstruct(IEnumerable<string> lines)
    {
        var requests = new List<JsonElement>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!row.TryGetProperty("kind", out var kindElement) || !kindElement.TryGetInt32(out var kind)) continue;

                switch (kind)
                {
                    case 0:
                        if (row.TryGetProperty("v", out var setValue) &&
                            setValue.ValueKind == JsonValueKind.Object &&
                            setValue.TryGetProperty("requests", out var appendedRequests) &&
                            appendedRequests.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var request in appendedRequests.EnumerateArray())
                                requests.Add(request.Clone());
                        }
                        break;

                    case 1:
                    {
                        // A patch addresses one path; only one into `requests` is
                        // a usage-bearing write. Out-of-range is dropped.
                        if (!TryPath(row, out var path) || path.Count < 2) continue;
                        if (path[0].ValueKind != JsonValueKind.String || path[0].GetString() != "requests") continue;
                        if (path[1].ValueKind != JsonValueKind.Number || !path[1].TryGetInt32(out var patchIndex)) continue;
                        if (patchIndex < 0 || patchIndex >= requests.Count) continue;

                        var value = row.TryGetProperty("v", out var v1) ? v1.Clone() : JsonSerializer.SerializeToElement((string?)null);
                        if (path.Count == 2)
                        {
                            requests[patchIndex] = value;
                        }
                        else
                        {
                            requests[patchIndex] = Mutate(requests[patchIndex].Clone(), path.Skip(2).ToList(), value);
                        }
                        break;
                    }

                    case 2:
                        if (TryPath(row, out var appendPath) && appendPath.Count == 1 &&
                            appendPath[0].ValueKind == JsonValueKind.String &&
                            appendPath[0].GetString() == "requests" &&
                            row.TryGetProperty("v", out var appendValue) &&
                            appendValue.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var request in appendValue.EnumerateArray())
                                requests.Add(request.Clone());
                        }
                        break;
                }
            }
        }
        return requests;
    }

    private static bool TryPath(JsonElement row, out List<JsonElement> path)
    {
        path = new List<JsonElement>();
        if (!row.TryGetProperty("k", out var k) || k.ValueKind != JsonValueKind.Array) return false;
        foreach (var part in k.EnumerateArray()) path.Add(part.Clone());
        return true;
    }

    /// <summary>Writes value at a nested path, creating the containers the path
    /// names. A scalar or missing key is not a path into anything, so the write
    /// is dropped (conservatively: the original value survives).</summary>
    private static JsonElement Mutate(JsonElement container, List<JsonElement> path, JsonElement value)
    {
        if (path.Count == 0) return value;

        var head = path[0];
        var rest = path.Skip(1).ToList();

        if (container.ValueKind == JsonValueKind.Array)
        {
            if (!head.TryGetInt32(out var index) || index < 0 || index >= container.GetArrayLength())
                return container;
            var items = container.EnumerateArray().Select((item, i) =>
                i == index ? Mutate(item, rest, value) : item.Clone()).ToList();
            var array = JsonSerializer.SerializeToElement(items);
            return array;
        }

        if (container.ValueKind == JsonValueKind.Object)
        {
            if (head.ValueKind != JsonValueKind.String) return container;
            var key = head.GetString()!;
            var dictionary = new Dictionary<string, JsonElement>();
            foreach (var property in container.EnumerateObject())
                dictionary[property.Name] = property.Value.Clone();

            var child = dictionary.TryGetValue(key, out var existing)
                ? existing
                : rest.Count > 0 && rest[0].ValueKind == JsonValueKind.Number
                    ? JsonSerializer.SerializeToElement(Array.Empty<object>())
                    : JsonSerializer.SerializeToElement(new Dictionary<string, object>());
            dictionary[key] = Mutate(child, rest, value);

            return JsonSerializer.SerializeToElement(dictionary);
        }

        return container;
    }
}

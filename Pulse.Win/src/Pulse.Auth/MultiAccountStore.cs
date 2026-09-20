using Pulse.Core.Accounts;
using Pulse.Core.Providers;

namespace Pulse.Auth;

/// <summary>
/// Which providers can hold added accounts, mirroring upstream
/// Provider.supportsMultipleAccounts: claudeCode, codex, grok, grokBot. NOT two,
/// not Cursor (an account-labeled UI would promise a behavior the provider's
/// credential model cannot deliver).
/// </summary>
public static class MultiAccountCapability
{
    public static bool Supports(ProviderId provider) => provider is
        ProviderId.ClaudeCode or ProviderId.Codex or ProviderId.Grok or ProviderId.GrokBot;
}

/// <summary>
/// Multi-account store on top of the credential vault. The primary account uses
/// the provider's raw id; added accounts get a generated slot that is never
/// reused, so removing one and adding another cannot inherit settings (upstream
/// AccountKey rule). Secrets stay in the ICredentialStore (DPAPI vault) — this
/// layer only tracks WHICH accounts exist.
/// </summary>
public sealed class MultiAccountStore
{
    private static string DefaultIndexPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "accounts.json");

    private readonly string _indexPath;
    private readonly ICredentialStore _store;
    private readonly object _lock = new();

    public MultiAccountStore(ICredentialStore store, string? indexPath = null)
    {
        _store = store;
        _indexPath = indexPath ?? DefaultIndexPath();
    }

    private sealed record AccountIndex(List<string> Slots)
    {
        public List<string> Slots { get; init; } = Slots ?? [];
    }

    public sealed record StoredAccount(string Slot, string? Label, DateTimeOffset AddedAt);

    private static readonly Dictionary<ProviderId, List<StoredAccount>> Empty = new();

    public IReadOnlyList<StoredAccount> List(ProviderId provider)
    {
        lock (_lock) return LoadAll().GetValueOrDefault(provider, []).AsReadOnly();
    }

    /// <summary>Add an account slot for a provider and store its token. Returns the slot id.</summary>
    public string Add(ProviderId provider, string secret, string? label = null)
    {
        lock (_lock)
        {
            var all = LoadAll();
            var slots = all.GetValueOrDefault(provider, []);

            // A slot generated once and never reused.
            var slot = provider + "#" + Guid.NewGuid().ToString("N")[..8];
            slots.Add(new StoredAccount(slot, label, DateTimeOffset.Now));
            all[provider] = slots;

            SaveAll(all);
            _store.SetSecret(provider, slot, secret);
            return slot;
        }
    }

    public void Remove(ProviderId provider, string slot)
    {
        lock (_lock)
        {
            var all = LoadAll();
            if (!all.TryGetValue(provider, out var slots)) return;
            all[provider] = slots.Where(s => s.Slot != slot).ToList();
            SaveAll(all);
            _store.RemoveSecret(provider, slot);
        }
    }

    /// <summary>Turn stored slots into monitored accounts (primary first, then added).</summary>
    public IEnumerable<MonitoredAccount> MonitoredAccounts(ProviderId provider)
    {
        yield return new MonitoredAccount { Provider = provider, AccountId = provider.ToString(), IsBorrowed = true };
        foreach (var stored in List(provider))
            yield return new MonitoredAccount
            {
                Provider = provider,
                AccountId = stored.Slot,
                Label = stored.Label ?? stored.Slot,
                IsBorrowed = false,
            };
    }

    private Dictionary<ProviderId, List<StoredAccount>> LoadAll()
    {
        try
        {
            if (File.Exists(_indexPath))
            {
                var json = File.ReadAllText(_indexPath);
                var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<StoredAccount>>>(json);
                if (raw is not null)
                    return raw.ToDictionary(kv => Enum.TryParse<ProviderId>(kv.Key, out var id) ? id : (ProviderId)(-1), kv => kv.Value)
                        .Where(kv => kv.Key != (ProviderId)(-1))
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        }
        catch (Exception) { }
        return new Dictionary<ProviderId, List<StoredAccount>>(Empty);
    }

    private void SaveAll(Dictionary<ProviderId, List<StoredAccount>> all)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_indexPath)!);
            var json = System.Text.Json.JsonSerializer.Serialize(
                all.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));
            File.WriteAllText(_indexPath, json);
        }
        catch (Exception) { }
    }
}

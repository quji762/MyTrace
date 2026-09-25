using Pulse.Core.Accounts;
using Pulse.Core.Providers;

namespace Pulse.Auth;

/// <summary>
/// Which providers can hold added accounts. Claude/Codex/Grok/GrokBot mirror
/// upstream `Provider.supportsMultipleAccounts`. Antigravity is a Windows-side
/// extension: its quota lives in a local language server, so an added account is
/// a second server connection (ports + CSRF) rather than an OAuth login. NOT
/// two, not Cursor (an account-labeled UI would promise a behavior the
/// provider's credential model cannot deliver).
/// </summary>
public static class MultiAccountCapability
{
    public static bool Supports(ProviderId provider) => provider is
        ProviderId.ClaudeCode or ProviderId.Codex or ProviderId.Grok or ProviderId.GrokBot
        or ProviderId.Antigravity;
}

/// <summary>
/// Multi-account store on top of the credential vault. The primary account uses
/// the provider's raw id; added accounts get a generated slot that is never
/// reused, so removing one and adding another cannot inherit settings (upstream
/// AccountKey rule). Secrets stay in the ICredentialStore (DPAPI vault) �?this
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

            // Secret first: a failed vault write must not leave an empty slot.
            _store.SetSecret(provider, slot, secret);
            SaveAll(all);
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

    /// <summary>Rename an added account. The slot id never changes.</summary>
    public void Rename(ProviderId provider, string slot, string? label)
    {
        lock (_lock)
        {
            var all = LoadAll();
            if (!all.TryGetValue(provider, out var slots)) return;
            var index = slots.FindIndex(s => s.Slot == slot);
            if (index < 0) return;
            var existing = slots[index];
            slots[index] = existing with { Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim() };
            SaveAll(all);
        }
    }

    /// <summary>Replace an added account's secret in place (Sign in again).
    /// A slot that was removed while a sign-in was pending is left alone �?
    /// writing back would resurrect a deleted account.</summary>
    public bool ReplaceSecret(ProviderId provider, string slot, string secret)
    {
        lock (_lock)
        {
            var all = LoadAll();
            if (!all.TryGetValue(provider, out var slots) || slots.All(s => s.Slot != slot))
                return false;
            _store.SetSecret(provider, slot, secret);
            return true;
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
                {
                    var result = new Dictionary<ProviderId, List<StoredAccount>>();
                    foreach (var kv in raw)
                    {
                        if (Enum.TryParse<ProviderId>(kv.Key, out var id))
                            result[id] = kv.Value;
                    }
                    return result;
                }
            }
        }
        catch (Exception) { }
        return new Dictionary<ProviderId, List<StoredAccount>>(Empty);
    }

    private void SaveAll(Dictionary<ProviderId, List<StoredAccount>> all)
    {
        try
        {
            Pulse.Core.Platform.SecureState.EnsureStateDirectory();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_indexPath)!);
            Pulse.Core.Platform.SecureState.Protect(_indexPath);
            var json = System.Text.Json.JsonSerializer.Serialize(
                all.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));
            Pulse.Core.Platform.SecureState.WriteStateTextAtomic(_indexPath, json);
        }
        catch (Exception) { }
    }
}

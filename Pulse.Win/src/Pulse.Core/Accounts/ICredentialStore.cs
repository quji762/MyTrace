using Pulse.Core.Providers;

namespace Pulse.Core.Accounts;

/// <summary>
/// Secret storage boundary. Providers never read files or keychains themselves:
/// they ask the store, which in production is backed by Windows Credential Manager
/// + DPAPI and keeps secrets out of settings/SQLite/log paths.
/// </summary>
public interface ICredentialStore
{
    /// <summary>The secret for an account, or null when none is stored.</summary>
    string? GetSecret(ProviderId provider, string accountId);

    void SetSecret(ProviderId provider, string accountId, string secret);

    void RemoveSecret(ProviderId provider, string accountId);
}

/// <summary>In-memory store for tests and the pre-vault milestone.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly object _lock = new();
    private readonly Dictionary<(ProviderId, string), string> _secrets = new();

    public string? GetSecret(ProviderId provider, string accountId)
    {
        lock (_lock) return _secrets.TryGetValue((provider, accountId), out var secret) ? secret : null;
    }

    public void SetSecret(ProviderId provider, string accountId, string secret)
    {
        lock (_lock) _secrets[(provider, accountId)] = secret;
    }

    public void RemoveSecret(ProviderId provider, string accountId)
    {
        lock (_lock) _secrets.Remove((provider, accountId));
    }
}

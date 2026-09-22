using Pulse.Core.Accounts;
using Pulse.Core.Providers;

namespace Pulse.Auth;

/// <summary>
/// Presents vault secrets to providers as the access token they can send.
/// A plain pasted key is returned as-is. An OAuth bundle is unwrapped, and when
/// it is inside the expiry skew it is renewed and written back to the same
/// account slot. The CLI's own files are never opened here.
/// </summary>
public sealed class RefreshingCredentialStore : ICredentialStore
{
    private readonly ICredentialStore _inner;
    private readonly Dictionary<(ProviderId, string), object> _gates = new();

    public RefreshingCredentialStore(ICredentialStore inner) => _inner = inner;

    public string? GetSecret(ProviderId provider, string accountId)
    {
        var raw = _inner.GetSecret(provider, accountId);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var tokens = OAuthTokens.Deserialize(raw);
        if (tokens is null) return raw.Trim();
        if (!Due(tokens) || ConfigFor(provider) is not { } config)
            return tokens.AccessToken;

        lock (Gate(provider, accountId))
        {
            raw = _inner.GetSecret(provider, accountId);
            tokens = OAuthTokens.Deserialize(raw) ?? tokens;
            if (!Due(tokens)) return tokens.AccessToken;

            try
            {
                var renewed = Task.Run(async () =>
                    await OAuthRefresh.RefreshAsync(config, tokens).ConfigureAwait(false))
                    .GetAwaiter().GetResult();
                if (renewed is null) return tokens.AccessToken;
                _inner.SetSecret(provider, accountId, renewed.Serialize());
                return renewed.AccessToken;
            }
            catch (Exception)
            {
                // A failed renewal still has the previous access token. The
                // provider reports a refusal if that token is already dead.
                return tokens.AccessToken;
            }
        }
    }

    public void SetSecret(ProviderId provider, string accountId, string secret) =>
        _inner.SetSecret(provider, accountId, secret);

    public void RemoveSecret(ProviderId provider, string accountId) =>
        _inner.RemoveSecret(provider, accountId);

    private object Gate(ProviderId provider, string accountId)
    {
        lock (_gates)
        {
            if (!_gates.TryGetValue((provider, accountId), out var gate))
                _gates[(provider, accountId)] = gate = new object();
            return gate;
        }
    }

    private static bool Due(OAuthTokens tokens) =>
        !string.IsNullOrEmpty(tokens.RefreshToken)
        && tokens.ExpiresAt is { } expires
        && expires <= DateTimeOffset.UtcNow.AddMinutes(2);

    private static ProviderConfig? ConfigFor(ProviderId provider) => provider switch
    {
        ProviderId.ClaudeCode => ProviderConfig.Claude,
        ProviderId.Codex => ProviderConfig.Codex,
        ProviderId.Grok => ProviderConfig.Grok,
        _ => null,
    };
}

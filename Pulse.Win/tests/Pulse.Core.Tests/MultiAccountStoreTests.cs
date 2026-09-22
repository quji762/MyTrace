using Pulse.Auth;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Storage;
using Xunit;

namespace Pulse.Core.Tests;

public class MultiAccountStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryCredentialStore _store;
    private readonly MultiAccountStore _multiAccount;

    public MultiAccountStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pulse-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new InMemoryCredentialStore();
        _multiAccount = new MultiAccountStore(_store, Path.Combine(_tempDir, "accounts.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void Capability_Matches_Upstream_SupportsMultipleAccounts()
    {
        // claudeCode, codex, grok, grokBot, plus Antigravity (Windows: second
        // language-server connection). Not two, not Cursor.
        Assert.True(MultiAccountCapability.Supports(ProviderId.ClaudeCode));
        Assert.True(MultiAccountCapability.Supports(ProviderId.Codex));
        Assert.True(MultiAccountCapability.Supports(ProviderId.Grok));
        Assert.True(MultiAccountCapability.Supports(ProviderId.GrokBot));
        Assert.True(MultiAccountCapability.Supports(ProviderId.Antigravity));
        Assert.False(MultiAccountCapability.Supports(ProviderId.Cursor));
        Assert.False(MultiAccountCapability.Supports(ProviderId.DeepSeek));
        Assert.False(MultiAccountCapability.Supports(ProviderId.Copilot));
        Assert.True(Pulse.Core.Providers.ProviderCapabilities.For(ProviderId.Antigravity).SupportsMultipleAccounts);
    }

    [Fact]
    public void Add_Generates_Unique_Non_Reused_Slots()
    {
        var first = _multiAccount.Add(ProviderId.Codex, "token-1");
        _multiAccount.Remove(ProviderId.Codex, first);
        var second = _multiAccount.Add(ProviderId.Codex, "token-2");

        // Removing one and adding another cannot inherit the removed slot's identity.
        Assert.NotEqual(first, second);
        Assert.StartsWith("Codex#", second);
    }

    [Fact]
    public void Added_Secrets_Go_To_The_Vault_Under_Their_Slot()
    {
        var slot = _multiAccount.Add(ProviderId.Grok, "grok-token");
        // Primary slot untouched; the added slot carries its own secret.
        Assert.Equal("grok-token", _store.GetSecret(ProviderId.Grok, slot));
    }

    [Fact]
    public void Remove_Deletes_Slot_And_Secret()
    {
        var slot = _multiAccount.Add(ProviderId.Codex, "token");
        _multiAccount.Remove(ProviderId.Codex, slot);
        Assert.Null(_store.GetSecret(ProviderId.Codex, slot));
        Assert.Empty(_multiAccount.List(ProviderId.Codex));
    }

    [Fact]
    public void Providers_Are_Isolated()
    {
        var codexSlot = _multiAccount.Add(ProviderId.Codex, "codex-token");
        var claudeSlot = _multiAccount.Add(ProviderId.ClaudeCode, "claude-token");
        Assert.NotEqual(codexSlot, claudeSlot);
        Assert.Single(_multiAccount.List(ProviderId.Codex));
        Assert.Single(_multiAccount.List(ProviderId.ClaudeCode));
    }

    [Fact]
    public void Rename_Changes_The_Label_Only()
    {
        var slot = _multiAccount.Add(ProviderId.Codex, "token", label: "old");
        _multiAccount.Rename(ProviderId.Codex, slot, "工作");
        var stored = Assert.Single(_multiAccount.List(ProviderId.Codex));
        Assert.Equal(slot, stored.Slot);
        Assert.Equal("工作", stored.Label);
        Assert.Equal("token", _store.GetSecret(ProviderId.Codex, slot));
    }

    [Fact]
    public void ReplaceSecret_Keeps_The_Slot_And_Refuses_Deleted_Ones()
    {
        var slot = _multiAccount.Add(ProviderId.Grok, "old-token", label: "kept");
        Assert.True(_multiAccount.ReplaceSecret(ProviderId.Grok, slot, "new-token"));
        Assert.Equal("new-token", _store.GetSecret(ProviderId.Grok, slot));
        Assert.Equal("kept", Assert.Single(_multiAccount.List(ProviderId.Grok)).Label);

        _multiAccount.Remove(ProviderId.Grok, slot);
        // A sign-in that finishes after Remove must not resurrect the account.
        Assert.False(_multiAccount.ReplaceSecret(ProviderId.Grok, slot, "zombie"));
        Assert.Null(_store.GetSecret(ProviderId.Grok, slot));
        Assert.Empty(_multiAccount.List(ProviderId.Grok));
    }

    [Fact]
    public void OAuthTokens_Serialize_Carries_Refresh_Forward()
    {
        // Refresh with no new refresh token in the reply keeps the old one valid.
        var tokens = new OAuthTokens("at", "rt", null);
        var restored = OAuthTokens.Deserialize(tokens.Serialize());
        Assert.Equal("rt", restored!.RefreshToken);
    }
}

/// <summary>Antigravity added-account connections: ports + CSRF survive a
/// round-trip, and anything that cannot name both is treated as absent.</summary>
public class AntigravityConnectionTests
{
    [Fact]
    public void Connection_Round_Trips_Ports_And_Token()
    {
        var connection = new Pulse.Providers.Antigravity.AntigravityConnection([1234, 5678], "csrf-token");
        var restored = Pulse.Providers.Antigravity.AntigravityConnection.Parse(connection.Serialize());
        Assert.NotNull(restored);
        Assert.Equal([1234, 5678], restored!.Ports);
        Assert.Equal("csrf-token", restored.Token);
    }

    [Fact]
    public void Missing_Or_Malformed_Secrets_Parse_As_Absent()
    {
        Assert.Null(Pulse.Providers.Antigravity.AntigravityConnection.Parse(null));
        Assert.Null(Pulse.Providers.Antigravity.AntigravityConnection.Parse(""));
        Assert.Null(Pulse.Providers.Antigravity.AntigravityConnection.Parse("not-json"));
        Assert.Null(Pulse.Providers.Antigravity.AntigravityConnection.Parse("""{"token":"t"}"""));
        Assert.Null(Pulse.Providers.Antigravity.AntigravityConnection.Parse("""{"ports":[1234]}"""));
        Assert.Null(Pulse.Providers.Antigravity.AntigravityConnection.Parse("""{"ports":[],"token":"t"}"""));
    }
}

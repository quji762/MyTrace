using System.Text;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Providers;
using Pulse.Storage;
using Xunit;

namespace Pulse.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class CredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pulse-vault-{Guid.NewGuid():N}");

    public CredentialStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void Vault_Keeps_Primary_And_Added_Account_Apart()
    {
        var store = new DpapiCredentialStore(_directory, () => null);
        store.SetSecret(ProviderId.Grok, "Grok", "primary-token");
        store.SetSecret(ProviderId.Grok, "Grok#abc", "added-token");

        Assert.Equal("primary-token", store.GetSecret(ProviderId.Grok, "Grok"));
        Assert.Equal("added-token", store.GetSecret(ProviderId.Grok, "Grok#abc"));

        store.RemoveSecret(ProviderId.Grok, "Grok#abc");
        Assert.Equal("primary-token", store.GetSecret(ProviderId.Grok, "Grok"));
        Assert.Null(store.GetSecret(ProviderId.Grok, "Grok#abc"));
    }

    [Fact]
    public void Vault_Reads_A_Legacy_Single_Entry_File()
    {
        var legacy = JsonSerializer.Serialize(new
        {
            Provider = "Grok",
            AccountId = "Grok",
            Secret = "legacy-key",
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(legacy));
        File.WriteAllBytes(Path.Combine(_directory, "Grok.bin"), encrypted);

        var store = new DpapiCredentialStore(_directory, () => null);
        Assert.Equal("legacy-key", store.GetSecret(ProviderId.Grok, "Grok"));
        Assert.Null(store.GetSecret(ProviderId.Grok, "Grok#other"));
    }

    [Fact]
    public void Registry_Includes_Every_Provider_Including_Grok()
    {
        var adapters = ProviderRegistry.CreateAll(new InMemoryCredentialStore());
        Assert.Equal(Enum.GetValues<ProviderId>().Length, adapters.Count);
        Assert.Contains(ProviderId.Grok, adapters.Keys);
    }
}

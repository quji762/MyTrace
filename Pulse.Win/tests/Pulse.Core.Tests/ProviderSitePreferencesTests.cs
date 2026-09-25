using Pulse.Core.Platform;
using Pulse.Core.Providers;
using Xunit;

namespace Pulse.Core.Tests;

public class ProviderSitePreferencesTests
{
    private static string TempPath()
    {
        var file = Path.Combine(Path.GetTempPath(), $"pulse-sites-{Guid.NewGuid():N}.json");
        File.Delete(file);
        return file;
    }

    [Fact]
    public void Defaults_Follow_Upstream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulse-sites-{Guid.NewGuid():N}.json");
        // Qoder reads the international site, StepFun the China one.
        Assert.False(ProviderSitePreferences.UseChina(ProviderId.Qoder, path));
        Assert.True(ProviderSitePreferences.UseChina(ProviderId.StepFun, path));
        // Providers without a site choice never report one.
        Assert.False(ProviderSitePreferences.HasSiteChoice(ProviderId.DeepSeek));
        Assert.False(ProviderSitePreferences.UseChina(ProviderId.DeepSeek, path));
    }

    [Fact]
    public void Save_Then_Load_RoundTrips_Per_Provider()
    {
        var path = TempPath();
        try
        {
            ProviderSitePreferences.Save(ProviderId.Qoder, useChina: true, path);
            ProviderSitePreferences.Save(ProviderId.StepFun, useChina: false, path);

            Assert.True(ProviderSitePreferences.UseChina(ProviderId.Qoder, path));
            Assert.False(ProviderSitePreferences.UseChina(ProviderId.StepFun, path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Providers_Without_A_Choice_Are_Never_Persisted()
    {
        var path = TempPath();
        try
        {
            ProviderSitePreferences.Save(ProviderId.DeepSeek, useChina: true, path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

using Pulse.Core.Platform;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>NetworkProxy: Follow System leaves handlers alone; Manual sets one
/// endpoint and helper env; host+port validity gates HasEndpoint.</summary>
public class NetworkProxyTests
{
    private static string TempPath() => Path.Combine(
        Path.GetTempPath(), $"pulse-proxy-{Guid.NewGuid():N}", "network.json");

    [Fact]
    public void Default_Is_Follow_System_And_Does_Not_Proxy()
    {
        Assert.Equal(ProxyMode.System, NetworkProxy.Default.Mode);
        Assert.False(NetworkProxy.Default.HasEndpoint);
        Assert.Null(NetworkProxy.Default.EndpointUri);
        Assert.Empty(NetworkProxy.Default.HelperEnvironment());

        var handler = new SocketsHttpHandler();
        NetworkProxy.Default.ApplyTo(handler);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void Manual_Endpoint_Configures_Http_And_Helper_Environment()
    {
        var proxy = new NetworkProxy(ProxyMode.Manual, "proxy.local", 8080);
        Assert.True(proxy.HasEndpoint);
        Assert.Equal(new Uri("http://proxy.local:8080"), proxy.EndpointUri);

        var handler = new SocketsHttpHandler();
        proxy.ApplyTo(handler);
        Assert.NotNull(handler.Proxy);

        var env = proxy.HelperEnvironment();
        Assert.Equal("http://proxy.local:8080", env["HTTPS_PROXY"]);
        Assert.Contains("127.0.0.1", env["NO_PROXY"]);
    }

    [Fact]
    public void Invalid_Host_Or_Port_Is_Not_An_Endpoint()
    {
        Assert.False(new NetworkProxy(ProxyMode.Manual, "", 8080).HasEndpoint);
        Assert.False(new NetworkProxy(ProxyMode.Manual, "host", 0).HasEndpoint);
        Assert.False(new NetworkProxy(ProxyMode.Manual, "host", 70000).HasEndpoint);
    }

    [Fact]
    public void Round_Trip_Through_The_Json_File()
    {
        var path = TempPath();
        try
        {
            new NetworkProxy(ProxyMode.Manual, "p.example", 3128).Save(path);
            var loaded = NetworkProxy.Load(path);
            Assert.Equal(ProxyMode.Manual, loaded.Mode);
            Assert.Equal("p.example", loaded.Host);
            Assert.Equal(3128, loaded.Port);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>DeepLink pulse:// parse and the second-instance pending handoff.</summary>
public class DeepLinkTests
{
    [Fact]
    public void Parses_Show_Settings_And_Account()
    {
        Assert.Equal(DeepLink.Command.Show, DeepLink.Parse(["pulse://show"])!.Action);
        Assert.Equal(DeepLink.Command.Settings, DeepLink.ParseUri("pulse://settings")!.Action);

        var account = DeepLink.ParseUri("pulse://account/ClaudeCode#abc");
        Assert.Equal(DeepLink.Command.Account, account!.Action);
        Assert.Equal("ClaudeCode#abc", account.AccountId);

        Assert.Null(DeepLink.Parse(["--statusline"]));
        Assert.Null(DeepLink.ParseUri("https://example.com"));
    }

    [Fact]
    public void Pending_Command_Round_Trips_And_Is_Consumed()
    {
        DeepLink.WritePending(new DeepLink.Command(DeepLink.Command.Account, "Codex#1"));
        var taken = DeepLink.TakePending();
        Assert.NotNull(taken);
        Assert.Equal(DeepLink.Command.Account, taken!.Action);
        Assert.Equal("Codex#1", taken.AccountId);
        Assert.Null(DeepLink.TakePending());
    }
}

using System.Net;

namespace Pulse.Core.Platform;

/// <summary>Follow System (default) or one manual HTTP/SOCKS endpoint. Mirrors
/// upstream NetworkProxy: one persisted value, host+port commit together, no
/// per-provider override and no direct/no-proxy mode. Loopback is never proxied.</summary>
public enum ProxyMode
{
    System,
    Manual,
}

public sealed record NetworkProxy(ProxyMode Mode, string? Host, int? Port)
{
    public static NetworkProxy Default { get; } = new(ProxyMode.System, null, null);

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PulseWin", "network.json");

    /// <summary>True only for a manual mode with a non-empty host and a port in 1–65535.</summary>
    public bool HasEndpoint =>
        Mode == ProxyMode.Manual
        && !string.IsNullOrWhiteSpace(Host)
        && Port is > 0 and < 65536;

    public Uri? EndpointUri =>
        HasEndpoint ? new Uri($"http://{Host!.Trim()}:{Port!.Value}") : null;

    public static NetworkProxy Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            if (!File.Exists(file)) return Default;
            var json = File.ReadAllText(file);
            var saved = System.Text.Json.JsonSerializer.Deserialize<NetworkProxy>(json);
            return saved is { Mode: ProxyMode.Manual, Host: not null } ? saved : Default;
        }
        catch (Exception)
        {
            return Default;
        }
    }

    public void Save(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(this));
        }
        catch (Exception)
        {
            // A locked settings file leaves the previous proxy in force.
        }
    }

    /// <summary>Apply to a shared handler. Manual sets the proxy; System leaves
    /// the platform default alone (WinINET / WinHTTP / environment).</summary>
    public void ApplyTo(SocketsHttpHandler handler)
    {
        if (EndpointUri is { } uri)
        {
            handler.Proxy = new WebProxy(uri);
            handler.UseProxy = true;
        }
    }

    /// <summary>Child-process environment for helpers that speak HTTP(S)_PROXY.
    /// Follow System injects nothing — that preserves whatever the process
    /// already inherited. Loopback stays direct.</summary>
    public IReadOnlyDictionary<string, string> HelperEnvironment()
    {
        if (EndpointUri is not { } uri) return new Dictionary<string, string>();
        var value = uri.ToString().TrimEnd('/');
        return new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = value,
            ["HTTPS_PROXY"] = value,
            ["NO_PROXY"] = "localhost,127.0.0.1,::1",
        };
    }
}

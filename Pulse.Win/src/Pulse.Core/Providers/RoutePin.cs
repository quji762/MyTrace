namespace Pulse.Core.Providers;

/// <summary>
/// Which route a primary Claude or Codex account is allowed to answer from.
/// A pin refuses a successful reading that came from the other route.
/// </summary>
public enum RoutePin
{
    Automatic,
    EndpointOnly,
    AlternateOnly,
}

public static class RoutePinning
{
    public static ProviderReadResult Select(
        RoutePin pin,
        ProviderReadResult endpoint,
        Func<ProviderReadResult> alternate) =>
        SelectAsync(pin, endpoint, () => Task.FromResult(alternate())).GetAwaiter().GetResult();

    public static async Task<ProviderReadResult> SelectAsync(
        RoutePin pin,
        ProviderReadResult endpoint,
        Func<Task<ProviderReadResult>> alternate)
    {
        if (pin == RoutePin.EndpointOnly) return endpoint;
        if (pin == RoutePin.AlternateOnly) return await alternate().ConfigureAwait(false);
        if (endpoint.Health == ProviderReadHealth.Healthy && endpoint.Usage is { Windows.Count: > 0 })
            return endpoint;
        return await alternate().ConfigureAwait(false);
    }
}

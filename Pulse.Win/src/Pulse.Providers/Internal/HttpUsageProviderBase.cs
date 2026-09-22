using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;

namespace Pulse.Providers.Internal;

/// <summary>
/// Shared plumbing for key-based HTTP providers: auth header, timeout, status-code
/// mapping into contract health, and JSON parsing. Mirrors the common shape of the
/// upstream DeepSeek/Kimi/OpenCode services.
/// </summary>
public abstract class HttpUsageProviderBase : IUsageProvider
{
    public abstract ProviderId Id { get; }
    public abstract ProviderCapabilities Capabilities { get; }

    protected abstract string Endpoint { get; }

    /// <summary>Subclasses with endpoint fallbacks may pin which one the next request uses.</summary>
    protected virtual string EndpointOrDefault => Endpoint;

    protected virtual AuthenticationHeaderValue? AuthHeader(string credential) =>
        new AuthenticationHeaderValue("Bearer", credential);

    protected virtual IReadOnlyDictionary<string, string> ExtraHeaders() =>
        new Dictionary<string, string>();

    /// <summary>Parse a successful (2xx) response body into usage. Throw SchemaException on unexpected shape.</summary>
    protected abstract ProviderUsage ParseSuccess(JsonDocument document, MonitoredAccount account, DateTimeOffset now);

    /// <summary>Build the outgoing request; override for POST/custom headers.</summary>
    protected virtual HttpRequestMessage BuildRequest(string credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, EndpointOrDefault);
        request.Headers.Authorization = AuthHeader(credential);
        foreach (var (name, value) in ExtraHeaders())
            request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    public virtual async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account,
        ProviderReadContext context,
        CancellationToken cancellationToken)
    {
        var credential = ResolveCredential(account, context);
        if (string.IsNullOrEmpty(credential))
            return ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, "credential missing");

        using var httpClient = HttpClientFactory.CreateClient();
        using var request = BuildRequest(credential!);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, ex.Message.Length > 200 ? ex.Message[..200] : ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "timeout");
        }

        using (response)
        {
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => ProviderReadResult.Failed(ProviderReadHealth.Unauthorized),
                System.Net.HttpStatusCode.Forbidden => ProviderReadResult.Failed(ProviderReadHealth.Unauthorized),
                System.Net.HttpStatusCode.TooManyRequests => ProviderReadResult.Failed(ProviderReadHealth.RateLimited),
                _ => await ReadBodyAsync(response, account, context.Now, cancellationToken).ConfigureAwait(false),
            };
        }
    }

    private async Task<ProviderReadResult> ReadBodyAsync(HttpResponseMessage response, MonitoredAccount account, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, $"HTTP {(int)response.StatusCode}");

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "body read failed");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var usage = ParseSuccess(document, account, now);
            return ProviderReadResult.Ok(usage);
        }
        catch (JsonException)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.SchemaChanged, "invalid JSON");
        }
        catch (SchemaException ex)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.SchemaChanged, ex.Message);
        }
        catch (Pulse.Providers.Zai.ZaiProvider.EnvelopeException ex)
        {
            // The envelope classified itself (bad key vs rate limit vs server error);
            // trust that verdict rather than flattening it into a schema change.
            return ProviderReadResult.Failed(ex.Kind switch
            {
                UnavailabilityKind.Unauthorized => ProviderReadHealth.Unauthorized,
                UnavailabilityKind.RateLimited => ProviderReadHealth.RateLimited,
                UnavailabilityKind.NotConfigured => ProviderReadHealth.ProviderUnavailable,
                _ => ProviderReadHealth.ProviderUnavailable,
            }, ex.Message);
        }
        catch (Pulse.Providers.MiniMax.MiniMaxProvider.EnvelopeException ex)
        {
            return ProviderReadResult.Failed(ex.Kind switch
            {
                UnavailabilityKind.Unauthorized => ProviderReadHealth.Unauthorized,
                UnavailabilityKind.RateLimited => ProviderReadHealth.RateLimited,
                _ => ProviderReadHealth.ProviderUnavailable,
            }, ex.Message);
        }
    }

    /// <summary>Resolve the API key for this account from the credential store.</summary>
    protected abstract string? ResolveCredential(MonitoredAccount account, ProviderReadContext context);

    /// <summary>Thrown when a response does not match the documented schema.</summary>
    public sealed class SchemaException : Exception
    {
        public SchemaException(string message) : base(message) { }
    }

    /// <summary>Parses ISO8601 timestamps with optional fractional seconds (upstream pattern).</summary>
    protected static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;
        return null;
    }

    protected static double? GetDouble(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    protected static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    protected static bool TryGetProperty(JsonElement element, string name, out JsonValueKind kind)
    {
        kind = JsonValueKind.Undefined;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty(name, out var property)) return false;
        kind = property.ValueKind;
        return true;
    }
}

/// <summary>Thin HttpClient wrapper so tests can inject a stub handler.
/// Production traffic follows <see cref="Pulse.Core.Platform.NetworkProxy"/>.</summary>
public static class HttpClientFactory
{
    public static Func<HttpMessageHandler>? HandlerOverride;

    public static HttpClient CreateClient()
    {
        if (HandlerOverride is not null)
            return new HttpClient(HandlerOverride(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        Pulse.Core.Platform.NetworkProxy.Load().ApplyTo(handler);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PulseWin/0.1");
        return client;
    }
}

internal static class CultureInfoExtensions
{
    public static bool TryParse(this string s, NumberStyles style, IFormatProvider provider, out double value) =>
        double.TryParse(s, style, provider, out value);
}

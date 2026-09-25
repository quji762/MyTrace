using System.Globalization;
using System.Text;
using System.Text.Json;
using Pulse.Core.Accounts;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Providers.Internal;

namespace Pulse.Providers.StepFun;

/// <summary>
/// StepFun's Step Plan, read the way its own console reads it.
/// Port of upstream StepFunUsageService.
///
/// **A browser session, not a key.** The Step API key buys inference; the
/// plan's allowance is only on the console, which asks
/// POST /api/step.openapi.devcenter.Dashboard/QueryStepPlanRateLimit with the
/// signed-in cookies. That session is the credential, as Qoder's and Xiaomi's
/// are. It is stored in the local DPAPI vault and sent only to the console
/// host over HTTPS.
///
/// **Two sites, two accounts.** platform.stepfun.com and platform.stepfun.ai
/// are separate sign-ins; a session for one is never sent to the other.
///
/// **Two plans, two shapes.** The Token Plan is a monthly pool of Credits plus
/// 30-day top-up packs, each a bucket with its own size, remainder and end
/// date. The Coding Plan meters a five-hour and a weekly window as a remaining
/// fraction and a reset time. One reply carries whichever the account has, the
/// other's fields zeroed.
/// </summary>
public sealed class StepFunProvider : IUsageProvider
{
    /// <summary>What the console's own request carries as its browser identity.</summary>
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36";

    private readonly Func<string?, string?> _credentialResolver;
    private readonly Func<bool> _useChina;

    public StepFunProvider(Func<string?, string?>? credentialResolver = null, Func<bool>? useChina = null)
    {
        _credentialResolver = credentialResolver ?? (key => key);
        // Read per request: a site switch applies on the next refresh pass.
        _useChina = useChina ?? (() => true); // the China site, as upstream's default
    }

    public ProviderId Id => ProviderId.StepFun;
    public ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.StepFun);

    /// <summary>The console's host, and the only host whose cookies are read.</summary>
    private string Host => _useChina() ? "platform.stepfun.com" : "platform.stepfun.ai";
    private string Origin => "https://" + Host;
    private string MethodUrl(string method) => $"{Origin}/api/step.openapi.devcenter.Dashboard/{method}";

    public async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account,
        ProviderReadContext context,
        CancellationToken cancellationToken)
    {
        var pasted = _credentialResolver(account.AccountId);
        if (string.IsNullOrWhiteSpace(pasted))
            return ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, "credential missing");

        string header;
        try
        {
            header = SessionCookie.Normalize(pasted);
        }
        catch (SessionCookie.CookieException ex)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, ex.Message);
        }

        string body;
        try
        {
            using var client = HttpClientFactory.CreateClient();
            body = await Post(client, "QueryStepPlanRateLimit", header, cancellationToken).ConfigureAwait(false);
        }
        catch (ReadHttpException ex)
        {
            return ProviderReadResult.Failed(ex.Health, ex.Detail);
        }
        catch (HttpRequestException ex)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable,
                ex.Message.Length > 200 ? ex.Message[..200] : ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "timeout");
        }

        ProviderReadResult result;
        try
        {
            using var document = JsonDocument.Parse(body);
            result = ParseReply(document, account, context.Now);
        }
        catch (JsonException)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.SchemaChanged, "invalid JSON");
        }

        // The plan's name is a second request and a nicety. Its failure costs
        // the name and nothing else.
        if (result.Health == ProviderReadHealth.Healthy && result.Usage is { State: UsageState.Live } live)
        {
            try
            {
                using var client = HttpClientFactory.CreateClient();
                var status = await Post(client, "GetStepPlanStatus", header, cancellationToken).ConfigureAwait(false);
                if (PlanName(status) is { } name)
                    result = result with { Usage = live with { Plan = name } };
            }
            catch (Exception)
            {
                // The figures do not depend on the name.
            }
        }

        return result;
    }

    private async Task<string> Post(HttpClient client, string method, string header, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MethodUrl(method))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Cookie", header);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        // The page that makes this request, so the Referer is true.
        request.Headers.TryAddWithoutValidation("Referer", $"{Origin}/plan-usage");
        // What the console's own request carries: its app id, its platform,
        // and the device the token belongs to. A token presented from another
        // device id is refused as stolen.
        request.Headers.TryAddWithoutValidation("oasis-appid", "10300");
        request.Headers.TryAddWithoutValidation("oasis-platform", "web");
        if (SessionCookie.WebId(header) is { } webId)
            request.Headers.TryAddWithoutValidation("oasis-webid", webId);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var code = (int)response.StatusCode;
        // Signed out: `{"code":"unauthenticated","message":"auth failed: …"}` with a 401, measured.
        if (code is 401 or 403) throw new ReadHttpException(ProviderReadHealth.Unauthorized, "session refused");
        if (code == 429) throw new ReadHttpException(ProviderReadHealth.RateLimited, "HTTP 429");
        if (code is >= 500 and <= 599) throw new ReadHttpException(ProviderReadHealth.ProviderUnavailable, $"HTTP {code}");
        if (!response.IsSuccessStatusCode) throw new ReadHttpException(ProviderReadHealth.SchemaChanged, $"HTTP {code}");
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private ProviderReadResult ParseReply(JsonDocument document, MonitoredAccount account, DateTimeOffset now)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return ProviderReadResult.Failed(ProviderReadHealth.SchemaChanged, "root is not an object");

        var status = Number(root, "status");
        if (status is not { } ok || ok != 1)
        {
            // A refusal inside a 200. The console words an auth failure as
            // such; anything else is a reply this cannot use.
            var words = string.Join(" ",
                new[] { Text(root, "desc"), Text(root, "message"), Text(root, "code") }
                    .Where(part => part is not null)).ToLowerInvariant();
            var health = words.Contains("auth") || words.Contains("token")
                ? ProviderReadHealth.Unauthorized
                : ProviderReadHealth.SchemaChanged;
            return ProviderReadResult.Failed(health, words.Length > 0 ? words : "status not ok");
        }

        var fiveHour = Window(root, "five_hour_usage_left_rate", "five_hour_usage_reset_time");
        var weekly = Window(root, "weekly_usage_left_rate", "weekly_usage_reset_time");

        var windows = new List<UsageWindow>();
        if (fiveHour is not null || weekly is not null)
        {
            // The Coding Plan's two windows are the plan's stated lengths, so
            // they are reported ones.
            if (fiveHour is { } h)
                windows.Add(new UsageWindow("stepfun.5h", UsageWindowKind.FiveHour, null,
                    1 - h.Remaining, 5 * 3600, h.ResetsAt, true, null, h.Remaining <= 0));
            if (weekly is { } d)
                windows.Add(new UsageWindow("stepfun.weekly", UsageWindowKind.Weekly, null,
                    1 - d.Remaining, 7 * 86_400, d.ResetsAt, true, null, d.Remaining <= 0));
        }
        else
        {
            root.TryGetProperty("plan_credit_rate_limit", out var credit);
            var buckets = new List<(double Remaining, DateTimeOffset? ExpiresAt, DateTimeOffset? NextResetAt)>();
            double bucketTotal = 0;
            if (credit.ValueKind == JsonValueKind.Object
                && credit.TryGetProperty("credit_buckets", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    var total = Number(entry, "credit_total");
                    var residual = Number(entry, "credit_residual");
                    if (total is not { } t || t <= 0 || residual is not { } r || r < 0) continue;
                    bucketTotal += t;
                    buckets.Add((Math.Min(r, t), Stamp(entry, "expire_at"), Stamp(entry, "next_reset_at")));
                }
            }

            if (buckets.Count > 0)
            {
                var remaining = buckets.Sum(b => b.Remaining);
                // One ring for the month's pool and any packs, as Qoder's
                // plan-plus-packs total is: they are spent from one balance,
                // soonest-lapsing first, so what is left is their sum.
                DateTimeOffset? resetsAt = null;
                foreach (var bucket in buckets)
                {
                    // A refill the reply states and that is still ahead. A
                    // monthly plan states none: its pool simply ends, which is
                    // the expiry, not a reset.
                    if (bucket.NextResetAt is { } next && next > now
                        && (resetsAt is null || next < resetsAt))
                        resetsAt = next;
                }

                windows.Add(new UsageWindow("stepfun.credits", UsageWindowKind.Monthly, null,
                    Math.Clamp((bucketTotal - remaining) / bucketTotal, 0, 1), 30 * 86_400,
                    resetsAt, false, null, remaining <= 0)
                {
                    Expiry = UsageExpiry.Soonest(
                        buckets.Where(b => b.ExpiresAt is { }).Select(b => (b.Remaining, b.ExpiresAt!.Value)), now),
                });
            }
            else
            {
                // No sizes, only fractions. The subscription's is the plan; a
                // pack's fraction of an unstated size cannot be added to it.
                var subscriptionLeft = Fraction(credit, "subscription_credit_left_rate");
                var topUpLeft = Fraction(credit, "topup_credit_left_rate");
                var stated = subscriptionLeft ?? topUpLeft;
                if (stated is { } left)
                {
                    windows.Add(new UsageWindow("stepfun.credits", UsageWindowKind.Monthly, null,
                        Math.Clamp(1 - left, 0, 1), 30 * 86_400, null, false, null, left <= 0));
                }
            }
        }

        if (windows.Count == 0)
        {
            // The session works and the account has no Step Plan on it. A
            // complete answer, not a fault — and not a ring at 100% either.
            // Storing it clears the previous reading from memory and disk.
            return ProviderReadResult.Ok(
                new ProviderUsage(ProviderId.StepFun, account.AccountId, Array.Empty<UsageWindow>(), now,
                    UsageState.Unavailable, null, null, null, UsageRoute.WebSession)
                { Unavailability = new Unavailability(UnavailabilityKind.NoCredits) });
        }

        return ProviderReadResult.Ok(new ProviderUsage(ProviderId.StepFun, account.AccountId, windows,
            now, UsageState.Live, null, null, null, UsageRoute.WebSession));
    }

    /// <summary>`subscription.name` from `GetStepPlanStatus`, when the reply succeeded.</summary>
    private static string? PlanName(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var status = Number(root, "status");
            if (status is not { } ok || ok != 1) return null;
            if (!root.TryGetProperty("subscription", out var subscription)
                || subscription.ValueKind != JsonValueKind.Object
                || !subscription.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String) return null;
            var trimmed = name.GetString()?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            return trimmed.Length > 40 ? trimmed[..40] : trimmed;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A window only when it states a reset: StepFun zeroes both fields
    /// for a window the plan does not have — which is "no window", not "spent".</summary>
    private static (double Remaining, DateTimeOffset ResetsAt)? Window(
        JsonElement root, string leftName, string resetName)
    {
        var resetsAt = Stamp(root, resetName);
        var remaining = Number(root, leftName);
        if (resetsAt is null || remaining is not { } value || !double.IsFinite(value)) return null;
        return (Math.Clamp(value, 0, 1), resetsAt.Value);
    }

    /// <summary>A stated fraction, 0…1. StepFun sends zero for "not on this plan"
    /// as well as for "none left", so a zero on its own is not taken as either.</summary>
    private static double? Fraction(JsonElement element, string name)
    {
        var value = Number(element, name);
        return value is { } v && double.IsFinite(v) && v > 0 ? Math.Min(v, 1) : null;
    }

    /// <summary>Numbers arrive as JSON numbers or as decimal strings — the bucket
    /// sizes are strings, the rates are not.</summary>
    private static double? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>A Unix stamp in seconds or milliseconds, as a number or a string.
    /// Zero is no date: StepFun writes "0" for every time it does not have.</summary>
    private static DateTimeOffset? Stamp(JsonElement element, string name)
    {
        var value = Number(element, name);
        if (value is not { } stamp || !double.IsFinite(stamp) || stamp <= 0) return null;
        return stamp > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)stamp)
            : DateTimeOffset.FromUnixTimeSeconds((long)stamp);
    }

    private sealed class ReadHttpException(ProviderReadHealth health, string detail) : Exception(detail)
    {
        public ProviderReadHealth Health { get; } = health;
        public string Detail { get; } = detail;
    }
}

/// <summary>
/// The cookie names the console's own requests carry. An allow list, as
/// Xiaomi's is: the session has published names, so nothing else the host set
/// is forwarded. A header built from an arbitrary string is a header injection
/// if a value carries a newline, so anything a header cannot carry is refused.
/// </summary>
public static class SessionCookie
{
    /// <summary>The session itself. The console answers nothing without it.</summary>
    private static readonly string[] Required = ["Oasis-Token"];

    /// <summary>Sent when present. `Oasis-Webid` has to agree with the device
    /// the token was issued to; `INGRESSCOOKIE` pins the load balancer.</summary>
    private static readonly string[] Optional = ["Oasis-Webid", "INGRESSCOOKIE"];

    public sealed class CookieException(string message) : Exception(message);

    /// <summary>A `Cookie:` header reduced to the names above. Tolerates a pasted
    /// `Cookie:` prefix and refuses control characters. The first of a
    /// host-only and a domain row wins, as in any cookie header.</summary>
    public static string Normalize(string input)
    {
        foreach (var ch in input)
        {
            if (ch < 32 || ch > 126)
                throw new CookieException("cookie carries characters a header cannot");
        }

        var header = input.Trim();
        if (header.Length >= 7 && header[..7].Equals("cookie:", StringComparison.OrdinalIgnoreCase))
            header = header[7..].Trim();
        if (header.Length == 0)
            throw new CookieException("cookie is empty");
        if (Encoding.UTF8.GetByteCount(header) > 32_768)
            throw new CookieException("cookie is too long");

        var wanted = Required.Concat(Optional).ToHashSet(StringComparer.Ordinal);
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in header.Split(';'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();
            if (!wanted.Contains(name)) continue;
            if (value.Length == 0 || value.Contains('"') || value.Contains('\\') || value.Contains(' '))
                throw new CookieException($"cookie value for {name} is not header-safe");
            if (!seen.Add(name)) continue;
            kept.Add($"{name}={value}");
        }

        if (Required.Any(name => !seen.Contains(name)))
            throw new CookieException("cookie is missing Oasis-Token");
        return string.Join("; ", kept);
    }

    /// <summary>
    /// The device id the console sends as `oasis-webid`, which must match the
    /// one the token was issued to: the `Oasis-Webid` cookie when the session
    /// carries it, otherwise the token's own `device_id` claim — the token is a
    /// JWT, or an `access...refresh` pair whose refresh half carries the claim.
    /// Read, not verified — it only has to be repeated back to the server that
    /// signed it.
    /// </summary>
    public static string? WebId(string header)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in header.Split(';'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            values[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
        }

        if (values.TryGetValue("Oasis-Webid", out var webid) && webid.Length > 0)
            return webid;
        if (!values.TryGetValue("Oasis-Token", out var token)) return null;
        foreach (var half in token.Split(new[] { "..." }, StringSplitOptions.None).Reverse())
        {
            if (DeviceIdInJwt(half) is { } id) return id;
        }

        return null;
    }

    private static string? DeviceIdInJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("device_id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } value
                ? value
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

using Pulse.Core.Accounts;

namespace Pulse.Core.Providers;

/// <summary>Static capabilities of a provider, ported from upstream Provider computed flags.</summary>
public sealed record ProviderCapabilities
{
    /// <summary>Provider is configured by pasting an API key.</summary>
    public required bool UsesApiKey { get; init; }

    /// <summary>Provider reads a browser session cookie (Ollama Cloud, Xiaomi MiMo).</summary>
    public required bool UsesSessionCookie { get; init; }

    /// <summary>Reports a spendable numeric balance (DeepSeek, Command Code).</summary>
    public required bool ReportsSpendableBalance { get; init; }

    /// <summary>Supports extra Pulse-managed accounts beyond the primary CLI/editor login.</summary>
    public required bool SupportsMultipleAccounts { get; init; }

    /// <summary>Local CLI transcripts exist for activity indication (Claude Code, Codex).</summary>
    public required bool KeepsLocalTranscripts { get; init; }

    public static ProviderCapabilities For(ProviderId id) => id switch
    {
        ProviderId.ClaudeCode => new() { UsesApiKey = false, UsesSessionCookie = false, ReportsSpendableBalance = false, SupportsMultipleAccounts = true, KeepsLocalTranscripts = true },
        ProviderId.Codex => new() { UsesApiKey = false, UsesSessionCookie = false, ReportsSpendableBalance = false, SupportsMultipleAccounts = true, KeepsLocalTranscripts = true },
        ProviderId.Grok => new() { UsesApiKey = false, UsesSessionCookie = false, ReportsSpendableBalance = false, SupportsMultipleAccounts = true, KeepsLocalTranscripts = false },
        ProviderId.GrokBot => new() { UsesApiKey = false, UsesSessionCookie = false, ReportsSpendableBalance = false, SupportsMultipleAccounts = true, KeepsLocalTranscripts = false },
        ProviderId.DeepSeek => new() { UsesApiKey = true, UsesSessionCookie = false, ReportsSpendableBalance = true, SupportsMultipleAccounts = false, KeepsLocalTranscripts = false },
        ProviderId.CommandCode => new() { UsesApiKey = true, UsesSessionCookie = false, ReportsSpendableBalance = true, SupportsMultipleAccounts = false, KeepsLocalTranscripts = false },
        ProviderId.KimiCode => new() { UsesApiKey = true, UsesSessionCookie = false, ReportsSpendableBalance = false, SupportsMultipleAccounts = false, KeepsLocalTranscripts = false },
        ProviderId.OpenCodeGo => new() { UsesApiKey = true, UsesSessionCookie = false, ReportsSpendableBalance = false, SupportsMultipleAccounts = false, KeepsLocalTranscripts = false },
        ProviderId.OllamaCloud => new() { UsesApiKey = false, UsesSessionCookie = true, ReportsSpendableBalance = false, SupportsMultipleAccounts = false, KeepsLocalTranscripts = false },
        ProviderId.XiaomiMiMo => new() { UsesApiKey = false, UsesSessionCookie = true, ReportsSpendableBalance = false, SupportsMultipleAccounts = false, KeepsLocalTranscripts = false },
        _ => new() { UsesApiKey = false, UsesSessionCookie = false, ReportsSpendableBalance = false, SupportsMultipleAccounts = false, KeepsLocalTranscripts = false },
    };
}

/// <summary>Contract health of a single read; adapters map transport failures into these.</summary>
public enum ProviderReadHealth
{
    Healthy,
    SchemaChanged,
    Unauthorized,
    CredentialExpired,
    ProviderUnavailable,
    HelperNotRunning,
    RateLimited,
    RouteRetired,
    UnsupportedPlatform,
}

/// <summary>Result of one provider read attempt.</summary>
public sealed record ProviderReadResult
{
    public required ProviderReadHealth Health { get; init; }
    public ProviderUsage? Usage { get; init; }
    public string? Detail { get; init; }

    public static ProviderReadResult Ok(ProviderUsage usage) => new() { Health = ProviderReadHealth.Healthy, Usage = usage };
    public static ProviderReadResult Failed(ProviderReadHealth health, string? detail = null) => new() { Health = health, Detail = detail };
}

/// <summary>Context handed to a provider for one read.</summary>
public sealed record ProviderReadContext
{
    public required DateTimeOffset Now { get; init; }
    public required IServiceProvider Services { get; init; }
}

/// <summary>
/// Plugin-style provider abstraction. Network logic lives in adapters, never in view models.
/// </summary>
public interface IUsageProvider
{
    ProviderId Id { get; }

    ProviderCapabilities Capabilities { get; }

    Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account,
        ProviderReadContext context,
        CancellationToken cancellationToken);
}

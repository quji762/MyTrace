namespace Pulse.Core.Accounts;

/// <summary>A monitored provider account: the primary CLI/editor login, or a Pulse-managed extra.</summary>
public sealed record MonitoredAccount
{
    public required Pulse.Core.Providers.ProviderId Provider { get; init; }

    /// <summary>Stable id; the primary account uses the provider name, mirroring upstream.</summary>
    public required string AccountId { get; init; }

    /// <summary>User-visible label.</summary>
    public string? Label { get; init; }

    /// <summary>True for the account Pulse borrows from an installed CLI/editor, where
    /// deleting it in Pulse must NOT delete the underlying client's credential.</summary>
    public bool IsBorrowed { get; init; } = true;

    public bool Enabled { get; init; } = true;

    /// <summary>Primary Claude and Codex accounts can refuse the fallback route.</summary>
    public Providers.RoutePin Pin { get; init; } = Providers.RoutePin.Automatic;
}

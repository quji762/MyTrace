using System.Text.Json;

namespace Pulse.Core.Ledger;

/// <summary>
/// Freebuff, which shares Codebuff's `manicode*` trees but persists no usage;
/// port of upstream FreebuffUsageReader.
///
/// A Freebuff chat is one whose `metadata.runState.sessionState.mainAgentState
/// .agentType` starts with `base2-free`; the Codebuff agent types (`base2`,
/// `base2-lite`, `base2-max`, `base2-plan`) are the other product. A chat that
/// carries authoritative usage is Codebuff's even when its agent type says
/// otherwise, so `CodebuffUsageReader` claims it and this reader never does.
///
/// **Nothing is emitted, and that is the whole reader.** The only figures
/// Freebuff leaves are estimates from message character counts — roughly four
/// characters to a token. An inferred count is not reported as if it were
/// measured, and a "0 tokens" from a store that persisted none is a fabricated
/// zero. So this reader answers with no records and says why.
/// </summary>
public static class FreebuffUsageReader
{
    /// <summary>The line a caller shows in place of a figure: no counters are persisted.</summary>
    public const string Limitation =
        "Freebuff stores no token counters; only character-based estimates exist, " +
        "which Pulse does not report as usage.";

    /// <summary>Whether a chat belongs to Freebuff by its agent type.</summary>
    public static bool IsFreebuff(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return false;
        if (!message.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object) return false;
        if (!metadata.TryGetProperty("runState", out var runState) || runState.ValueKind != JsonValueKind.Object) return false;
        if (!runState.TryGetProperty("sessionState", out var sessionState) || sessionState.ValueKind != JsonValueKind.Object) return false;
        if (!sessionState.TryGetProperty("mainAgentState", out var mainAgent) || mainAgent.ValueKind != JsonValueKind.Object) return false;
        if (!mainAgent.TryGetProperty("agentType", out var agentType) || agentType.ValueKind != JsonValueKind.String) return false;
        return agentType.GetString()!.ToLowerInvariant().StartsWith("base2-free", StringComparison.Ordinal);
    }

    /// <summary>Always empty: the store has no reported counters to hand over.</summary>
    public static IReadOnlyList<AgentUsageRecord> Records() => Array.Empty<AgentUsageRecord>();
}

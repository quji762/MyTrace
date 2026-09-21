namespace Pulse.Core.Ledger;

/// <summary>
/// Warp's synced account snapshot; port of upstream CapturedWarpReader.
///
/// **This reader returns no records, ever, and that is the point.** A Warp
/// snapshot carries a request count and money — `requestsUsed` and
/// `spendCents` — and **no tokens of any kind**. TokenTally is the boundary
/// here, and a request is not a token: fabricating one, or dividing the spend
/// by a price to arrive at a token count, would put a number on the page that
/// nobody measured.
///
/// The cache location is still named (`~/.config/tokscale/warp-cache` plus the
/// `UsageImports/warp` import folder), so the pane can show Warp as
/// recognised-but-without-token-counts rather than silently absent.
/// </summary>
public static class CapturedWarpReader
{
    /// <summary>Always empty. The snapshot is a real source; it simply has no
    /// tokens to contribute to a token ledger.</summary>
    public static IReadOnlyList<AgentUsageRecord> Records(string? userProfile = null) =>
        Array.Empty<AgentUsageRecord>();
}

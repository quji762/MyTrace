using Pulse.Core.Accounts;
using Pulse.Core.Providers;

namespace Pulse.Providers.Kiro;

/// <summary>
/// Kiro provider; port of upstream KiroUsageService.
/// Reads Kiro's subscription credits through Kiro CLI's native ACP process.
/// Kiro owns authentication and token refresh; Pulse does not read its tokens.
///
/// Each bounded credit pool is drawn as a monthly window. Window identity uses
/// Kiro's resource type (not array position), so a reordered response keeps
/// saved pins and alert state on the correct allowance.
/// </summary>
public sealed class KiroProvider : IUsageProvider
{
    private readonly Func<KiroAcpClient>? _clientFactory;

    public KiroProvider(Func<KiroAcpClient>? clientFactory = null)
    {
        _clientFactory = clientFactory;
    }

    public ProviderId Id => ProviderId.Kiro;

    public ProviderCapabilities Capabilities => ProviderCapabilities.For(ProviderId.Kiro);

    public async Task<ProviderReadResult> ReadAsync(
        MonitoredAccount account,
        ProviderReadContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _clientFactory?.Invoke() ?? new KiroAcpClient();
            var root = await client.UsageAsync(cancellationToken).ConfigureAwait(false);
            return ParseReply(root, account, context.Now);
        }
        catch (KiroAcpClient.AcpException ex)
        {
            var (health, detail) = KiroUsageParser.ClassifyFailure(ex);
            return ProviderReadResult.Failed(health, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "server error");
        }
    }

    /// <summary>Parse a decoded ACP reply into a read result. Public for contract tests.</summary>
    public static ProviderReadResult ParseReply(System.Text.Json.JsonElement root, MonitoredAccount account, DateTimeOffset now)
    {
        var result = KiroUsageParser.Decode(root);
        if (!result.Success)
        {
            var (health, detail) = KiroUsageParser.ClassifyMessage(result.Message);
            return ProviderReadResult.Failed(health, detail);
        }

        if (result.Data is not { } payload)
            return ProviderReadResult.Failed(ProviderReadHealth.SchemaChanged, "no limits reported");

        var windows = KiroUsageParser.Windows(payload);
        if (windows.Count == 0)
            return ProviderReadResult.Failed(ProviderReadHealth.SchemaChanged, "no limits reported");

        var usage = new ProviderUsage(
            Provider: ProviderId.Kiro,
            AccountId: account.AccountId,
            Windows: windows,
            ObservedAt: now,
            State: UsageState.Live,
            Plan: payload.PlanName,
            CreditBalance: null,
            CreditRemaining: null,
            Origin: UsageRoute.KiroAcp);
        return ProviderReadResult.Ok(usage);
    }
}

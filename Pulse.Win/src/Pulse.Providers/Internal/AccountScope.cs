using Pulse.Core.Accounts;

namespace Pulse.Providers.Internal;

/// <summary>
/// The primary account id is the provider name. Added accounts use a generated
/// slot and must not borrow the CLI's own login.
/// </summary>
internal static class AccountScope
{
    public static bool IsPrimary(MonitoredAccount account) =>
        string.Equals(account.AccountId, account.Provider.ToString(), StringComparison.Ordinal);
}

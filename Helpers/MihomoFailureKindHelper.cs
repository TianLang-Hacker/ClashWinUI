using ClashWinUI.Models;

namespace ClashWinUI.Helpers
{
    public static class MihomoFailureKindHelper
    {
        public static bool IsTunFailure(MihomoFailureKind kind)
        {
            return kind is MihomoFailureKind.TunDependency
                or MihomoFailureKind.TunPermission
                or MihomoFailureKind.TunAdapterMissing
                or MihomoFailureKind.TunRouteMissing
                or MihomoFailureKind.TunDnsUnmanaged
                or MihomoFailureKind.TunFirewallBlocked;
        }
    }
}

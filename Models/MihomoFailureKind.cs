namespace ClashWinUI.Models
{
    public enum MihomoFailureKind
    {
        None = 0,
        GeoData,
        PortBindConflict,
        TunDependency,
        TunPermission,
        TunAdapterMissing,
        TunRouteMissing,
        TunDnsUnmanaged,
        TunFirewallBlocked,
        Unknown,
    }
}
namespace ClashWinUI.Models
{
    public sealed class SystemProxyState
    {
        public required bool IsEnabled { get; init; }

        public required string ProxyServer { get; init; }

        public required string BypassList { get; init; }

        public static SystemProxyState Disabled(string? proxyServer = null, string? bypassList = null)
        {
            return new SystemProxyState
            {
                IsEnabled = false,
                ProxyServer = proxyServer ?? string.Empty,
                BypassList = bypassList ?? string.Empty,
            };
        }
    }
}

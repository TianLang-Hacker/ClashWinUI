namespace ClashWinUI.Models
{
    public sealed class MixinSettings
    {
        public int? MixedPort { get; set; }
        public int? HttpPort { get; set; }
        public int? SocksPort { get; set; }
        public int? RedirPort { get; set; }
        public int? TProxyPort { get; set; }
        public bool TunEnabled { get; set; }
        public bool AllowLan { get; set; }
        public string Mode { get; set; } = "rule";
        public string LogLevel { get; set; } = "info";
        public bool Ipv6Enabled { get; set; }
    }
}

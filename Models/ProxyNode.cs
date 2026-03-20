
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Linq;

namespace ClashWinUI.Models
{
    public enum ProxyDelayLevel
    {
        Unknown = 0,
        Low = 1,
        Medium = 2,
        High = 3,
    }

    public partial class ProxyNode : ObservableObject
    {
        [ObservableProperty]
        public partial string Name { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Type { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ControllerName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string TransportText { get; set; } = "TCP";

        [ObservableProperty]
        public partial bool SupportsUdp { get; set; }

        [ObservableProperty]
        public partial string Network { get; set; } = string.Empty;

        [ObservableProperty]
        public partial int? DelayMs { get; set; }

        [ObservableProperty]
        public partial bool IsTesting { get; set; }

        public string DelayText => DelayMs is > -1 and int delay ? $"{delay} ms" : "--";

        public string ProtocolDisplayText => string.IsNullOrWhiteSpace(Type)
            ? "PROXY"
            : Type.ToUpperInvariant();

        public string ProtocolMonogram => GetProtocolMonogram(Type);

        public string TransportBadgeText => GetTransportBadgeText(TransportText, Network);

        public string ConnectivityBadgeText => GetConnectivityBadgeText(Type, SupportsUdp, Network);

        public ProxyDelayLevel DelayLevel => DelayMs switch
        {
            null => ProxyDelayLevel.Unknown,
            <= 150 => ProxyDelayLevel.Low,
            <= 350 => ProxyDelayLevel.Medium,
            _ => ProxyDelayLevel.High,
        };

        partial void OnDelayMsChanged(int? value)
        {
            OnPropertyChanged(nameof(DelayText));
            OnPropertyChanged(nameof(DelayLevel));
        }

        partial void OnTypeChanged(string value)
        {
            NotifyPresentationPropertiesChanged();
        }

        partial void OnTransportTextChanged(string value)
        {
            OnPropertyChanged(nameof(TransportBadgeText));
        }

        partial void OnSupportsUdpChanged(bool value)
        {
            OnPropertyChanged(nameof(ConnectivityBadgeText));
        }

        partial void OnNetworkChanged(string value)
        {
            OnPropertyChanged(nameof(TransportBadgeText));
            OnPropertyChanged(nameof(ConnectivityBadgeText));
        }

        private void NotifyPresentationPropertiesChanged()
        {
            OnPropertyChanged(nameof(ProtocolDisplayText));
            OnPropertyChanged(nameof(ProtocolMonogram));
            OnPropertyChanged(nameof(ConnectivityBadgeText));
        }

        private static string GetTransportBadgeText(string transportText, string network)
        {
            string normalized = NormalizeBadgeToken(
                string.IsNullOrWhiteSpace(transportText) ? network : transportText);

            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            if (string.Equals(normalized, "TCP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "UDP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "TCP / UDP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "TCP/UDP", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return normalized;
        }

        private static string GetConnectivityBadgeText(string type, bool supportsUdp, string network)
        {
            if (string.Equals(type, "Direct", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "Reject", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "Pass", StringComparison.OrdinalIgnoreCase))
            {
                return "LOCAL";
            }

            if (string.Equals(type, "Selector", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "URLTest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "Fallback", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "LoadBalance", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "Relay", StringComparison.OrdinalIgnoreCase))
            {
                return "SELECT";
            }

            if (supportsUdp)
            {
                return "TCP/UDP";
            }

            if (!string.IsNullOrWhiteSpace(network)
                && network.Contains("udp", StringComparison.OrdinalIgnoreCase)
                && !network.Contains("tcp", StringComparison.OrdinalIgnoreCase))
            {
                return "UDP";
            }

            return "TCP";
        }

        private static string NormalizeBadgeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim();
            if (!trimmed.Contains('/'))
            {
                return trimmed.ToUpperInvariant();
            }

            string[] parts = trimmed
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.ToUpperInvariant())
                .ToArray();

            return parts.Length == 0 ? string.Empty : string.Join("/", parts);
        }

        private static string GetProtocolMonogram(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return "PX";
            }

            return type.Trim().ToLowerInvariant() switch
            {
                "vmess" => "VM",
                "vless" => "VL",
                "trojan" => "TR",
                "ss" => "SS",
                "shadowsocks" => "SS",
                "ssr" => "SR",
                "snell" => "SN",
                "tuic" => "TU",
                "hysteria" => "HY",
                "hysteria2" => "H2",
                "wireguard" => "WG",
                "selector" => "GP",
                "urltest" => "UT",
                "fallback" => "FB",
                "loadbalance" => "LB",
                "relay" => "RY",
                "direct" => "DI",
                "reject" => "RJ",
                "pass" => "PS",
                _ when type.Length >= 2 => type[..2].ToUpperInvariant(),
                _ => type.ToUpperInvariant(),
            };
        }
    }
}


using CommunityToolkit.Mvvm.ComponentModel;

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
        public partial int? DelayMs { get; set; }

        [ObservableProperty]
        public partial bool IsTesting { get; set; }

        public string DelayText => DelayMs is > -1 and int delay ? $"{delay} ms" : "--";

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
    }
}

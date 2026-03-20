using CommunityToolkit.Mvvm.ComponentModel;

namespace ClashWinUI.Models
{
    public partial class PortSettingsDraft : ObservableObject
    {
        [ObservableProperty]
        public partial string MixedPortInput { get; set; } = "0";

        [ObservableProperty]
        public partial string HttpPortInput { get; set; } = "0";

        [ObservableProperty]
        public partial string SocksPortInput { get; set; } = "0";

        [ObservableProperty]
        public partial string RedirPortInput { get; set; } = "0";

        [ObservableProperty]
        public partial string TProxyPortInput { get; set; } = "0";

        [ObservableProperty]
        public partial string StatusMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsBusy { get; set; }
    }
}

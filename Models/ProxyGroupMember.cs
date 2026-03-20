using CommunityToolkit.Mvvm.ComponentModel;

namespace ClashWinUI.Models
{
    public partial class ProxyGroupMember : ObservableObject
    {
        [ObservableProperty]
        public partial string GroupName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial ProxyNode Node { get; set; } = new();

        [ObservableProperty]
        public partial bool IsCurrent { get; set; }
    }
}

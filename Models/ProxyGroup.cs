using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System;

namespace ClashWinUI.Models
{
    public partial class ProxyGroup : ObservableObject
    {
        [ObservableProperty]
        public partial string Name { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Type { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ControllerName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string CurrentProxyName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsExpanded { get; set; } = false;

        public ObservableCollection<ProxyGroupMember> Members { get; } = new();

        public string CurrentProxyDisplayText => string.IsNullOrWhiteSpace(CurrentProxyName)
            ? "--"
            : CurrentProxyName;

        partial void OnCurrentProxyNameChanged(string value)
        {
            OnPropertyChanged(nameof(CurrentProxyDisplayText));
        }

        public void SetCurrentProxy(string? proxyName)
        {
            CurrentProxyName = proxyName?.Trim() ?? string.Empty;

            foreach (ProxyGroupMember member in Members)
            {
                member.IsCurrent = string.Equals(
                    member.Node.Name,
                    CurrentProxyName,
                    StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(member.Node.ControllerName)
                        && string.Equals(
                            member.Node.ControllerName,
                            CurrentProxyName,
                            StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}

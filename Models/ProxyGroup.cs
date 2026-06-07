using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Globalization;

namespace ClashWinUI.Models
{
    public partial class ProxyGroup : ObservableObject
    {
        public const int VisibleMembersBatchSize = 96;

        private int _visibleMemberLimit;

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

        public ObservableCollection<ProxyGroupMember> VisibleMembers { get; } = new();

        public string CurrentProxyDisplayText => string.IsNullOrWhiteSpace(CurrentProxyName)
            ? "--"
            : CurrentProxyName;

        public bool HasHiddenMembers => IsExpanded && VisibleMembers.Count < Members.Count;

        public string MembersProgressText => string.Create(
            CultureInfo.CurrentCulture,
            $"{VisibleMembers.Count.ToString("N0", CultureInfo.CurrentCulture)} / {Members.Count.ToString("N0", CultureInfo.CurrentCulture)}");

        partial void OnCurrentProxyNameChanged(string value)
        {
            OnPropertyChanged(nameof(CurrentProxyDisplayText));
        }

        partial void OnIsExpandedChanged(bool value)
        {
            _visibleMemberLimit = value ? VisibleMembersBatchSize : 0;
            RefreshVisibleMembers();
        }

        public void ShowMoreMembers()
        {
            if (!IsExpanded)
            {
                return;
            }

            int currentLimit = Math.Max(_visibleMemberLimit, VisibleMembers.Count);
            _visibleMemberLimit = Math.Min(Members.Count, currentLimit + VisibleMembersBatchSize);
            RefreshVisibleMembers();
        }

        public void RefreshVisibleMembers()
        {
            int desiredCount = IsExpanded
                ? Math.Min(Members.Count, Math.Max(_visibleMemberLimit, VisibleMembersBatchSize))
                : 0;

            while (VisibleMembers.Count > desiredCount)
            {
                VisibleMembers.RemoveAt(VisibleMembers.Count - 1);
            }

            for (int i = 0; i < desiredCount; i++)
            {
                ProxyGroupMember member = Members[i];
                if (i >= VisibleMembers.Count)
                {
                    VisibleMembers.Add(member);
                }
                else if (!ReferenceEquals(VisibleMembers[i], member))
                {
                    VisibleMembers[i] = member;
                }
            }

            OnPropertyChanged(nameof(HasHiddenMembers));
            OnPropertyChanged(nameof(MembersProgressText));
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

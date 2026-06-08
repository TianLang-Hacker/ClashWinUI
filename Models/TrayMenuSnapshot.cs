using System;
using System.Collections.Generic;

namespace ClashWinUI.Models
{
    public sealed class TrayMenuSnapshot
    {
        public static TrayMenuSnapshot Empty { get; } = new();

        public bool IsLoaded { get; init; }
        public string ActiveProfileId { get; init; } = string.Empty;
        public string Mode { get; init; } = "rule";
        public bool TunEnabled { get; init; }
        public bool ProxyGroupsLoading { get; init; }
        public bool ProxyGroupsUnavailable { get; init; }
        public string ProxyGroupsErrorMessage { get; init; } = string.Empty;
        public IReadOnlyList<TrayProfileMenuItem> Profiles { get; init; } = Array.Empty<TrayProfileMenuItem>();
        public IReadOnlyList<TrayProxyGroupMenuItem> ProxyGroups { get; init; } = Array.Empty<TrayProxyGroupMenuItem>();
    }

    public sealed class TrayProfileMenuItem
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }

    public sealed class TrayProxyGroupMenuItem
    {
        public string Name { get; init; } = string.Empty;
        public string ControllerName { get; init; } = string.Empty;
        public string CurrentProxyName { get; init; } = string.Empty;
        public bool HasMoreNodes { get; init; }
        public IReadOnlyList<TrayProxyNodeMenuItem> Nodes { get; init; } = Array.Empty<TrayProxyNodeMenuItem>();
    }

    public sealed class TrayProxyNodeMenuItem
    {
        public string Name { get; init; } = string.Empty;
        public string ControllerName { get; init; } = string.Empty;
        public bool IsCurrent { get; init; }
    }
}

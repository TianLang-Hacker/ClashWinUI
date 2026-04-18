using ClashWinUI.Models;
using System.Collections.Generic;
using System.Linq;

namespace ClashWinUI.Helpers
{
    public static class ModelSnapshotCloneHelper
    {
        public static IReadOnlyList<ProxyGroup> CloneProxyGroups(IEnumerable<ProxyGroup> groups)
        {
            return groups.Select(CloneProxyGroup).ToList();
        }

        public static ProxyGroup CloneProxyGroup(ProxyGroup source)
        {
            var clone = new ProxyGroup
            {
                Name = source.Name,
                Type = source.Type,
                ControllerName = source.ControllerName,
                CurrentProxyName = source.CurrentProxyName,
                IsExpanded = source.IsExpanded,
            };

            foreach (ProxyGroupMember member in source.Members)
            {
                clone.Members.Add(new ProxyGroupMember
                {
                    GroupName = member.GroupName,
                    IsCurrent = member.IsCurrent,
                    Node = new ProxyNode
                    {
                        Name = member.Node.Name,
                        Type = member.Node.Type,
                        ControllerName = member.Node.ControllerName,
                        TransportText = member.Node.TransportText,
                        SupportsUdp = member.Node.SupportsUdp,
                        Network = member.Node.Network,
                        DelayMs = member.Node.DelayMs,
                        IsTesting = false,
                    }
                });
            }

            return clone;
        }

        public static IReadOnlyList<RuntimeRuleItem> CloneRuntimeRuleItems(IEnumerable<RuntimeRuleItem> items)
        {
            return items.Select(CloneRuntimeRuleItem).ToList();
        }

        public static RuntimeRuleItem CloneRuntimeRuleItem(RuntimeRuleItem source)
        {
            return new RuntimeRuleItem
            {
                StableId = source.StableId,
                Index = source.Index,
                MatcherType = source.MatcherType,
                MatcherTypeDisplay = source.MatcherTypeDisplay,
                MatcherValue = source.MatcherValue,
                MatcherValueDisplay = source.MatcherValueDisplay,
                RawRuleText = source.RawRuleText,
                ActionKind = source.ActionKind,
                ActionKindDisplay = source.ActionKindDisplay,
                ActionTargetRaw = source.ActionTargetRaw,
                ActionTargetDisplay = source.ActionTargetDisplay,
                IsEnabled = source.IsEnabled,
                IsApplying = false,
                IsToggleSynchronizing = false,
            };
        }
    }
}

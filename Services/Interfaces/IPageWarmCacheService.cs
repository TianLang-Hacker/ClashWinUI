using ClashWinUI.Models;
using System.Collections.Generic;

namespace ClashWinUI.Services.Interfaces
{
    public interface IPageWarmCacheService
    {
        void Clear();
        void InvalidateProxyGroups(string runtimeFingerprint);
        void InvalidateRules(string runtimeFingerprint);
        void StoreProxyGroups(string runtimeFingerprint, IReadOnlyList<ProxyGroup> groups);
        void StoreRules(string runtimeFingerprint, IReadOnlyList<RuntimeRuleItem> rules);
        bool TryGetProxyGroups(string runtimeFingerprint, out IReadOnlyList<ProxyGroup> groups);
        bool TryGetRules(string runtimeFingerprint, out IReadOnlyList<RuntimeRuleItem> rules);
    }
}

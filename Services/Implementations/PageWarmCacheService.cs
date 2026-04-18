using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace ClashWinUI.Services.Implementations
{
    public sealed class PageWarmCacheService : IPageWarmCacheService
    {
        private const int MaxEntriesPerCache = 16;

        private readonly object _syncRoot = new();
        private readonly Dictionary<string, CacheEntry<IReadOnlyList<ProxyGroup>>> _proxyGroupsCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CacheEntry<IReadOnlyList<RuntimeRuleItem>>> _rulesCache = new(StringComparer.Ordinal);

        public bool TryGetProxyGroups(string runtimeFingerprint, out IReadOnlyList<ProxyGroup> groups)
        {
            lock (_syncRoot)
            {
                if (_proxyGroupsCache.TryGetValue(runtimeFingerprint, out CacheEntry<IReadOnlyList<ProxyGroup>>? entry))
                {
                    entry.LastAccessedAt = DateTimeOffset.UtcNow;
                    groups = ModelSnapshotCloneHelper.CloneProxyGroups(entry.Value);
                    return true;
                }
            }

            groups = Array.Empty<ProxyGroup>();
            return false;
        }

        public void StoreProxyGroups(string runtimeFingerprint, IReadOnlyList<ProxyGroup> groups)
        {
            if (string.IsNullOrWhiteSpace(runtimeFingerprint))
            {
                return;
            }

            lock (_syncRoot)
            {
                _proxyGroupsCache[runtimeFingerprint] = new CacheEntry<IReadOnlyList<ProxyGroup>>(ModelSnapshotCloneHelper.CloneProxyGroups(groups));
                TrimCache(_proxyGroupsCache);
            }
        }

        public void InvalidateProxyGroups(string runtimeFingerprint)
        {
            if (string.IsNullOrWhiteSpace(runtimeFingerprint))
            {
                return;
            }

            lock (_syncRoot)
            {
                _proxyGroupsCache.Remove(runtimeFingerprint);
            }
        }

        public bool TryGetRules(string runtimeFingerprint, out IReadOnlyList<RuntimeRuleItem> rules)
        {
            lock (_syncRoot)
            {
                if (_rulesCache.TryGetValue(runtimeFingerprint, out CacheEntry<IReadOnlyList<RuntimeRuleItem>>? entry))
                {
                    entry.LastAccessedAt = DateTimeOffset.UtcNow;
                    rules = ModelSnapshotCloneHelper.CloneRuntimeRuleItems(entry.Value);
                    return true;
                }
            }

            rules = Array.Empty<RuntimeRuleItem>();
            return false;
        }

        public void StoreRules(string runtimeFingerprint, IReadOnlyList<RuntimeRuleItem> rules)
        {
            if (string.IsNullOrWhiteSpace(runtimeFingerprint))
            {
                return;
            }

            lock (_syncRoot)
            {
                _rulesCache[runtimeFingerprint] = new CacheEntry<IReadOnlyList<RuntimeRuleItem>>(ModelSnapshotCloneHelper.CloneRuntimeRuleItems(rules));
                TrimCache(_rulesCache);
            }
        }

        public void InvalidateRules(string runtimeFingerprint)
        {
            if (string.IsNullOrWhiteSpace(runtimeFingerprint))
            {
                return;
            }

            lock (_syncRoot)
            {
                _rulesCache.Remove(runtimeFingerprint);
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _proxyGroupsCache.Clear();
                _rulesCache.Clear();
            }
        }

        private static void TrimCache<TValue>(Dictionary<string, CacheEntry<TValue>> cache)
        {
            while (cache.Count > MaxEntriesPerCache)
            {
                string? oldestKey = null;
                DateTimeOffset oldestAccess = DateTimeOffset.MaxValue;

                foreach ((string key, CacheEntry<TValue> entry) in cache)
                {
                    if (entry.LastAccessedAt < oldestAccess)
                    {
                        oldestAccess = entry.LastAccessedAt;
                        oldestKey = key;
                    }
                }

                if (oldestKey is null)
                {
                    break;
                }

                cache.Remove(oldestKey);
            }
        }

        private sealed class CacheEntry<TValue>
        {
            public CacheEntry(TValue value)
            {
                Value = value;
                LastAccessedAt = DateTimeOffset.UtcNow;
            }

            public DateTimeOffset LastAccessedAt { get; set; }

            public TValue Value { get; }
        }
    }
}

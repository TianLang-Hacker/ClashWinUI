using ClashWinUI.Common;
using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.ViewModels
{
    public partial class RulesViewModel : ObservableObject, IDisposable
    {
        private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(200);

        private readonly LocalizedStrings _localizedStrings;
        private readonly IProfileService _profileService;
        private readonly IConfigService _configService;
        private readonly IMihomoService _mihomoService;
        private readonly IGeoDataService _geoDataService;
        private readonly IProcessService _processService;
        private readonly ITunService _tunService;
        private readonly ISystemProxyService _systemProxyService;
        private readonly IPageWarmCacheService _pageWarmCacheService;
        private readonly SemaphoreSlim _applySemaphore = new(1, 1);
        private readonly CancellationTokenSource _disposeCancellation = new();

        private CancellationTokenSource? _loadCancellation;
        private CancellationTokenSource? _searchDebounceCancellation;
        private IReadOnlyList<RuntimeRuleItem> _allRules = Array.Empty<RuntimeRuleItem>();
        private ProfileItem? _activeProfile;
        private string _currentRuntimePath = string.Empty;
        private string _currentRuntimeFingerprint = string.Empty;
        private bool _isDisposed;
        private int _loadVersion;

        [ObservableProperty]
        public partial string Title { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; }

        [ObservableProperty]
        public partial string SearchKeyword { get; set; }

        [ObservableProperty]
        public partial bool IsLoading { get; set; }

        [ObservableProperty]
        public partial IReadOnlyList<RuntimeRuleItem> Rules { get; set; }

        public RulesViewModel(
            LocalizedStrings localizedStrings,
            IProfileService profileService,
            IConfigService configService,
            IMihomoService mihomoService,
            IGeoDataService geoDataService,
            IProcessService processService,
            ITunService tunService,
            ISystemProxyService systemProxyService,
            IPageWarmCacheService pageWarmCacheService)
        {
            _localizedStrings = localizedStrings;
            _profileService = profileService;
            _configService = configService;
            _mihomoService = mihomoService;
            _geoDataService = geoDataService;
            _processService = processService;
            _tunService = tunService;
            _systemProxyService = systemProxyService;
            _pageWarmCacheService = pageWarmCacheService;

            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["PageRules"];
            StatusMessage = string.Empty;
            SearchKeyword = string.Empty;
            Rules = Array.Empty<RuntimeRuleItem>();
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            CancelLoad();
            CancelSearchDebounce();
            _disposeCancellation.Cancel();
            _localizedStrings.PropertyChanged -= OnLocalizedStringsPropertyChanged;
            _allRules = Array.Empty<RuntimeRuleItem>();
            Rules = Array.Empty<RuntimeRuleItem>();
            _activeProfile = null;
            _currentRuntimePath = string.Empty;
            _currentRuntimeFingerprint = string.Empty;
            _applySemaphore.Dispose();
            _disposeCancellation.Dispose();
        }

        public Task InitializeAsync()
        {
            if (!TryBeginLoadSession(out int requestVersion, out CancellationToken cancellationToken))
            {
                return Task.CompletedTask;
            }

            Stopwatch immediateStopwatch = Stopwatch.StartNew();
            ApplyImmediateSnapshot();
            immediateStopwatch.Stop();
            PerformanceTraceHelper.LogElapsed(
                "rules init immediate",
                immediateStopwatch.Elapsed,
                TimeSpan.FromMilliseconds(16));

            _ = LoadRulesAsync(
                requestVersion,
                cancellationToken,
                showStatus: true,
                publishRawBeforeHydrate: true,
                reason: "init");
            return Task.CompletedTask;
        }

        public async Task<bool> SetRuleEnabledAsync(RuntimeRuleItem? rule, bool isEnabled)
        {
            if (rule is null)
            {
                return false;
            }

            if (_isDisposed || _disposeCancellation.IsCancellationRequested)
            {
                return false;
            }

            if (_activeProfile is null)
            {
                StatusMessage = _localizedStrings["RulesNoActiveProfile"];
                return false;
            }

            bool previousState = rule.IsEnabled;
            if (previousState == isEnabled)
            {
                return true;
            }

            await _applySemaphore.WaitAsync(_disposeCancellation.Token);
            rule.IsApplying = true;

            try
            {
                CancellationToken cancellationToken = _disposeCancellation.Token;
                _configService.SetRuleEnabled(_activeProfile, rule.StableId, isEnabled);
                string runtimePath = _configService.BuildRuntime(_activeProfile);
                bool applied = await _mihomoService.ApplyConfigAsync(runtimePath, cancellationToken);
                if (_isDisposed || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (!applied)
                {
                    if (PathsEqual(_processService.CurrentConfigPath, runtimePath))
                    {
                        await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                            _systemProxyService,
                            _processService,
                            _tunService,
                            runtimePath,
                            cancellationToken);
                    }

                    RollbackRuleOverride(rule.StableId, previousState);
                    StatusMessage = MihomoFailureTextHelper.TryBuildControllerFailureMessage(
                        _localizedStrings,
                        _processService,
                        _geoDataService,
                        _tunService,
                        runtimePath,
                        out string controllerMessage)
                        ? controllerMessage
                        : _localizedStrings["RulesStatusApplyFailed"];
                    return false;
                }

                rule.IsEnabled = isEnabled;
                await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                    _systemProxyService,
                    _processService,
                    _tunService,
                    runtimePath,
                    cancellationToken);
                if (_isDisposed || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (!TryBeginLoadSession(out int requestVersion, out CancellationToken refreshCancellation))
                {
                    return false;
                }

                await LoadRulesAsync(
                    requestVersion,
                    refreshCancellation,
                    showStatus: false,
                    publishRawBeforeHydrate: false,
                    reason: "toggle");
                if (_isDisposed || refreshCancellation.IsCancellationRequested)
                {
                    return false;
                }

                StatusMessage = string.Format(_localizedStrings["RulesStatusApplied"], rule.MatcherValueDisplay);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                RollbackRuleOverride(rule.StableId, previousState);
                StatusMessage = string.Format(_localizedStrings["RulesStatusLoadFailed"], ex.Message);
                return false;
            }
            finally
            {
                rule.IsApplying = false;
                _applySemaphore.Release();
            }
        }

        partial void OnSearchKeywordChanged(string value)
        {
            if (_isDisposed)
            {
                return;
            }

            CancelSearchDebounce();
            _searchDebounceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_disposeCancellation.Token);
            CancellationToken cancellationToken = _searchDebounceCancellation.Token;

            _ = ApplyFiltersDebouncedAsync(cancellationToken);
        }

        private void ApplyImmediateSnapshot()
        {
            _activeProfile = _profileService.GetActiveProfile();
            _currentRuntimePath = string.Empty;
            _currentRuntimeFingerprint = string.Empty;

            if (_activeProfile is null)
            {
                _allRules = Array.Empty<RuntimeRuleItem>();
                Rules = Array.Empty<RuntimeRuleItem>();
                StatusMessage = _localizedStrings["RulesNoActiveProfile"];
                return;
            }

            string runtimePath = _configService.GetWorkspace(_activeProfile).RuntimePath;
            if (FileFingerprintHelper.TryGetFingerprint(runtimePath, out string runtimeFingerprint)
                && _pageWarmCacheService.TryGetRules(runtimeFingerprint, out IReadOnlyList<RuntimeRuleItem> cachedRules))
            {
                _currentRuntimePath = runtimePath;
                _currentRuntimeFingerprint = runtimeFingerprint;
                _allRules = cachedRules;
                PublishRulesFiltered();
                StatusMessage = cachedRules.Count == 0
                    ? _localizedStrings["RulesStatusEmpty"]
                    : string.Format(_localizedStrings["RulesStatusLoaded"], cachedRules.Count);
                return;
            }

            _allRules = Array.Empty<RuntimeRuleItem>();
            Rules = Array.Empty<RuntimeRuleItem>();
            StatusMessage = string.Empty;
        }

        private bool TryBeginLoadSession(out int requestVersion, out CancellationToken cancellationToken)
        {
            requestVersion = 0;
            cancellationToken = CancellationToken.None;
            if (_isDisposed || _disposeCancellation.IsCancellationRequested)
            {
                return false;
            }

            CancelLoad();
            _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_disposeCancellation.Token);
            requestVersion = Interlocked.Increment(ref _loadVersion);
            cancellationToken = _loadCancellation.Token;
            return true;
        }

        private void CancelLoad()
        {
            if (_loadCancellation is null)
            {
                return;
            }

            try
            {
                _loadCancellation.Cancel();
            }
            catch
            {
                // Ignore disposal races.
            }
            finally
            {
                _loadCancellation.Dispose();
                _loadCancellation = null;
            }
        }

        private void CancelSearchDebounce()
        {
            if (_searchDebounceCancellation is null)
            {
                return;
            }

            try
            {
                _searchDebounceCancellation.Cancel();
            }
            catch
            {
                // Ignore disposal races.
            }
            finally
            {
                _searchDebounceCancellation.Dispose();
                _searchDebounceCancellation = null;
            }
        }

        private async Task LoadRulesAsync(
            int requestVersion,
            CancellationToken cancellationToken,
            bool showStatus,
            bool publishRawBeforeHydrate,
            string reason)
        {
            if (!IsCurrentRequest(requestVersion, cancellationToken))
            {
                return;
            }

            IsLoading = true;
            Stopwatch totalStopwatch = Stopwatch.StartNew();

            try
            {
                await Task.Yield();
                if (!IsCurrentRequest(requestVersion, cancellationToken))
                {
                    return;
                }

                _activeProfile = _profileService.GetActiveProfile();
                if (_activeProfile is null)
                {
                    _allRules = Array.Empty<RuntimeRuleItem>();
                    Rules = Array.Empty<RuntimeRuleItem>();
                    _currentRuntimePath = string.Empty;
                    _currentRuntimeFingerprint = string.Empty;
                    StatusMessage = _localizedStrings["RulesNoActiveProfile"];
                    return;
                }

                Stopwatch baseLoadStopwatch = Stopwatch.StartNew();
                RuleLoadContext context = await Task.Run(() => LoadRulesBase(_activeProfile, cancellationToken), cancellationToken);
                baseLoadStopwatch.Stop();
                PerformanceTraceHelper.LogElapsed(
                    $"rules init base ({reason})",
                    baseLoadStopwatch.Elapsed,
                    TimeSpan.FromMilliseconds(120));

                if (!IsCurrentRequest(requestVersion, cancellationToken))
                {
                    return;
                }

                _currentRuntimePath = context.RuntimePath;
                _currentRuntimeFingerprint = context.RuntimeFingerprint;
                _allRules = context.Items;

                if (publishRawBeforeHydrate)
                {
                    PublishRulesFiltered();
                    UpdateLoadedStatus(showStatus);
                }

                Stopwatch hydrateStopwatch = Stopwatch.StartNew();
                await HydrateProxyTargetsAsync(context, cancellationToken);
                hydrateStopwatch.Stop();
                PerformanceTraceHelper.LogElapsed(
                    $"rules init hydrate ({reason})",
                    hydrateStopwatch.Elapsed,
                    TimeSpan.FromMilliseconds(120));

                if (!IsCurrentRequest(requestVersion, cancellationToken))
                {
                    return;
                }

                PublishRulesFiltered();
                UpdateLoadedStatus(showStatus);
                if (!string.IsNullOrWhiteSpace(context.RuntimeFingerprint))
                {
                    _pageWarmCacheService.StoreRules(context.RuntimeFingerprint, _allRules);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the page leaves or a newer load starts.
            }
            catch (Exception ex)
            {
                if (IsCurrentRequest(requestVersion, cancellationToken))
                {
                    _allRules = Array.Empty<RuntimeRuleItem>();
                    Rules = Array.Empty<RuntimeRuleItem>();
                    if (showStatus)
                    {
                        StatusMessage = string.Format(_localizedStrings["RulesStatusLoadFailed"], ex.Message);
                    }
                }
            }
            finally
            {
                totalStopwatch.Stop();
                PerformanceTraceHelper.LogElapsed(
                    $"rules init total ({reason})",
                    totalStopwatch.Elapsed,
                    TimeSpan.FromMilliseconds(120));

                if (!_isDisposed && requestVersion == _loadVersion)
                {
                    IsLoading = false;
                }
            }
        }

        private RuleLoadContext LoadRulesBase(ProfileItem profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<RuntimeRuleItem> items = _configService.GetRuntimeRules(profile);
            cancellationToken.ThrowIfCancellationRequested();

            string runtimePath = _configService.GetWorkspace(profile).RuntimePath;
            string runtimeFingerprint = FileFingerprintHelper.GetFingerprintOrMissing(runtimePath);
            return new RuleLoadContext(profile, runtimePath, runtimeFingerprint, items);
        }

        private async Task HydrateProxyTargetsAsync(RuleLoadContext context, CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, ProxyGroup> groupLookup = await BuildProxyGroupLookupAsync(
                context.RuntimePath,
                context.RuntimeFingerprint,
                cancellationToken);

            foreach (RuntimeRuleItem item in context.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item.ActionKind != RuleActionKind.Proxy)
                {
                    item.ActionTargetDisplay = string.Empty;
                    continue;
                }

                if (!groupLookup.TryGetValue(item.ActionTargetRaw, out ProxyGroup? group))
                {
                    item.ActionTargetDisplay = item.ActionTargetRaw;
                    continue;
                }

                item.ActionTargetDisplay = string.IsNullOrWhiteSpace(group.CurrentProxyName)
                    ? group.Name
                    : $"{group.Name} -> {group.CurrentProxyDisplayText}";
            }
        }

        private async Task<IReadOnlyDictionary<string, ProxyGroup>> BuildProxyGroupLookupAsync(
            string runtimePath,
            string runtimeFingerprint,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ProxyGroup> groups;
            if (!string.IsNullOrWhiteSpace(runtimeFingerprint)
                && _pageWarmCacheService.TryGetProxyGroups(runtimeFingerprint, out IReadOnlyList<ProxyGroup> cachedGroups))
            {
                groups = cachedGroups;
            }
            else
            {
                ProxyGroupLoadResult proxyLoadResult = await _mihomoService.GetProxyGroupsAsync(runtimePath, cancellationToken);
                groups = proxyLoadResult.Groups;
            }

            var groupLookup = new Dictionary<string, ProxyGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (ProxyGroup group in groups)
            {
                if (!string.IsNullOrWhiteSpace(group.Name))
                {
                    groupLookup[group.Name] = group;
                }

                if (!string.IsNullOrWhiteSpace(group.ControllerName))
                {
                    groupLookup[group.ControllerName] = group;
                }
            }

            return groupLookup;
        }

        private async Task ApplyFiltersDebouncedAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(SearchDebounceDelay, cancellationToken);
                if (_isDisposed || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                PublishRulesFiltered();
            }
            catch (OperationCanceledException)
            {
                // Expected when the user keeps typing or the page leaves.
            }
        }

        private void PublishRulesFiltered()
        {
            string[] keywords = (SearchKeyword ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            IEnumerable<RuntimeRuleItem> filtered = _allRules;
            if (keywords.Length > 0)
            {
                filtered = filtered.Where(rule =>
                    keywords.Any(keyword => rule.SearchText.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
            }

            Rules = filtered.ToList();
        }

        private void UpdateLoadedStatus(bool showStatus)
        {
            if (!showStatus)
            {
                return;
            }

            StatusMessage = _allRules.Count == 0
                ? _localizedStrings["RulesStatusEmpty"]
                : string.Format(_localizedStrings["RulesStatusLoaded"], _allRules.Count);
        }

        private void RollbackRuleOverride(string stableId, bool previousState)
        {
            if (_activeProfile is null)
            {
                return;
            }

            try
            {
                _configService.SetRuleEnabled(_activeProfile, stableId, previousState);
                _configService.BuildRuntime(_activeProfile);
            }
            catch
            {
                // Best effort rollback to keep the workspace close to the last known good runtime.
            }
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isDisposed)
            {
                return;
            }

            if (e.PropertyName != nameof(LocalizedStrings.CurrentLanguage) && e.PropertyName != "Item[]")
            {
                return;
            }

            Title = _localizedStrings["PageRules"];
            if (_activeProfile is null)
            {
                StatusMessage = _localizedStrings["RulesNoActiveProfile"];
            }
            else if (_allRules.Count == 0)
            {
                StatusMessage = _localizedStrings["RulesStatusEmpty"];
            }
            else if (!string.IsNullOrWhiteSpace(StatusMessage))
            {
                StatusMessage = string.Format(_localizedStrings["RulesStatusLoaded"], _allRules.Count);
            }
        }

        private bool IsCurrentRequest(int requestVersion, CancellationToken cancellationToken)
        {
            return !_isDisposed
                && !cancellationToken.IsCancellationRequested
                && requestVersion == _loadVersion;
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(
                System.IO.Path.GetFullPath(left.Trim()),
                System.IO.Path.GetFullPath(right.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }

        private sealed class RuleLoadContext
        {
            public RuleLoadContext(ProfileItem profile, string runtimePath, string runtimeFingerprint, IReadOnlyList<RuntimeRuleItem> items)
            {
                Profile = profile;
                RuntimePath = runtimePath;
                RuntimeFingerprint = runtimeFingerprint;
                Items = items;
            }

            public ProfileItem Profile { get; }

            public string RuntimePath { get; }

            public string RuntimeFingerprint { get; }

            public IReadOnlyList<RuntimeRuleItem> Items { get; }
        }
    }
}

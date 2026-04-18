using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.ViewModels
{
    public partial class ProxiesViewModel : ObservableObject, IDisposable
    {
        private static readonly HashSet<string> NonTestableNodeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "REJECT",
            "REJECT-DROP",
            "DIRECT",
            "PASS",
        };

        private static readonly HashSet<string> NonTestableNodeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Reject",
            "Direct",
            "Pass",
        };

        private const int TestAllConcurrencyLimit = 8;
        private const int ControllerWarmupAttempts = 3;
        private static readonly TimeSpan ControllerWarmupDelay = TimeSpan.FromMilliseconds(250);

        private readonly LocalizedStrings _localizedStrings;
        private readonly IProfileService _profileService;
        private readonly IConfigService _configService;
        private readonly IMihomoService _mihomoService;
        private readonly IGeoDataService _geoDataService;
        private readonly IProcessService _processService;
        private readonly ITunService _tunService;
        private readonly ISystemProxyService _systemProxyService;
        private readonly IAppSettingsService _appSettingsService;
        private readonly IPageWarmCacheService _pageWarmCacheService;
        private readonly DispatcherQueue? _dispatcherQueue;
        private readonly CancellationTokenSource _disposeCancellation = new();

        private CancellationTokenSource? _loadCancellation;
        private string _currentRuntimePath = string.Empty;
        private string _currentRuntimeFingerprint = string.Empty;
        private bool _isWatchingRuntimeChanges;
        private bool _isDisposed;
        private int _loadVersion;

        [ObservableProperty]
        public partial string Title { get; set; }

        [ObservableProperty]
        public partial string TestUrl { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; }

        [ObservableProperty]
        public partial ProfileItem? ActiveProfile { get; set; }

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        [ObservableProperty]
        public partial IReadOnlyList<ProxyGroup> ProxyGroups { get; set; }

        public ProxiesViewModel(
            LocalizedStrings localizedStrings,
            IProfileService profileService,
            IConfigService configService,
            IMihomoService mihomoService,
            IGeoDataService geoDataService,
            IProcessService processService,
            ITunService tunService,
            ISystemProxyService systemProxyService,
            IAppSettingsService appSettingsService,
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
            _appSettingsService = appSettingsService;
            _pageWarmCacheService = pageWarmCacheService;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["PageProxies"];
            TestUrl = "https://www.gstatic.com/generate_204";
            StatusMessage = string.Empty;
            ProxyGroups = Array.Empty<ProxyGroup>();
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            StopWatchingRuntimeChanges();
            CancelLoad();
            _disposeCancellation.Cancel();
            _disposeCancellation.Dispose();
            _localizedStrings.PropertyChanged -= OnLocalizedStringsPropertyChanged;
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
                "proxies init immediate",
                immediateStopwatch.Elapsed,
                TimeSpan.FromMilliseconds(16));

            _ = RefreshGroupsAsync(requestVersion, cancellationToken, reapplyRuntime: false, reason: "init");
            return Task.CompletedTask;
        }

        public void StartWatchingRuntimeChanges()
        {
            if (_isWatchingRuntimeChanges)
            {
                return;
            }

            _mihomoService.ConfigApplied += OnMihomoConfigApplied;
            _isWatchingRuntimeChanges = true;
        }

        public void StopWatchingRuntimeChanges()
        {
            if (!_isWatchingRuntimeChanges)
            {
                return;
            }

            _mihomoService.ConfigApplied -= OnMihomoConfigApplied;
            _isWatchingRuntimeChanges = false;
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            if (!TryBeginLoadSession(out int requestVersion, out CancellationToken cancellationToken))
            {
                return;
            }

            await RefreshGroupsAsync(requestVersion, cancellationToken, reapplyRuntime: true, reason: "manual");
        }

        [RelayCommand]
        private async Task TestNodeAsync(ProxyGroupMember? member)
        {
            if (member?.Node is null || IsBusy)
            {
                return;
            }

            if (!IsDelayTestable(member.Node))
            {
                StatusMessage = string.Format(_localizedStrings["ProxiesStatusDelaySkippedNonTestable"], member.Node.Name);
                return;
            }

            IsBusy = true;
            try
            {
                member.Node.IsTesting = true;
                int? delay = await _mihomoService.TestProxyDelayAsync(member.Node.Name, TestUrl, cancellationToken: _disposeCancellation.Token);
                if (delay is null)
                {
                    StatusMessage = _localizedStrings["ProxiesStatusDelayFailed"];
                    return;
                }

                member.Node.DelayMs = delay;
                StatusMessage = string.Format(_localizedStrings["ProxiesStatusDelaySuccess"], member.Node.Name, delay.Value);
                StoreCurrentProxySnapshot();
            }
            catch (OperationCanceledException)
            {
                // Ignore when the page leaves while delay tests are in-flight.
            }
            finally
            {
                member.Node.IsTesting = false;
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task TestAllAsync()
        {
            IReadOnlyList<ProxyNode> nodes = GetUniqueNodes();
            if (nodes.Count == 0 || IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                int skippedCount = await TestNodesAsync(nodes, _disposeCancellation.Token);
                StatusMessage = skippedCount > 0
                    ? string.Format(_localizedStrings["ProxiesStatusDelayAllFinishedWithSkipped"], skippedCount)
                    : _localizedStrings["ProxiesStatusDelayAllFinished"];
                StoreCurrentProxySnapshot();
            }
            catch (OperationCanceledException)
            {
                // Ignore when the page leaves while tests are running.
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task TestGroupAsync(ProxyGroup? group)
        {
            if (group is null || IsBusy)
            {
                return;
            }

            IReadOnlyList<ProxyNode> nodes = group.Members
                .Select(member => member.Node)
                .DistinctBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (nodes.Count == 0)
            {
                return;
            }

            IsBusy = true;
            try
            {
                int skippedCount = await TestNodesAsync(nodes, _disposeCancellation.Token);
                StatusMessage = skippedCount > 0
                    ? string.Format(_localizedStrings["ProxiesStatusDelayGroupFinishedWithSkipped"], group.Name, skippedCount)
                    : string.Format(_localizedStrings["ProxiesStatusDelayGroupFinished"], group.Name);
                StoreCurrentProxySnapshot();
            }
            catch (OperationCanceledException)
            {
                // Ignore when the page leaves while tests are running.
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task SelectProxyAsync(ProxyGroupMember? member)
        {
            if (member?.Node is null || IsBusy)
            {
                return;
            }

            ProxyGroup? group = ProxyGroups.FirstOrDefault(item =>
                string.Equals(item.Name, member.GroupName, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                return;
            }

            if (string.Equals(group.CurrentProxyName, member.Node.Name, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = string.Format(_localizedStrings["ProxiesStatusProxyAlreadySelected"], group.Name, member.Node.Name);
                return;
            }

            IsBusy = true;
            try
            {
                string groupName = string.IsNullOrWhiteSpace(group.ControllerName)
                    ? group.Name
                    : group.ControllerName;
                string proxyName = string.IsNullOrWhiteSpace(member.Node.ControllerName)
                    ? member.Node.Name
                    : member.Node.ControllerName;

                bool selected = await _mihomoService.SelectProxyAsync(groupName, proxyName, _disposeCancellation.Token);
                if (!selected)
                {
                    StatusMessage = string.Format(_localizedStrings["ProxiesStatusProxySelectFailed"], group.Name, member.Node.Name);
                    return;
                }

                group.SetCurrentProxy(member.Node.Name);
                StatusMessage = string.Format(_localizedStrings["ProxiesStatusProxySelected"], group.Name, member.Node.Name);
                StoreCurrentProxySnapshot();
                InvalidateRulesSnapshot();
            }
            catch (OperationCanceledException)
            {
                // Ignore when the page leaves while selection is in-flight.
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ApplyImmediateSnapshot()
        {
            ActiveProfile = _profileService.GetActiveProfile();
            _currentRuntimePath = string.Empty;
            _currentRuntimeFingerprint = string.Empty;

            if (ActiveProfile is null)
            {
                ProxyGroups = Array.Empty<ProxyGroup>();
                StatusMessage = _localizedStrings["ProxiesStatusNoActiveProfile"];
                return;
            }

            string runtimePath = _configService.GetWorkspace(ActiveProfile).RuntimePath;
            if (FileFingerprintHelper.TryGetFingerprint(runtimePath, out string runtimeFingerprint)
                && _pageWarmCacheService.TryGetProxyGroups(runtimeFingerprint, out IReadOnlyList<ProxyGroup> cachedGroups))
            {
                _currentRuntimePath = runtimePath;
                _currentRuntimeFingerprint = runtimeFingerprint;
                ProxyGroups = cachedGroups;

                int nodeCount = CountUniqueNodes(cachedGroups);
                StatusMessage = nodeCount == 0
                    ? string.Empty
                    : string.Format(_localizedStrings["ProxiesStatusLoadedNoApply"], nodeCount);
                return;
            }

            ProxyGroups = Array.Empty<ProxyGroup>();
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

        private async Task RefreshGroupsAsync(int requestVersion, CancellationToken cancellationToken, bool reapplyRuntime, string reason)
        {
            if (!IsCurrentRequest(requestVersion, cancellationToken))
            {
                return;
            }

            IsBusy = true;
            Stopwatch totalStopwatch = Stopwatch.StartNew();

            try
            {
                await Task.Yield();
                if (!IsCurrentRequest(requestVersion, cancellationToken))
                {
                    return;
                }

                ProfileItem? activeProfile = ActiveProfile ?? _profileService.GetActiveProfile();
                ActiveProfile = activeProfile;
                if (activeProfile is null)
                {
                    ProxyGroups = Array.Empty<ProxyGroup>();
                    _currentRuntimePath = string.Empty;
                    _currentRuntimeFingerprint = string.Empty;
                    StatusMessage = _localizedStrings["ProxiesStatusNoActiveProfile"];
                    return;
                }

                Stopwatch resolveStopwatch = Stopwatch.StartNew();
                ProxyLoadContext context = await Task.Run(() => ResolveLoadContext(activeProfile, cancellationToken), cancellationToken);
                resolveStopwatch.Stop();
                PerformanceTraceHelper.LogElapsed(
                    $"proxies init resolve ({reason})",
                    resolveStopwatch.Elapsed,
                    TimeSpan.FromMilliseconds(120));

                if (!IsCurrentRequest(requestVersion, cancellationToken))
                {
                    return;
                }

                ProxyRefreshResult refreshResult = await LoadLatestGroupsAsync(context, reapplyRuntime, cancellationToken);
                if (!IsCurrentRequest(requestVersion, cancellationToken))
                {
                    return;
                }

                Stopwatch applyStopwatch = Stopwatch.StartNew();
                ApplyLoadedGroups(refreshResult);
                applyStopwatch.Stop();
                PerformanceTraceHelper.LogElapsed(
                    $"proxies init apply ({reason})",
                    applyStopwatch.Elapsed,
                    TimeSpan.FromMilliseconds(16));
            }
            catch (OperationCanceledException)
            {
                // Expected when a newer load supersedes the current one or the page leaves.
            }
            catch (Exception ex)
            {
                if (IsCurrentRequest(requestVersion, cancellationToken))
                {
                    StatusMessage = ex.Message;
                }
            }
            finally
            {
                totalStopwatch.Stop();
                PerformanceTraceHelper.LogElapsed(
                    $"proxies init total ({reason})",
                    totalStopwatch.Elapsed,
                    TimeSpan.FromMilliseconds(120));

                if (!_isDisposed && requestVersion == _loadVersion)
                {
                    IsBusy = false;
                }
            }
        }

        private ProxyLoadContext ResolveLoadContext(ProfileItem profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string runtimePath = _configService.GetRuntimePath(profile);
            cancellationToken.ThrowIfCancellationRequested();
            _ = FileFingerprintHelper.TryGetFingerprint(runtimePath, out string runtimeFingerprint);
            ProfileCompatibilityStatus compatibility = ProfileCompatibilityChecker.Check(profile.FilePath);

            return new ProxyLoadContext(
                profile,
                runtimePath,
                runtimeFingerprint,
                compatibility == ProfileCompatibilityStatus.Base64NotYaml);
        }

        private async Task<ProxyRefreshResult> LoadLatestGroupsAsync(ProxyLoadContext context, bool reapplyRuntime, CancellationToken cancellationToken)
        {
            bool attemptedApply = reapplyRuntime;
            bool applied = false;
            if (reapplyRuntime)
            {
                applied = await ApplyRuntimeAsync(context.RuntimePath, cancellationToken);
            }

            ProxyGroupLoadResult loadResult = await LoadProxyGroupsWithWarmupAsync(
                context.RuntimePath,
                allowWarmup: reapplyRuntime,
                cancellationToken);

            if (!reapplyRuntime && ShouldRetryWithApply(context.RuntimePath, loadResult))
            {
                attemptedApply = true;
                applied = await ApplyRuntimeAsync(context.RuntimePath, cancellationToken);
                if (applied || PathsEqual(_processService.CurrentConfigPath, context.RuntimePath))
                {
                    loadResult = await LoadProxyGroupsWithWarmupAsync(
                        context.RuntimePath,
                        allowWarmup: true,
                        cancellationToken);
                }
            }

            return new ProxyRefreshResult(context, loadResult, attemptedApply, applied);
        }

        private async Task<bool> ApplyRuntimeAsync(string runtimePath, CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool applied = false;

            try
            {
                applied = await _mihomoService.ApplyConfigAsync(runtimePath, cancellationToken);
                if (applied || PathsEqual(_processService.CurrentConfigPath, runtimePath))
                {
                    await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                        _systemProxyService,
                        _processService,
                        _tunService,
                        runtimePath,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                applied = false;
            }
            finally
            {
                stopwatch.Stop();
                PerformanceTraceHelper.LogElapsed(
                    "proxies runtime apply",
                    stopwatch.Elapsed,
                    TimeSpan.FromMilliseconds(120));
            }

            return applied;
        }

        private async Task<ProxyGroupLoadResult> LoadProxyGroupsWithWarmupAsync(string runtimePath, bool allowWarmup, CancellationToken cancellationToken)
        {
            Stopwatch fetchStopwatch = Stopwatch.StartNew();
            ProxyGroupLoadResult bestResult = await _mihomoService.GetProxyGroupsAsync(runtimePath, cancellationToken);
            fetchStopwatch.Stop();
            PerformanceTraceHelper.LogElapsed(
                "proxies controller fetch",
                fetchStopwatch.Elapsed,
                TimeSpan.FromMilliseconds(120));

            if (!allowWarmup || IsControllerReady(bestResult))
            {
                return bestResult;
            }

            Stopwatch warmupStopwatch = Stopwatch.StartNew();
            for (int attempt = 1; attempt < ControllerWarmupAttempts; attempt++)
            {
                await Task.Delay(ControllerWarmupDelay, cancellationToken);

                ProxyGroupLoadResult currentResult = await _mihomoService.GetProxyGroupsAsync(runtimePath, cancellationToken);
                if (currentResult.Groups.Count > 0)
                {
                    bestResult = currentResult;
                }

                if (IsControllerReady(currentResult))
                {
                    warmupStopwatch.Stop();
                    PerformanceTraceHelper.LogElapsed(
                        "proxies controller warmup",
                        warmupStopwatch.Elapsed,
                        TimeSpan.FromMilliseconds(120));
                    return currentResult;
                }
            }

            warmupStopwatch.Stop();
            PerformanceTraceHelper.LogElapsed(
                "proxies controller warmup",
                warmupStopwatch.Elapsed,
                TimeSpan.FromMilliseconds(120));
            return bestResult;
        }

        private void ApplyLoadedGroups(ProxyRefreshResult refreshResult)
        {
            _currentRuntimePath = refreshResult.Context.RuntimePath;
            _currentRuntimeFingerprint = refreshResult.Context.RuntimeFingerprint;

            if (refreshResult.LoadResult.Groups.Count > 0)
            {
                IReadOnlyList<ProxyGroup> displayGroups = PrepareDisplayGroups(refreshResult.LoadResult.Groups);
                ProxyGroups = displayGroups;
                StoreCurrentProxySnapshot();

                int nodeCount = CountUniqueNodes(displayGroups);
                bool hasGeoDataFailure = TryGetControllerFailureStatus(refreshResult.Context.RuntimePath, out string controllerFailureMessage);
                if (refreshResult.Context.IsIncompatibleProfile)
                {
                    StatusMessage = refreshResult.LoadResult.Source == ProxyGroupLoadSource.MihomoController
                        ? string.Format(_localizedStrings["ProxiesStatusLoadedIncompatibleProfile"], nodeCount)
                        : string.Format(_localizedStrings["ProxiesStatusFallbackLoadedIncompatibleProfile"], nodeCount);
                    return;
                }

                if (refreshResult.AttemptedApply && !refreshResult.Applied && hasGeoDataFailure)
                {
                    StatusMessage = controllerFailureMessage;
                    return;
                }

                StatusMessage = refreshResult.LoadResult.Source == ProxyGroupLoadSource.RuntimeFile
                    ? string.Format(_localizedStrings["ProxiesStatusFallbackLoaded"], nodeCount)
                    : (refreshResult.AttemptedApply && refreshResult.Applied
                        ? string.Format(_localizedStrings["ProxiesStatusLoaded"], nodeCount)
                        : string.Format(_localizedStrings["ProxiesStatusLoadedNoApply"], nodeCount));
                return;
            }

            ProxyGroups = Array.Empty<ProxyGroup>();
            _pageWarmCacheService.InvalidateProxyGroups(refreshResult.Context.RuntimeFingerprint);
            StatusMessage = TryGetControllerFailureStatus(refreshResult.Context.RuntimePath, out string noGroupMessage)
                ? noGroupMessage
                : _localizedStrings["ProxiesStatusNoProxyGroups"];
        }

        private IReadOnlyList<ProxyGroup> PrepareDisplayGroups(IReadOnlyList<ProxyGroup> groups)
        {
            IReadOnlyList<ProxyGroup> clones = ModelSnapshotCloneHelper.CloneProxyGroups(groups);
            var expandedLookup = BuildExpansionLookup(ProxyGroups);
            bool expandByDefault = _appSettingsService.ProxyGroupsExpandedByDefault;

            foreach (ProxyGroup group in clones)
            {
                if (TryGetExpandedState(expandedLookup, group, out bool isExpanded))
                {
                    group.IsExpanded = isExpanded;
                }
                else
                {
                    group.IsExpanded = expandByDefault;
                }
            }

            return clones;
        }

        private static Dictionary<string, bool> BuildExpansionLookup(IReadOnlyList<ProxyGroup> groups)
        {
            var lookup = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (ProxyGroup group in groups)
            {
                if (!string.IsNullOrWhiteSpace(group.Name))
                {
                    lookup[group.Name] = group.IsExpanded;
                }

                if (!string.IsNullOrWhiteSpace(group.ControllerName))
                {
                    lookup[group.ControllerName] = group.IsExpanded;
                }
            }

            return lookup;
        }

        private static bool TryGetExpandedState(Dictionary<string, bool> expandedLookup, ProxyGroup group, out bool isExpanded)
        {
            if (!string.IsNullOrWhiteSpace(group.Name) && expandedLookup.TryGetValue(group.Name, out isExpanded))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(group.ControllerName) && expandedLookup.TryGetValue(group.ControllerName, out isExpanded))
            {
                return true;
            }

            isExpanded = false;
            return false;
        }

        private static bool IsDelayTestable(ProxyNode node)
        {
            return !NonTestableNodeNames.Contains(node.Name)
                && !NonTestableNodeTypes.Contains(node.Type);
        }

        private void OnMihomoConfigApplied(object? sender, string configPath)
        {
            if (_isDisposed || _dispatcherQueue is null)
            {
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (IsBusy || !TryBeginLoadSession(out int requestVersion, out CancellationToken cancellationToken))
                {
                    return;
                }

                _ = RefreshGroupsAsync(requestVersion, cancellationToken, reapplyRuntime: false, reason: "config-applied");
            });
        }

        private async Task<int> TestNodesAsync(IReadOnlyList<ProxyNode> nodes, CancellationToken cancellationToken)
        {
            int skippedCount = 0;
            using var semaphore = new SemaphoreSlim(TestAllConcurrencyLimit, TestAllConcurrencyLimit);
            var tasks = new List<Task>(nodes.Count);

            foreach (ProxyNode node in nodes)
            {
                if (!IsDelayTestable(node))
                {
                    skippedCount++;
                    node.DelayMs = null;
                    node.IsTesting = false;
                    continue;
                }

                tasks.Add(TestNodeDelayWithLimitAsync(node, semaphore, cancellationToken));
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }

            return skippedCount;
        }

        private async Task TestNodeDelayWithLimitAsync(ProxyNode node, SemaphoreSlim semaphore, CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                node.IsTesting = true;
                int? delay = await _mihomoService.TestProxyDelayAsync(node.Name, TestUrl, cancellationToken: cancellationToken);
                node.DelayMs = delay;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                node.DelayMs = null;
            }
            finally
            {
                node.IsTesting = false;
                semaphore.Release();
            }
        }

        private IReadOnlyList<ProxyNode> GetUniqueNodes()
        {
            return ProxyGroups
                .SelectMany(group => group.Members)
                .Select(member => member.Node)
                .DistinctBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int CountUniqueNodes(IEnumerable<ProxyGroup> groups)
        {
            return groups
                .SelectMany(group => group.Members)
                .Select(member => member.Node.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        private static bool IsControllerReady(ProxyGroupLoadResult result)
        {
            if (result.Source != ProxyGroupLoadSource.MihomoController)
            {
                return false;
            }

            foreach (ProxyGroup group in result.Groups)
            {
                if (group.Members.Count == 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(group.ControllerName))
                {
                    return false;
                }

                if (group.Members.Any(member => string.IsNullOrWhiteSpace(member.Node.ControllerName)))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ShouldRetryWithApply(string runtimePath, ProxyGroupLoadResult loadResult)
        {
            return !PathsEqual(_processService.CurrentConfigPath, runtimePath)
                && (loadResult.Source != ProxyGroupLoadSource.MihomoController || !IsControllerReady(loadResult));
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isDisposed)
            {
                return;
            }

            if (e.PropertyName == nameof(LocalizedStrings.CurrentLanguage) || e.PropertyName == "Item[]")
            {
                Title = _localizedStrings["PageProxies"];
            }
        }

        private bool TryGetControllerFailureStatus(string? runtimePath, out string message)
        {
            return MihomoFailureTextHelper.TryBuildControllerFailureMessage(
                _localizedStrings,
                _processService,
                _geoDataService,
                _tunService,
                runtimePath,
                out message);
        }

        private void StoreCurrentProxySnapshot()
        {
            if (!string.IsNullOrWhiteSpace(_currentRuntimeFingerprint) && ProxyGroups.Count > 0)
            {
                _pageWarmCacheService.StoreProxyGroups(_currentRuntimeFingerprint, ProxyGroups);
            }
        }

        private void InvalidateRulesSnapshot()
        {
            if (!string.IsNullOrWhiteSpace(_currentRuntimeFingerprint))
            {
                _pageWarmCacheService.InvalidateRules(_currentRuntimeFingerprint);
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

        private sealed class ProxyLoadContext
        {
            public ProxyLoadContext(ProfileItem profile, string runtimePath, string runtimeFingerprint, bool isIncompatibleProfile)
            {
                Profile = profile;
                RuntimePath = runtimePath;
                RuntimeFingerprint = runtimeFingerprint;
                IsIncompatibleProfile = isIncompatibleProfile;
            }

            public ProfileItem Profile { get; }

            public string RuntimePath { get; }

            public string RuntimeFingerprint { get; }

            public bool IsIncompatibleProfile { get; }
        }

        private sealed class ProxyRefreshResult
        {
            public ProxyRefreshResult(ProxyLoadContext context, ProxyGroupLoadResult loadResult, bool attemptedApply, bool applied)
            {
                Context = context;
                LoadResult = loadResult;
                AttemptedApply = attemptedApply;
                Applied = applied;
            }

            public ProxyLoadContext Context { get; }

            public ProxyGroupLoadResult LoadResult { get; }

            public bool AttemptedApply { get; }

            public bool Applied { get; }
        }
    }
}

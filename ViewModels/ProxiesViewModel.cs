using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        private const int ControllerWarmupAttempts = 6;
        private static readonly TimeSpan ControllerWarmupDelay = TimeSpan.FromMilliseconds(250);

        private readonly LocalizedStrings _localizedStrings;
        private readonly IProfileService _profileService;
        private readonly IConfigService _configService;
        private readonly IMihomoService _mihomoService;
        private readonly IGeoDataService _geoDataService;
        private readonly IProcessService _processService;
        private readonly IAppSettingsService _appSettingsService;
        private readonly DispatcherQueue? _dispatcherQueue;
        private bool _isWatchingRuntimeChanges;
        private bool _isDisposed;

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

        public ObservableCollection<ProxyGroup> ProxyGroups { get; } = new();

        public ProxiesViewModel(
            LocalizedStrings localizedStrings,
            IProfileService profileService,
            IConfigService configService,
            IMihomoService mihomoService,
            IGeoDataService geoDataService,
            IProcessService processService,
            IAppSettingsService appSettingsService)
        {
            _localizedStrings = localizedStrings;
            _profileService = profileService;
            _configService = configService;
            _mihomoService = mihomoService;
            _geoDataService = geoDataService;
            _processService = processService;
            _appSettingsService = appSettingsService;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["PageProxies"];
            TestUrl = "https://www.gstatic.com/generate_204";
            StatusMessage = string.Empty;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            StopWatchingRuntimeChanges();
            _localizedStrings.PropertyChanged -= OnLocalizedStringsPropertyChanged;
        }

        public Task InitializeAsync()
        {
            return RefreshGroupsAsync(reapplyRuntime: true);
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
        private Task RefreshAsync()
        {
            return RefreshGroupsAsync(reapplyRuntime: true);
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
                int? delay = await _mihomoService.TestProxyDelayAsync(member.Node.Name, TestUrl);
                if (delay is null)
                {
                    StatusMessage = _localizedStrings["ProxiesStatusDelayFailed"];
                    return;
                }

                member.Node.DelayMs = delay;
                StatusMessage = string.Format(_localizedStrings["ProxiesStatusDelaySuccess"], member.Node.Name, delay.Value);
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
                int skippedCount = await TestNodesAsync(nodes);
                StatusMessage = skippedCount > 0
                    ? string.Format(_localizedStrings["ProxiesStatusDelayAllFinishedWithSkipped"], skippedCount)
                    : _localizedStrings["ProxiesStatusDelayAllFinished"];
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
                int skippedCount = await TestNodesAsync(nodes);
                StatusMessage = skippedCount > 0
                    ? string.Format(_localizedStrings["ProxiesStatusDelayGroupFinishedWithSkipped"], group.Name, skippedCount)
                    : string.Format(_localizedStrings["ProxiesStatusDelayGroupFinished"], group.Name);
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

                bool selected = await _mihomoService.SelectProxyAsync(groupName, proxyName);
                if (!selected)
                {
                    StatusMessage = string.Format(_localizedStrings["ProxiesStatusProxySelectFailed"], group.Name, member.Node.Name);
                    return;
                }

                group.SetCurrentProxy(member.Node.Name);
                StatusMessage = string.Format(_localizedStrings["ProxiesStatusProxySelected"], group.Name, member.Node.Name);
            }
            finally
            {
                IsBusy = false;
            }
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

            _dispatcherQueue.TryEnqueue(async () =>
            {
                if (IsBusy)
                {
                    return;
                }

                await RefreshGroupsAsync(reapplyRuntime: false);
            });
        }

        private async Task<int> TestNodesAsync(IReadOnlyList<ProxyNode> nodes)
        {
            int skippedCount = 0;
            var semaphore = new SemaphoreSlim(TestAllConcurrencyLimit, TestAllConcurrencyLimit);
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

                tasks.Add(TestNodeDelayWithLimitAsync(node, semaphore));
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }

            return skippedCount;
        }

        private async Task TestNodeDelayWithLimitAsync(ProxyNode node, SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync();
            try
            {
                node.IsTesting = true;
                int? delay = await _mihomoService.TestProxyDelayAsync(node.Name, TestUrl);
                node.DelayMs = delay;
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

        private async Task RefreshGroupsAsync(bool reapplyRuntime)
        {
            IsBusy = true;
            try
            {
                ActiveProfile = _profileService.GetActiveProfile();
                ProxyGroups.Clear();

                if (ActiveProfile is null)
                {
                    StatusMessage = _localizedStrings["ProxiesStatusNoActiveProfile"];
                    return;
                }

                ProfileCompatibilityStatus compatibility = ProfileCompatibilityChecker.Check(ActiveProfile.FilePath);
                bool isIncompatibleProfile = compatibility == ProfileCompatibilityStatus.Base64NotYaml;

                string runtimePath = ActiveProfile.FilePath;
                bool applied = false;
                try
                {
                    runtimePath = _configService.GetRuntimePath(ActiveProfile);
                    if (reapplyRuntime)
                    {
                        applied = await _mihomoService.ApplyConfigAsync(runtimePath);
                    }
                }
                catch
                {
                    applied = false;
                }

                ProxyGroupLoadResult loadResult = await LoadProxyGroupsWithWarmupAsync(runtimePath);
                if (loadResult.Groups.Count > 0)
                {
                    bool expandByDefault = _appSettingsService.ProxyGroupsExpandedByDefault;
                    foreach (ProxyGroup group in loadResult.Groups)
                    {
                        group.IsExpanded = expandByDefault;
                        ProxyGroups.Add(group);
                    }

                    int nodeCount = GetUniqueNodes().Count;
                    bool hasGeoDataFailure = TryGetGeoDataFailureStatus(out string geoDataFailureMessage);
                    if (isIncompatibleProfile)
                    {
                        StatusMessage = loadResult.Source == ProxyGroupLoadSource.MihomoController
                            ? string.Format(_localizedStrings["ProxiesStatusLoadedIncompatibleProfile"], nodeCount)
                            : string.Format(_localizedStrings["ProxiesStatusFallbackLoadedIncompatibleProfile"], nodeCount);
                    }
                    else if (reapplyRuntime && !applied && hasGeoDataFailure)
                    {
                        StatusMessage = geoDataFailureMessage;
                    }
                    else
                    {
                        StatusMessage = loadResult.Source == ProxyGroupLoadSource.RuntimeFile
                            ? string.Format(_localizedStrings["ProxiesStatusFallbackLoaded"], nodeCount)
                            : (reapplyRuntime && applied
                                ? string.Format(_localizedStrings["ProxiesStatusLoaded"], nodeCount)
                                : string.Format(_localizedStrings["ProxiesStatusLoadedNoApply"], nodeCount));
                    }

                    return;
                }

                StatusMessage = TryGetGeoDataFailureStatus(out string noGroupGeoDataFailureMessage)
                    ? noGroupGeoDataFailureMessage
                    : _localizedStrings["ProxiesStatusNoProxyGroups"];
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
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

        private async Task<ProxyGroupLoadResult> LoadProxyGroupsWithWarmupAsync(string runtimePath)
        {
            ProxyGroupLoadResult bestResult = await _mihomoService.GetProxyGroupsAsync(runtimePath);
            if (IsControllerReady(bestResult))
            {
                return bestResult;
            }

            for (int attempt = 1; attempt < ControllerWarmupAttempts; attempt++)
            {
                await Task.Delay(ControllerWarmupDelay);

                ProxyGroupLoadResult currentResult = await _mihomoService.GetProxyGroupsAsync(runtimePath);
                if (currentResult.Groups.Count > 0)
                {
                    bestResult = currentResult;
                }

                if (IsControllerReady(currentResult))
                {
                    return currentResult;
                }
            }

            return bestResult;
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

        private bool TryGetGeoDataFailureStatus(out string message)
        {
            return GeoDataStatusTextHelper.TryBuildControllerFailureMessage(
                _localizedStrings,
                _processService,
                _geoDataService,
                out message);
        }
    }
}

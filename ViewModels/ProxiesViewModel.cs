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
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.ViewModels
{
    public partial class ProxiesViewModel : ObservableObject
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

        private readonly LocalizedStrings _localizedStrings;
        private readonly IProfileService _profileService;
        private readonly IConfigService _configService;
        private readonly IMihomoService _mihomoService;
        private readonly DispatcherQueue? _dispatcherQueue;
        private bool _isWatchingRuntimeChanges;

        [ObservableProperty]
        public partial string Title { get; set; }

        [ObservableProperty]
        public partial string TestUrl { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; }

        [ObservableProperty]
        public partial ProfileItem? ActiveProfile { get; set; }

        [ObservableProperty]
        public partial ProxyNode? SelectedNode { get; set; }

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        public ObservableCollection<ProxyNode> Nodes { get; } = new();

        public ProxiesViewModel(
            LocalizedStrings localizedStrings,
            IProfileService profileService,
            IConfigService configService,
            IMihomoService mihomoService)
        {
            _localizedStrings = localizedStrings;
            _profileService = profileService;
            _configService = configService;
            _mihomoService = mihomoService;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["PageProxies"];
            TestUrl = "https://www.gstatic.com/generate_204";
            StatusMessage = string.Empty;
        }

        public Task InitializeAsync()
        {
            return RefreshNodesAsync(reapplyRuntime: true);
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
            return RefreshNodesAsync(reapplyRuntime: true);
        }

        [RelayCommand(CanExecute = nameof(CanTestSelected))]
        private async Task TestSelectedAsync()
        {
            if (SelectedNode is null || IsBusy)
            {
                return;
            }

            if (!IsDelayTestable(SelectedNode))
            {
                StatusMessage = string.Format(_localizedStrings["ProxiesStatusDelaySkippedNonTestable"], SelectedNode.Name);
                return;
            }

            IsBusy = true;
            try
            {
                SelectedNode.IsTesting = true;
                int? delay = await _mihomoService.TestProxyDelayAsync(SelectedNode.Name, TestUrl);
                if (delay is null)
                {
                    StatusMessage = _localizedStrings["ProxiesStatusDelayFailed"];
                    return;
                }

                SelectedNode.DelayMs = delay;
                StatusMessage = string.Format(_localizedStrings["ProxiesStatusDelaySuccess"], SelectedNode.Name, delay.Value);
            }
            finally
            {
                SelectedNode.IsTesting = false;
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task TestAllAsync()
        {
            if (Nodes.Count == 0 || IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                int skippedCount = 0;
                var semaphore = new SemaphoreSlim(TestAllConcurrencyLimit, TestAllConcurrencyLimit);
                var tasks = new List<Task>(Nodes.Count);

                foreach (ProxyNode node in Nodes)
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

                StatusMessage = skippedCount > 0
                    ? string.Format(_localizedStrings["ProxiesStatusDelayAllFinishedWithSkipped"], skippedCount)
                    : _localizedStrings["ProxiesStatusDelayAllFinished"];
            }
            finally
            {
                IsBusy = false;
            }
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

        partial void OnSelectedNodeChanged(ProxyNode? value)
        {
            TestSelectedCommand.NotifyCanExecuteChanged();
        }

        private bool CanTestSelected()
        {
            return SelectedNode is not null && !IsBusy;
        }

        private static bool IsDelayTestable(ProxyNode node)
        {
            return !NonTestableNodeNames.Contains(node.Name)
                && !NonTestableNodeTypes.Contains(node.Type);
        }

        private void OnMihomoConfigApplied(object? sender, string configPath)
        {
            if (_dispatcherQueue is null)
            {
                return;
            }

            _dispatcherQueue.TryEnqueue(async () =>
            {
                if (IsBusy)
                {
                    return;
                }

                await RefreshNodesAsync(reapplyRuntime: false);
            });
        }

        private async Task RefreshNodesAsync(bool reapplyRuntime)
        {
            IsBusy = true;
            try
            {
                ActiveProfile = _profileService.GetActiveProfile();
                Nodes.Clear();

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

                IReadOnlyList<ProxyNode> mihomoNodes = await _mihomoService.GetProxiesAsync();
                if (mihomoNodes.Count > 0)
                {
                    foreach (ProxyNode node in mihomoNodes)
                    {
                        Nodes.Add(node);
                    }

                    if (isIncompatibleProfile)
                    {
                        StatusMessage = string.Format(_localizedStrings["ProxiesStatusLoadedIncompatibleProfile"], Nodes.Count);
                    }
                    else
                    {
                        StatusMessage = reapplyRuntime && applied
                            ? string.Format(_localizedStrings["ProxiesStatusLoaded"], Nodes.Count)
                            : string.Format(_localizedStrings["ProxiesStatusLoadedNoApply"], Nodes.Count);
                    }

                    return;
                }

                IReadOnlyList<ProxyNode> fallbackNodes = ProxyConfigParser.ParseFromFile(runtimePath);
                foreach (ProxyNode node in fallbackNodes)
                {
                    Nodes.Add(node);
                }

                StatusMessage = isIncompatibleProfile
                    ? string.Format(_localizedStrings["ProxiesStatusFallbackLoadedIncompatibleProfile"], Nodes.Count)
                    : string.Format(_localizedStrings["ProxiesStatusFallbackLoaded"], Nodes.Count);
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
            if (e.PropertyName == nameof(LocalizedStrings.CurrentLanguage) || e.PropertyName == "Item[]")
            {
                Title = _localizedStrings["PageProxies"];
            }
        }
    }
}

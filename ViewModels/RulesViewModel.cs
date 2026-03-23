using ClashWinUI.Common;
using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.ViewModels
{
    public partial class RulesViewModel : ObservableObject, IDisposable
    {
        private readonly LocalizedStrings _localizedStrings;
        private readonly IProfileService _profileService;
        private readonly IConfigService _configService;
        private readonly IMihomoService _mihomoService;
        private readonly IGeoDataService _geoDataService;
        private readonly IProcessService _processService;
        private readonly ISystemProxyService _systemProxyService;
        private readonly SemaphoreSlim _applySemaphore = new(1, 1);
        private readonly CancellationTokenSource _disposeCancellation = new();
        private readonly List<RuntimeRuleItem> _allRules = new();

        private ProfileItem? _activeProfile;
        private bool _isDisposed;

        [ObservableProperty]
        public partial string Title { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; }

        [ObservableProperty]
        public partial string SearchKeyword { get; set; }

        [ObservableProperty]
        public partial bool IsLoading { get; set; }

        public ObservableCollection<RuntimeRuleItem> Rules { get; } = new();

        public RulesViewModel(
            LocalizedStrings localizedStrings,
            IProfileService profileService,
            IConfigService configService,
            IMihomoService mihomoService,
            IGeoDataService geoDataService,
            IProcessService processService,
            ISystemProxyService systemProxyService)
        {
            _localizedStrings = localizedStrings;
            _profileService = profileService;
            _configService = configService;
            _mihomoService = mihomoService;
            _geoDataService = geoDataService;
            _processService = processService;
            _systemProxyService = systemProxyService;

            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["PageRules"];
            StatusMessage = string.Empty;
            SearchKeyword = string.Empty;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _disposeCancellation.Cancel();
            _localizedStrings.PropertyChanged -= OnLocalizedStringsPropertyChanged;
            _allRules.Clear();
            Rules.Clear();
            _activeProfile = null;
            _applySemaphore.Dispose();
            _disposeCancellation.Dispose();
        }

        public Task InitializeAsync()
        {
            return RefreshRulesAsync(showStatus: true);
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
                    RollbackRuleOverride(rule.StableId, previousState);
                    StatusMessage = GeoDataStatusTextHelper.TryBuildControllerFailureMessage(
                        _localizedStrings,
                        _processService,
                        _geoDataService,
                        out string geoDataMessage)
                        ? geoDataMessage
                        : _localizedStrings["RulesStatusApplyFailed"];
                    return false;
                }

                int proxyPort = _processService.ResolveProxyPort(runtimePath);
                await _systemProxyService.EnableAsync("127.0.0.1", proxyPort, AppConstants.SystemProxyBypassList);
                if (_isDisposed || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                await RefreshRulesAsync(showStatus: false);
                if (_isDisposed || cancellationToken.IsCancellationRequested)
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
            ApplyFilters();
        }

        private async Task RefreshRulesAsync(bool showStatus)
        {
            if (_isDisposed || _disposeCancellation.IsCancellationRequested)
            {
                return;
            }

            IsLoading = true;
            try
            {
                CancellationToken cancellationToken = _disposeCancellation.Token;
                _activeProfile = _profileService.GetActiveProfile();
                if (_activeProfile is null)
                {
                    _allRules.Clear();
                    Rules.Clear();
                    StatusMessage = _localizedStrings["RulesNoActiveProfile"];
                    return;
                }

                IReadOnlyList<RuntimeRuleItem> items = _configService.GetRuntimeRules(_activeProfile);
                await PopulateProxyTargetsAsync(_activeProfile, items, cancellationToken);
                if (_isDisposed || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _allRules.Clear();
                _allRules.AddRange(items);
                ApplyFilters();

                if (showStatus)
                {
                    StatusMessage = _allRules.Count == 0
                        ? _localizedStrings["RulesStatusEmpty"]
                        : string.Format(_localizedStrings["RulesStatusLoaded"], _allRules.Count);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the page leaves and the ViewModel is disposed.
            }
            catch (Exception ex)
            {
                _allRules.Clear();
                Rules.Clear();
                if (showStatus)
                {
                    StatusMessage = string.Format(_localizedStrings["RulesStatusLoadFailed"], ex.Message);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task PopulateProxyTargetsAsync(ProfileItem profile, IReadOnlyList<RuntimeRuleItem> items, CancellationToken cancellationToken)
        {
            string runtimePath = _configService.GetRuntimePath(profile);
            ProxyGroupLoadResult proxyLoadResult = await _mihomoService.GetProxyGroupsAsync(runtimePath, cancellationToken);
            var groupLookup = new Dictionary<string, ProxyGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (ProxyGroup group in proxyLoadResult.Groups)
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

            foreach (RuntimeRuleItem item in items)
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

        private void ApplyFilters()
        {
            string[] keywords = (SearchKeyword ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            IEnumerable<RuntimeRuleItem> filtered = _allRules;
            if (keywords.Length > 0)
            {
                filtered = filtered.Where(rule =>
                    keywords.Any(keyword => rule.SearchText.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
            }

            Rules.Clear();
            foreach (RuntimeRuleItem item in filtered)
            {
                Rules.Add(item);
            }
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
    }
}

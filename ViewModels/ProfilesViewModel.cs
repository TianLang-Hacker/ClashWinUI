using ClashWinUI.Common;
using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.ViewModels
{
    public partial class ProfilesViewModel : ObservableObject, IDisposable
    {
        private readonly LocalizedStrings _localizedStrings;
        private readonly IProfileService _profileService;
        private readonly IConfigService _configService;
        private readonly IMihomoService _mihomoService;
        private readonly IGeoDataService _geoDataService;
        private readonly IProcessService _processService;
        private readonly ITunService _tunService;
        private readonly ISystemProxyService _systemProxyService;
        private int _profileMetadataRefreshVersion;
        private bool _suppressSelectionActivation;
        private string? _pendingSelectedProfileId;
        private bool _isDisposed;

        [ObservableProperty]
        public partial string Title { get; set; }

        [ObservableProperty]
        public partial string SubscriptionUrl { get; set; }

        [ObservableProperty]
        public partial string ActiveProfileText { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; }

        [ObservableProperty]
        public partial ProfileItem? SelectedProfile { get; set; }

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        public ObservableCollection<ProfileItem> Profiles { get; } = new();

        public ProfilesViewModel(
            LocalizedStrings localizedStrings,
            IProfileService profileService,
            IConfigService configService,
            IMihomoService mihomoService,
            IGeoDataService geoDataService,
            IProcessService processService,
            ITunService tunService,
            ISystemProxyService systemProxyService)
        {
            _localizedStrings = localizedStrings;
            _profileService = profileService;
            _configService = configService;
            _mihomoService = mihomoService;
            _geoDataService = geoDataService;
            _processService = processService;
            _tunService = tunService;
            _systemProxyService = systemProxyService;
            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["PageProfiles"];
            SubscriptionUrl = string.Empty;
            ActiveProfileText = string.Empty;
            StatusMessage = string.Empty;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _localizedStrings.PropertyChanged -= OnLocalizedStringsPropertyChanged;
        }

        public Task InitializeAsync()
        {
            ReloadProfiles();
            QueueProfileMetadataRefresh();
            return Task.CompletedTask;
        }

        [RelayCommand]
        private Task RefreshAsync()
        {
            ReloadProfiles();
            QueueProfileMetadataRefresh();
            return Task.CompletedTask;
        }

        [RelayCommand]
        private async Task FetchSubscriptionAsync()
        {
            if (string.IsNullOrWhiteSpace(SubscriptionUrl) || IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                ProfileItem profile = await _profileService.AddOrUpdateFromSubscriptionAsync(SubscriptionUrl);
                ProfileCompatibilityStatus compatibility = ProfileCompatibilityChecker.Check(profile.FilePath);
                (bool applied, string runtimePath) = await ActivateProfileAsync(profile);

                ReloadProfiles();
                StatusMessage = compatibility == ProfileCompatibilityStatus.Base64NotYaml
                    ? _localizedStrings["ProfilesStatusNotMihomoCompatible"]
                    : (applied
                        ? _localizedStrings["ProfilesStatusFetchedAndApplied"]
                        : GetApplyFailureStatus("ProfilesStatusFetchedOnly", runtimePath));
            }
            catch (Exception ex)
            {
                StatusMessage = $"{_localizedStrings["ProfilesStatusFailed"]}: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task ImportLocalFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                ProfileItem profile = await _profileService.ImportLocalFileAsync(filePath);
                ProfileCompatibilityStatus compatibility = ProfileCompatibilityChecker.Check(profile.FilePath);
                (bool applied, string runtimePath) = await ActivateProfileAsync(profile);

                ReloadProfiles();
                StatusMessage = compatibility == ProfileCompatibilityStatus.Base64NotYaml
                    ? _localizedStrings["ProfilesStatusNotMihomoCompatible"]
                    : (applied
                        ? _localizedStrings["ProfilesStatusImportedAndApplied"]
                        : GetApplyFailureStatus("ProfilesStatusImportedOnly", runtimePath));
            }
            catch (Exception ex)
            {
                StatusMessage = $"{_localizedStrings["ProfilesStatusFailed"]}: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
        private Task DeleteSelectedAsync()
        {
            if (SelectedProfile is null || IsBusy)
            {
                return Task.CompletedTask;
            }

            _profileService.DeleteProfile(SelectedProfile.Id);
            ReloadProfiles();
            StatusMessage = _localizedStrings["ProfilesStatusDeleted"];
            return Task.CompletedTask;
        }

        partial void OnSelectedProfileChanged(ProfileItem? value)
        {
            DeleteSelectedCommand.NotifyCanExecuteChanged();

            if (value is null || _suppressSelectionActivation)
            {
                return;
            }

            if (IsBusy)
            {
                _pendingSelectedProfileId = value.Id;
                return;
            }

            if (IsActiveProfile(value.Id))
            {
                return;
            }

            _ = ActivateSelectedProfileAsync(value);
        }

        partial void OnIsBusyChanged(bool value)
        {
            DeleteSelectedCommand.NotifyCanExecuteChanged();
        }

        private bool CanDeleteSelected()
        {
            return SelectedProfile is not null && !IsBusy;
        }

        private async Task<bool> ApplyRuntimeAndSyncProxyAsync(string runtimePath)
        {
            bool applied = await _mihomoService.ApplyConfigAsync(runtimePath);
            if (!applied)
            {
                if (PathsEqual(_processService.CurrentConfigPath, runtimePath))
                {
                    await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                        _systemProxyService,
                        _processService,
                        _tunService,
                        runtimePath);
                }

                return false;
            }

            await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                _systemProxyService,
                _processService,
                _tunService,
                runtimePath);
            return true;
        }

        private async Task<(bool Applied, string RuntimePath)> ActivateProfileAsync(ProfileItem profile)
        {
            if (!_profileService.SetActiveProfile(profile.Id))
            {
                return (false, _configService.GetRuntimePath(profile));
            }

            string runtimePath = _configService.GetRuntimePath(profile);
            bool applied = await ApplyRuntimeAndSyncProxyAsync(runtimePath);
            return (applied, runtimePath);
        }

        private async Task ActivateSelectedProfileAsync(ProfileItem profile)
        {
            if (IsBusy || IsActiveProfile(profile.Id))
            {
                return;
            }

            IsBusy = true;
            _pendingSelectedProfileId = null;
            try
            {
                ProfileCompatibilityStatus compatibility = ProfileCompatibilityChecker.Check(profile.FilePath);
                (bool applied, string runtimePath) = await ActivateProfileAsync(profile);
                ReloadProfiles();
                StatusMessage = compatibility == ProfileCompatibilityStatus.Base64NotYaml
                    ? _localizedStrings["ProfilesStatusNotMihomoCompatible"]
                    : (applied
                        ? _localizedStrings["ProfilesStatusActivated"]
                        : GetApplyFailureStatus("ProfilesStatusActivatedNotApplied", runtimePath));
            }
            catch (Exception ex)
            {
                StatusMessage = $"{_localizedStrings["ProfilesStatusFailed"]}: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                await ProcessPendingSelectionAsync();
            }
        }

        private async Task ProcessPendingSelectionAsync()
        {
            string? pendingId = _pendingSelectedProfileId;
            _pendingSelectedProfileId = null;

            if (string.IsNullOrWhiteSpace(pendingId)
                || SelectedProfile is null
                || !string.Equals(SelectedProfile.Id, pendingId, StringComparison.OrdinalIgnoreCase)
                || IsActiveProfile(pendingId))
            {
                return;
            }

            await ActivateSelectedProfileAsync(SelectedProfile);
        }

        private bool IsActiveProfile(string? profileId)
        {
            return !string.IsNullOrWhiteSpace(profileId)
                && string.Equals(
                    _profileService.GetActiveProfile()?.Id,
                    profileId,
                    StringComparison.OrdinalIgnoreCase);
        }

        private void ReloadProfiles()
        {
            string? previouslySelectedId = SelectedProfile?.Id;

            Profiles.Clear();
            foreach (ProfileItem profile in _profileService.GetProfiles())
            {
                Profiles.Add(profile);
            }

            ProfileItem? active = _profileService.GetActiveProfile();
            string activeName = active?.DisplayName ?? _localizedStrings["ProfilesNoActive"];
            ActiveProfileText = $"{_localizedStrings["ProfilesActiveLabel"]}: {activeName}";

            ProfileItem? nextSelected = null;
            if (!string.IsNullOrWhiteSpace(previouslySelectedId))
            {
                nextSelected = Profiles.FirstOrDefault(item => item.Id == previouslySelectedId);
            }

            if (nextSelected is null && active is not null)
            {
                nextSelected = Profiles.FirstOrDefault(item => item.Id == active.Id);
            }

            SetSelectedProfileSilently(nextSelected);
        }

        private void QueueProfileMetadataRefresh()
        {
            int requestVersion = Interlocked.Increment(ref _profileMetadataRefreshVersion);
            _ = RefreshProfileMetadataAsync(requestVersion);
        }

        private async Task RefreshProfileMetadataAsync(int requestVersion)
        {
            ProfileItem[] snapshot = _profileService.GetProfiles().ToArray();
            (string Id, int NodeCount)[] recalculatedCounts = await Task.Run(() =>
            {
                return snapshot
                    .Select(profile => (profile.Id, CalculateNodeCount(profile.FilePath)))
                    .ToArray();
            });

            if (_isDisposed || requestVersion != _profileMetadataRefreshVersion)
            {
                return;
            }

            bool changed = false;
            foreach ((string id, int nodeCount) in recalculatedCounts)
            {
                ProfileItem? profile = snapshot.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (profile is null || profile.NodeCount == nodeCount)
                {
                    continue;
                }

                profile.NodeCount = nodeCount;
                changed = true;
            }

            if (changed)
            {
                ReloadProfiles();
            }
        }

        private static int CalculateNodeCount(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return 0;
            }

            try
            {
                return ProxyConfigParser.ParseFromFile(filePath).Count;
            }
            catch
            {
                return 0;
            }
        }

        private void SetSelectedProfileSilently(ProfileItem? profile)
        {
            _suppressSelectionActivation = true;
            try
            {
                SelectedProfile = profile;
            }
            finally
            {
                _suppressSelectionActivation = false;
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

            Title = _localizedStrings["PageProfiles"];
            ReloadProfiles();
        }

        private string GetApplyFailureStatus(string fallbackResourceKey, string? runtimePath)
        {
            return MihomoFailureTextHelper.TryBuildControllerFailureMessage(
                _localizedStrings,
                _processService,
                _geoDataService,
                _tunService,
                runtimePath,
                out string controllerMessage)
                ? controllerMessage
                : _localizedStrings[fallbackResourceKey];
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
    }
}

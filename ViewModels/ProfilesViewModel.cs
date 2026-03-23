using ClashWinUI.Common;
using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
        private readonly ISystemProxyService _systemProxyService;
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
            return Task.CompletedTask;
        }

        [RelayCommand]
        private Task RefreshAsync()
        {
            ReloadProfiles();
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
                bool applied = await ActivateProfileAsync(profile);

                ReloadProfiles();
                StatusMessage = compatibility == ProfileCompatibilityStatus.Base64NotYaml
                    ? _localizedStrings["ProfilesStatusNotMihomoCompatible"]
                    : (applied
                        ? _localizedStrings["ProfilesStatusFetchedAndApplied"]
                        : GetApplyFailureStatus("ProfilesStatusFetchedOnly"));
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
                bool applied = await ActivateProfileAsync(profile);

                ReloadProfiles();
                StatusMessage = compatibility == ProfileCompatibilityStatus.Base64NotYaml
                    ? _localizedStrings["ProfilesStatusNotMihomoCompatible"]
                    : (applied
                        ? _localizedStrings["ProfilesStatusImportedAndApplied"]
                        : GetApplyFailureStatus("ProfilesStatusImportedOnly"));
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

        private async Task<bool> ApplyRuntimeAndSyncProxyAsync(ProfileItem profile)
        {
            string runtimePath = _configService.GetRuntimePath(profile);
            bool applied = await _mihomoService.ApplyConfigAsync(runtimePath);
            if (!applied)
            {
                return false;
            }

            int proxyPort = _processService.ResolveProxyPort(runtimePath);
            await _systemProxyService.EnableAsync("127.0.0.1", proxyPort, AppConstants.SystemProxyBypassList);
            return true;
        }

        private async Task<bool> ActivateProfileAsync(ProfileItem profile)
        {
            return _profileService.SetActiveProfile(profile.Id)
                && await ApplyRuntimeAndSyncProxyAsync(profile);
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
                bool applied = await ActivateProfileAsync(profile);
                ReloadProfiles();
                StatusMessage = compatibility == ProfileCompatibilityStatus.Base64NotYaml
                    ? _localizedStrings["ProfilesStatusNotMihomoCompatible"]
                    : (applied
                        ? _localizedStrings["ProfilesStatusActivated"]
                        : GetApplyFailureStatus("ProfilesStatusActivatedNotApplied"));
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

        private string GetApplyFailureStatus(string fallbackResourceKey)
        {
            return GeoDataStatusTextHelper.TryBuildControllerFailureMessage(
                _localizedStrings,
                _processService,
                _geoDataService,
                out string geoDataMessage)
                ? geoDataMessage
                : _localizedStrings[fallbackResourceKey];
        }
    }
}

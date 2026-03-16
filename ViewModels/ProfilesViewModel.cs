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
    public partial class ProfilesViewModel : ObservableObject
    {
        private readonly LocalizedStrings _localizedStrings;
        private readonly IProfileService _profileService;
        private readonly IMihomoService _mihomoService;

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
            IMihomoService mihomoService)
        {
            _localizedStrings = localizedStrings;
            _profileService = profileService;
            _mihomoService = mihomoService;
            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["PageProfiles"];
            SubscriptionUrl = string.Empty;
            ActiveProfileText = string.Empty;
            StatusMessage = string.Empty;
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
                bool switched = _profileService.SetActiveProfile(profile.Id);
                ProfileCompatibilityStatus compatibility = ProfileCompatibilityChecker.Check(profile.FilePath);
                bool applied = switched && await _mihomoService.ApplyConfigAsync(profile.FilePath);

                ReloadProfiles();
                StatusMessage = compatibility == ProfileCompatibilityStatus.Base64NotYaml
                    ? _localizedStrings["ProfilesStatusNotMihomoCompatible"]
                    : (applied
                        ? _localizedStrings["ProfilesStatusFetchedAndApplied"]
                        : _localizedStrings["ProfilesStatusFetchedOnly"]);
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
                bool switched = _profileService.SetActiveProfile(profile.Id);
                ProfileCompatibilityStatus compatibility = ProfileCompatibilityChecker.Check(profile.FilePath);
                bool applied = switched && await _mihomoService.ApplyConfigAsync(profile.FilePath);

                ReloadProfiles();
                StatusMessage = compatibility == ProfileCompatibilityStatus.Base64NotYaml
                    ? _localizedStrings["ProfilesStatusNotMihomoCompatible"]
                    : (applied
                        ? _localizedStrings["ProfilesStatusImportedAndApplied"]
                        : _localizedStrings["ProfilesStatusImportedOnly"]);
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

        [RelayCommand(CanExecute = nameof(CanActivateOrDelete))]
        private async Task ActivateSelectedAsync()
        {
            if (SelectedProfile is null || IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                bool switched = _profileService.SetActiveProfile(SelectedProfile.Id);
                ProfileCompatibilityStatus compatibility = ProfileCompatibilityChecker.Check(SelectedProfile.FilePath);
                bool applied = switched && await _mihomoService.ApplyConfigAsync(SelectedProfile.FilePath);
                ReloadProfiles();
                StatusMessage = compatibility == ProfileCompatibilityStatus.Base64NotYaml
                    ? _localizedStrings["ProfilesStatusNotMihomoCompatible"]
                    : (applied
                        ? _localizedStrings["ProfilesStatusActivated"]
                        : _localizedStrings["ProfilesStatusActivatedNotApplied"]);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanActivateOrDelete))]
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
            ActivateSelectedCommand.NotifyCanExecuteChanged();
            DeleteSelectedCommand.NotifyCanExecuteChanged();
        }

        private bool CanActivateOrDelete()
        {
            return SelectedProfile is not null && !IsBusy;
        }

        private void ReloadProfiles()
        {
            ProfileItem? previouslySelected = SelectedProfile;

            Profiles.Clear();
            foreach (ProfileItem profile in _profileService.GetProfiles())
            {
                Profiles.Add(profile);
            }

            ProfileItem? active = _profileService.GetActiveProfile();
            string activeName = active?.DisplayName ?? _localizedStrings["ProfilesNoActive"];
            ActiveProfileText = $"{_localizedStrings["ProfilesActiveLabel"]}: {activeName}";

            if (previouslySelected is not null)
            {
                SelectedProfile = Profiles.FirstOrDefault(item => item.Id == previouslySelected.Id);
            }

            if (SelectedProfile is null && active is not null)
            {
                SelectedProfile = Profiles.FirstOrDefault(item => item.Id == active.Id);
            }
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(LocalizedStrings.CurrentLanguage) && e.PropertyName != "Item[]")
            {
                return;
            }

            Title = _localizedStrings["PageProfiles"];
            ReloadProfiles();
        }
    }
}

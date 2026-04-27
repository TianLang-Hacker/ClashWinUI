using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ClashWinUI.ViewModels
{
    public partial class WelcomeWizardViewModel : ObservableObject
    {
        private const int IntroPageIndex = 0;
        private const int ThemePageIndex = 1;
        private const int KernelPageIndex = 2;
        private const int ImportPageIndex = 3;
        private const int DownloadPageIndex = 4;
        private const int CompletePageIndex = 5;

        private const string ThemeLight = "light";
        private const string ThemeDark = "dark";
        private const string BackdropMica = "mica";
        private const string BackdropMicaAlt = "mica_alt";
        private const string BackdropAcrylic = "acrylic";
        private const string KernelExecutableName = "mihomo.exe";
        private static readonly string[] GeoDataAssetNames =
        [
            "geoip.metadb",
            "geoip.dat",
            "geosite.dat",
        ];

        private readonly LocalizedStrings _localizedStrings;
        private readonly IThemeService _themeService;
        private readonly IAppSettingsService _appSettingsService;
        private readonly IKernelPathService _kernelPathService;
        private readonly IKernelBootstrapService _kernelBootstrapService;
        private readonly IGeoDataService _geoDataService;
        private readonly IProfileService _profileService;
        private bool _isUpdatingThemeSelection;
        private bool _downloadStarted;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIntroPage))]
        [NotifyPropertyChangedFor(nameof(IsThemePage))]
        [NotifyPropertyChangedFor(nameof(IsKernelPage))]
        [NotifyPropertyChangedFor(nameof(IsImportPage))]
        [NotifyPropertyChangedFor(nameof(IsDownloadPage))]
        [NotifyPropertyChangedFor(nameof(IsCompletePage))]
        public partial int CurrentPageIndex { get; set; }

        [ObservableProperty]
        public partial string SelectedAppThemeTag { get; set; }

        [ObservableProperty]
        public partial string SelectedBackdropTag { get; set; }

        [ObservableProperty]
        public partial string SelectedLanguageTag { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCustomKernelSelected))]
        public partial bool IsOnlineKernelSelected { get; set; }

        [ObservableProperty]
        public partial string CustomKernelPathInput { get; set; }

        [ObservableProperty]
        public partial string KernelStatusMessage { get; set; }

        [ObservableProperty]
        public partial string SubscriptionUrl { get; set; }

        [ObservableProperty]
        public partial string ImportStatusMessage { get; set; }

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        [ObservableProperty]
        public partial bool IsDownloading { get; set; }

        [ObservableProperty]
        public partial bool DownloadCompleted { get; set; }

        [ObservableProperty]
        public partial bool DownloadFailed { get; set; }

        [ObservableProperty]
        public partial double DownloadProgressValue { get; set; }

        [ObservableProperty]
        public partial string DownloadProgressText { get; set; }

        [ObservableProperty]
        public partial string DownloadStatusMessage { get; set; }

        public WelcomeWizardViewModel(
            LocalizedStrings localizedStrings,
            IThemeService themeService,
            IAppSettingsService appSettingsService,
            IKernelPathService kernelPathService,
            IKernelBootstrapService kernelBootstrapService,
            IGeoDataService geoDataService,
            IProfileService profileService)
        {
            _localizedStrings = localizedStrings;
            _themeService = themeService;
            _appSettingsService = appSettingsService;
            _kernelPathService = kernelPathService;
            _kernelBootstrapService = kernelBootstrapService;
            _geoDataService = geoDataService;
            _profileService = profileService;

            CurrentPageIndex = IntroPageIndex;
            SelectedAppThemeTag = MapAppThemeToTag(_themeService.CurrentAppTheme);
            SelectedBackdropTag = MapBackdropToTag(_themeService.CurrentBackdrop);
            SelectedLanguageTag = _localizedStrings.CurrentLanguage;
            IsOnlineKernelSelected = true;
            CustomKernelPathInput = _kernelPathService.CustomKernelPath ?? _kernelPathService.DefaultKernelPath;
            KernelStatusMessage = string.Empty;
            SubscriptionUrl = string.Empty;
            ImportStatusMessage = string.Empty;
            DownloadProgressText = FormatProgressText(0);
            DownloadStatusMessage = string.Empty;
        }

        public event EventHandler? Completed;

        public bool IsIntroPage => CurrentPageIndex == IntroPageIndex;
        public bool IsThemePage => CurrentPageIndex == ThemePageIndex;
        public bool IsKernelPage => CurrentPageIndex == KernelPageIndex;
        public bool IsImportPage => CurrentPageIndex == ImportPageIndex;
        public bool IsDownloadPage => CurrentPageIndex == DownloadPageIndex;
        public bool IsCompletePage => CurrentPageIndex == CompletePageIndex;
        public bool IsCustomKernelSelected => !IsOnlineKernelSelected;
        public bool ShowBackButton => CurrentPageIndex > IntroPageIndex && CurrentPageIndex < CompletePageIndex;
        public bool ShowNextButton => CurrentPageIndex < CompletePageIndex;
        public bool ShowSkipButton => CurrentPageIndex is IntroPageIndex or ThemePageIndex or ImportPageIndex or DownloadPageIndex;
        public bool ShowFinishButton => CurrentPageIndex == CompletePageIndex;
        public bool ShowRetryButton => CurrentPageIndex == DownloadPageIndex && DownloadFailed && !IsDownloading;
        public bool CanImport => !IsBusy;
        public bool CanGoBack => ShowBackButton && !IsBusy && !IsDownloading;
        public bool CanGoNext => CurrentPageIndex switch
        {
            KernelPageIndex => CanUseSelectedKernel(),
            DownloadPageIndex => DownloadCompleted,
            CompletePageIndex => false,
            _ => !IsBusy && !IsDownloading,
        };
        public bool CanSkip => ShowSkipButton && !IsBusy && !IsDownloading && CurrentPageIndex != DownloadPageIndex;
        public bool CanFinish => ShowFinishButton && !IsBusy && !IsDownloading;

        [RelayCommand]
        private void Back()
        {
            TryGoBack();
        }

        [RelayCommand]
        private void Next()
        {
            TryGoNext();
        }

        [RelayCommand]
        private void Skip()
        {
            TrySkip();
        }

        public bool TryGoBack()
        {
            if (!CanGoBack)
            {
                return false;
            }

            CurrentPageIndex--;
            return true;
        }

        public bool TryGoNext()
        {
            if (!CanGoNext)
            {
                return false;
            }

            if (CurrentPageIndex == KernelPageIndex && !ApplyKernelSelection())
            {
                return false;
            }

            CurrentPageIndex++;
            return true;
        }

        public bool TrySkip()
        {
            if (!CanSkip)
            {
                return false;
            }

            CurrentPageIndex++;
            return true;
        }

        [RelayCommand]
        private async Task ImportFromUrlAsync()
        {
            if (IsBusy || string.IsNullOrWhiteSpace(SubscriptionUrl))
            {
                return;
            }

            IsBusy = true;
            ImportStatusMessage = string.Empty;
            try
            {
                await _profileService.AddOrUpdateFromSubscriptionAsync(SubscriptionUrl.Trim());
                ImportStatusMessage = _localizedStrings["WelcomeImportSuccess"];
            }
            catch (Exception ex)
            {
                ImportStatusMessage = string.Format(_localizedStrings["WelcomeImportFailedFormat"], ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void RetryDownload()
        {
            if (CurrentPageIndex != DownloadPageIndex || IsDownloading)
            {
                return;
            }

            _downloadStarted = false;
            _ = StartDownloadIfNeededAsync();
        }

        [RelayCommand]
        private void Finish()
        {
            if (!CanFinish)
            {
                return;
            }

            _appSettingsService.WelcomeCompleted = true;
            Completed?.Invoke(this, EventArgs.Empty);
        }

        public async Task ImportLocalFileAsync(string filePath)
        {
            if (IsBusy || string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            IsBusy = true;
            ImportStatusMessage = string.Empty;
            try
            {
                await _profileService.ImportLocalFileAsync(filePath);
                ImportStatusMessage = _localizedStrings["WelcomeImportSuccess"];
            }
            catch (Exception ex)
            {
                ImportStatusMessage = string.Format(_localizedStrings["WelcomeImportFailedFormat"], ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        partial void OnCurrentPageIndexChanged(int value)
        {
            NotifyNavigationStateChanged();
            if (value == DownloadPageIndex)
            {
                _ = StartDownloadIfNeededAsync();
            }
        }

        partial void OnSelectedLanguageTagChanged(string value)
        {
            _localizedStrings.SetLanguage(value);
        }

        partial void OnSelectedAppThemeTagChanged(string value)
        {
            if (_isUpdatingThemeSelection)
            {
                return;
            }

            _themeService.ApplyAppTheme(MapTagToAppTheme(value));
            _isUpdatingThemeSelection = true;
            SelectedAppThemeTag = MapAppThemeToTag(_themeService.CurrentAppTheme);
            _isUpdatingThemeSelection = false;
        }

        partial void OnSelectedBackdropTagChanged(string value)
        {
            if (_isUpdatingThemeSelection)
            {
                return;
            }

            bool applied = _themeService.ApplyBackdrop(MapTagToBackdrop(value));
            if (!applied)
            {
                _isUpdatingThemeSelection = true;
                SelectedBackdropTag = MapBackdropToTag(_themeService.CurrentBackdrop);
                _isUpdatingThemeSelection = false;
            }
        }

        partial void OnIsOnlineKernelSelectedChanged(bool value)
        {
            KernelStatusMessage = string.Empty;
            NotifyNavigationStateChanged();
        }

        partial void OnCustomKernelPathInputChanged(string value)
        {
            KernelStatusMessage = string.Empty;
            NotifyNavigationStateChanged();
        }

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanImport));
            NotifyNavigationStateChanged();
        }

        partial void OnIsDownloadingChanged(bool value)
        {
            NotifyNavigationStateChanged();
        }

        partial void OnDownloadCompletedChanged(bool value)
        {
            NotifyNavigationStateChanged();
        }

        partial void OnDownloadFailedChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowRetryButton));
        }

        private async Task StartDownloadIfNeededAsync()
        {
            if (_downloadStarted || CurrentPageIndex != DownloadPageIndex)
            {
                return;
            }

            _downloadStarted = true;
            DownloadCompleted = false;
            DownloadFailed = false;
            SetDownloadProgress(0);
            IsDownloading = true;

            try
            {
                if (IsOnlineKernelSelected)
                {
                    DownloadStatusMessage = _localizedStrings["WelcomeDownloadKernelStatus"];
                    var kernelProgress = new Progress<DownloadProgressReport>(
                        report => ApplyLinearDownloadProgress(report, stageStart: 0, stageEnd: 50));
                    bool kernelReady = await _kernelBootstrapService.EnsureKernelReadyAsync(kernelProgress);
                    if (!kernelReady)
                    {
                        MarkDownloadFailed(_localizedStrings["WelcomeDownloadKernelFailed"]);
                        return;
                    }

                    SetDownloadProgress(50);
                    DownloadStatusMessage = _localizedStrings["WelcomeDownloadGeoDataStatus"];
                    var geoDataProgress = new Progress<DownloadProgressReport>(
                        report => ApplyGeoDataDownloadProgress(report, stageStart: 50, stageEnd: 100));
                    var geoDataResult = await _geoDataService.EnsureGeoDataReadyAsync(geoDataProgress);
                    if (!geoDataResult.Success)
                    {
                        MarkDownloadFailed(string.Format(_localizedStrings["WelcomeDownloadGeoDataFailedFormat"], geoDataResult.Details));
                        return;
                    }
                }
                else
                {
                    DownloadStatusMessage = _localizedStrings["WelcomeDownloadGeoDataStatus"];
                    var geoDataProgress = new Progress<DownloadProgressReport>(
                        report => ApplyGeoDataDownloadProgress(report, stageStart: 0, stageEnd: 100));
                    var geoDataResult = await _geoDataService.EnsureGeoDataReadyAsync(geoDataProgress);
                    if (!geoDataResult.Success)
                    {
                        MarkDownloadFailed(string.Format(_localizedStrings["WelcomeDownloadGeoDataFailedFormat"], geoDataResult.Details));
                        return;
                    }
                }

                SetDownloadProgress(100);
                DownloadCompleted = true;
                DownloadStatusMessage = _localizedStrings["WelcomeDownloadCompleted"];
            }
            catch (Exception ex)
            {
                MarkDownloadFailed(string.Format(_localizedStrings["WelcomeDownloadFailedFormat"], ex.Message));
            }
            finally
            {
                IsDownloading = false;
            }
        }

        private void MarkDownloadFailed(string message)
        {
            DownloadFailed = true;
            DownloadStatusMessage = message;
        }

        private void ApplyLinearDownloadProgress(DownloadProgressReport report, double stageStart, double stageEnd)
        {
            if (report.IsIndeterminate)
            {
                SetDownloadProgress(stageStart);
                return;
            }

            double stageProgress = Math.Clamp(report.Percentage, 0, 100) / 100d;
            SetDownloadProgress(stageStart + ((stageEnd - stageStart) * stageProgress));
        }

        private void ApplyGeoDataDownloadProgress(DownloadProgressReport report, double stageStart, double stageEnd)
        {
            int assetIndex = Array.FindIndex(
                GeoDataAssetNames,
                assetName => string.Equals(assetName, report.FileName, StringComparison.OrdinalIgnoreCase));
            if (assetIndex < 0)
            {
                ApplyLinearDownloadProgress(report, stageStart, stageEnd);
                return;
            }

            double perAssetRange = (stageEnd - stageStart) / GeoDataAssetNames.Length;
            double assetStart = stageStart + (perAssetRange * assetIndex);
            if (report.IsIndeterminate)
            {
                SetDownloadProgress(assetStart);
                return;
            }

            double assetProgress = Math.Clamp(report.Percentage, 0, 100) / 100d;
            SetDownloadProgress(assetStart + (perAssetRange * assetProgress));
        }

        private void SetDownloadProgress(double value)
        {
            double clamped = Math.Clamp(value, 0, 100);
            DownloadProgressValue = clamped;
            DownloadProgressText = FormatProgressText(clamped);
        }

        private static string FormatProgressText(double value)
        {
            return $"{Math.Round(Math.Clamp(value, 0, 100), MidpointRounding.AwayFromZero):0}%";
        }

        private bool ApplyKernelSelection()
        {
            if (IsOnlineKernelSelected)
            {
                _kernelPathService.SetCustomKernelPath(null);
                KernelStatusMessage = string.Empty;
                return true;
            }

            if (!TryResolveKernelPathInput(CustomKernelPathInput, out string resolvedPath) || !File.Exists(resolvedPath))
            {
                KernelStatusMessage = _localizedStrings["WelcomeKernelCustomPathInvalid"];
                NotifyNavigationStateChanged();
                return false;
            }

            _kernelPathService.SetCustomKernelPath(resolvedPath);
            CustomKernelPathInput = resolvedPath;
            KernelStatusMessage = string.Empty;
            return true;
        }

        private bool CanUseSelectedKernel()
        {
            if (IsBusy || IsDownloading)
            {
                return false;
            }

            return IsOnlineKernelSelected
                || (TryResolveKernelPathInput(CustomKernelPathInput, out string resolvedPath) && File.Exists(resolvedPath));
        }

        private static bool TryResolveKernelPathInput(string? input, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            try
            {
                string trimmed = input.Trim().Trim('"');
                string fullPath = Path.GetFullPath(trimmed);
                resolvedPath = Directory.Exists(fullPath)
                    ? Path.Combine(fullPath, KernelExecutableName)
                    : fullPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void NotifyNavigationStateChanged()
        {
            OnPropertyChanged(nameof(ShowBackButton));
            OnPropertyChanged(nameof(ShowNextButton));
            OnPropertyChanged(nameof(ShowSkipButton));
            OnPropertyChanged(nameof(ShowFinishButton));
            OnPropertyChanged(nameof(ShowRetryButton));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanSkip));
            OnPropertyChanged(nameof(CanFinish));
        }

        private static AppThemeMode MapTagToAppTheme(string? tag)
        {
            return tag switch
            {
                ThemeDark => AppThemeMode.Dark,
                _ => AppThemeMode.Light,
            };
        }

        private static string MapAppThemeToTag(AppThemeMode mode)
        {
            return mode switch
            {
                AppThemeMode.Dark => ThemeDark,
                _ => ThemeLight,
            };
        }

        private static BackdropMode MapTagToBackdrop(string? tag)
        {
            return tag switch
            {
                BackdropMicaAlt => BackdropMode.MicaAlt,
                BackdropAcrylic => BackdropMode.Acrylic,
                _ => BackdropMode.Mica,
            };
        }

        private static string MapBackdropToTag(BackdropMode mode)
        {
            return mode switch
            {
                BackdropMode.MicaAlt => BackdropMicaAlt,
                BackdropMode.Acrylic => BackdropAcrylic,
                _ => BackdropMica,
            };
        }
    }
}

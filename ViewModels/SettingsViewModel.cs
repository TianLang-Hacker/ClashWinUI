using ClashWinUI.Common;
using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        public const string ThemeSystem = "system";
        public const string ThemeLight = "light";
        public const string ThemeDark = "dark";

        public const string BackdropMica = "mica";
        public const string BackdropMicaAlt = "mica_alt";
        public const string BackdropAcrylic = "acrylic";

        public const string CloseBehaviorMinimizeToTray = "minimize_to_tray";
        public const string CloseBehaviorExit = "exit";

        public const string ModeRule = "rule";
        public const string ModeGlobal = "global";
        public const string ModeDirect = "direct";

        public const string LogDebug = "debug";
        public const string LogInfo = "info";
        public const string LogWarning = "warning";
        public const string LogError = "error";

        private readonly LocalizedStrings _localizedStrings;
        private readonly IThemeService _themeService;
        private readonly IKernelPathService _kernelPathService;
        private readonly IAppSettingsService _appSettingsService;
        private readonly IProfileService _profileService;
        private readonly IConfigService _configService;
        private readonly IMihomoService _mihomoService;
        private readonly IGeoDataService _geoDataService;
        private readonly IProcessService _processService;
        private readonly ISystemProxyService _systemProxyService;
        private readonly SemaphoreSlim _mixinApplySemaphore = new(1, 1);

        private bool _isUpdatingFromLocalization;
        private bool _isUpdatingFromThemeService;
        private bool _isUpdatingFromAppSettings;
        private bool _isUpdatingMixinInputs;
        private int _mixinApplyRequestVersion;
        private ProfileItem? _activeMixinProfile;
        private MixinSettings _currentMixinSettings = new();

        [ObservableProperty]
        public partial string Title { get; set; }

        [ObservableProperty]
        public partial string SelectedLanguageTag { get; set; }

        [ObservableProperty]
        public partial string SelectedAppThemeTag { get; set; }

        [ObservableProperty]
        public partial string SelectedBackdropTag { get; set; }

        [ObservableProperty]
        public partial string KernelPathInput { get; set; }

        [ObservableProperty]
        public partial string SelectedCloseBehaviorTag { get; set; }

        [ObservableProperty]
        public partial bool ProxyGroupsExpandedByDefault { get; set; }

        [ObservableProperty]
        public partial bool HasActiveMixinProfile { get; set; }

        [ObservableProperty]
        public partial string CurrentMixinProfileName { get; set; }

        [ObservableProperty]
        public partial string CurrentMixinWorkspacePath { get; set; }

        [ObservableProperty]
        public partial string MixinStatusMessage { get; set; }

        [ObservableProperty]
        public partial bool IsUpdatingGeoData { get; set; }

        [ObservableProperty]
        public partial string GeoDataStatusMessage { get; set; }

        [ObservableProperty]
        public partial string MixedPortInput { get; set; }

        [ObservableProperty]
        public partial string HttpPortInput { get; set; }

        [ObservableProperty]
        public partial string SocksPortInput { get; set; }

        [ObservableProperty]
        public partial string RedirPortInput { get; set; }

        [ObservableProperty]
        public partial string TProxyPortInput { get; set; }

        [ObservableProperty]
        public partial bool TunEnabled { get; set; }

        [ObservableProperty]
        public partial bool AllowLanEnabled { get; set; }

        [ObservableProperty]
        public partial bool Ipv6Enabled { get; set; }

        [ObservableProperty]
        public partial string SelectedModeTag { get; set; }

        [ObservableProperty]
        public partial string SelectedLogLevelTag { get; set; }

        public SettingsViewModel(
            LocalizedStrings localizedStrings,
            IThemeService themeService,
            IKernelPathService kernelPathService,
            IAppSettingsService appSettingsService,
            IProfileService profileService,
            IConfigService configService,
            IMihomoService mihomoService,
            IGeoDataService geoDataService,
            IProcessService processService,
            ISystemProxyService systemProxyService)
        {
            _localizedStrings = localizedStrings;
            _themeService = themeService;
            _kernelPathService = kernelPathService;
            _appSettingsService = appSettingsService;
            _profileService = profileService;
            _configService = configService;
            _mihomoService = mihomoService;
            _geoDataService = geoDataService;
            _processService = processService;
            _systemProxyService = systemProxyService;

            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;
            _appSettingsService.SettingsChanged += OnAppSettingsChanged;

            Title = _localizedStrings["PageSettings"];
            SelectedLanguageTag = _localizedStrings.CurrentLanguage;
            SelectedAppThemeTag = MapAppThemeToTag(_themeService.CurrentAppTheme);
            SelectedBackdropTag = MapBackdropToTag(_themeService.CurrentBackdrop);
            KernelPathInput = _kernelPathService.CustomKernelPath ?? _kernelPathService.DefaultKernelPath;
            SelectedCloseBehaviorTag = MapCloseBehaviorToTag(_appSettingsService.CloseBehavior);
            ProxyGroupsExpandedByDefault = _appSettingsService.ProxyGroupsExpandedByDefault;

            HasActiveMixinProfile = false;
            CurrentMixinProfileName = string.Empty;
            CurrentMixinWorkspacePath = string.Empty;
            MixinStatusMessage = string.Empty;
            GeoDataStatusMessage = GeoDataStatusTextHelper.BuildSettingsStatusMessage(_localizedStrings, _geoDataService.LastResult);
            MixedPortInput = "0";
            HttpPortInput = "0";
            SocksPortInput = "0";
            RedirPortInput = "0";
            TProxyPortInput = "0";
            SelectedModeTag = ModeRule;
            SelectedLogLevelTag = LogInfo;

            RefreshActiveProfileState();
        }

        [RelayCommand]
        private void SaveKernelPath()
        {
            _kernelPathService.SetCustomKernelPath(KernelPathInput);
            KernelPathInput = _kernelPathService.CustomKernelPath ?? _kernelPathService.DefaultKernelPath;
        }

        [RelayCommand(CanExecute = nameof(CanModifyMixin))]
        private void OpenMixinFolder()
        {
            if (_activeMixinProfile is null || string.IsNullOrWhiteSpace(CurrentMixinWorkspacePath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = CurrentMixinWorkspacePath,
                UseShellExecute = true,
            });
        }

        public void RefreshActiveProfileState()
        {
            GeoDataStatusMessage = GeoDataStatusTextHelper.BuildSettingsStatusMessage(_localizedStrings, _geoDataService.LastResult);
            _activeMixinProfile = _profileService.GetActiveProfile();
            HasActiveMixinProfile = _activeMixinProfile is not null;
            OpenMixinFolderCommand.NotifyCanExecuteChanged();

            if (_activeMixinProfile is null)
            {
                CurrentMixinProfileName = _localizedStrings["ProfilesNoActive"];
                CurrentMixinWorkspacePath = string.Empty;
                MixinStatusMessage = _localizedStrings["SettingsMixinNoActiveProfile"];
                ResetMixinInputs();
                return;
            }

            try
            {
                ProfileConfigWorkspace workspace = _configService.EnsureWorkspace(_activeMixinProfile);
                MixinSettings settings = _configService.LoadMixin(_activeMixinProfile);

                CurrentMixinProfileName = _activeMixinProfile.DisplayName;
                CurrentMixinWorkspacePath = workspace.DirectoryPath;
                MixinStatusMessage = string.Empty;
                _currentMixinSettings = CloneMixinSettings(settings);
                ApplyMixinSettings(settings);
            }
            catch (Exception ex)
            {
                CurrentMixinProfileName = _activeMixinProfile.DisplayName;
                CurrentMixinWorkspacePath = _activeMixinProfile.WorkspaceDirectory;
                MixinStatusMessage = string.Format(_localizedStrings["SettingsMixinStatusLoadFailed"], ex.Message);
                _currentMixinSettings = new MixinSettings();
                ResetMixinInputs();
            }
        }

        [RelayCommand(CanExecute = nameof(CanUpdateGeoData))]
        private async Task UpdateGeoDataAsync()
        {
            if (IsUpdatingGeoData)
            {
                return;
            }

            IsUpdatingGeoData = true;
            GeoDataStatusMessage = string.Empty;
            try
            {
                GeoDataOperationResult result = await _geoDataService.UpdateGeoDataAsync();
                GeoDataStatusMessage = GeoDataStatusTextHelper.BuildSettingsStatusMessage(_localizedStrings, result);
            }
            finally
            {
                IsUpdatingGeoData = false;
            }
        }

        partial void OnSelectedLanguageTagChanged(string value)
        {
            if (_isUpdatingFromLocalization)
            {
                return;
            }

            _localizedStrings.SetLanguage(value);
        }

        partial void OnSelectedAppThemeTagChanged(string value)
        {
            if (_isUpdatingFromThemeService)
            {
                return;
            }

            if (!TryMapTagToAppTheme(value, out AppThemeMode mode))
            {
                return;
            }

            _themeService.ApplyAppTheme(mode);
            _isUpdatingFromThemeService = true;
            SelectedAppThemeTag = MapAppThemeToTag(_themeService.CurrentAppTheme);
            _isUpdatingFromThemeService = false;
        }

        partial void OnSelectedBackdropTagChanged(string value)
        {
            if (_isUpdatingFromThemeService)
            {
                return;
            }

            if (!TryMapTagToBackdrop(value, out BackdropMode mode))
            {
                return;
            }

            bool applied = _themeService.ApplyBackdrop(mode);
            if (!applied)
            {
                _isUpdatingFromThemeService = true;
                SelectedBackdropTag = MapBackdropToTag(_themeService.CurrentBackdrop);
                _isUpdatingFromThemeService = false;
            }
        }

        partial void OnSelectedCloseBehaviorTagChanged(string value)
        {
            if (_isUpdatingFromAppSettings)
            {
                return;
            }

            if (!TryMapTagToCloseBehavior(value, out CloseBehavior closeBehavior))
            {
                return;
            }

            _appSettingsService.CloseBehavior = closeBehavior;
            _isUpdatingFromAppSettings = true;
            SelectedCloseBehaviorTag = MapCloseBehaviorToTag(_appSettingsService.CloseBehavior);
            _isUpdatingFromAppSettings = false;
        }

        partial void OnProxyGroupsExpandedByDefaultChanged(bool value)
        {
            if (_isUpdatingFromAppSettings)
            {
                return;
            }

            _appSettingsService.ProxyGroupsExpandedByDefault = value;

            _isUpdatingFromAppSettings = true;
            ProxyGroupsExpandedByDefault = _appSettingsService.ProxyGroupsExpandedByDefault;
            _isUpdatingFromAppSettings = false;
        }

        partial void OnTunEnabledChanged(bool value)
        {
            QueueImmediateMixinApply();
        }

        partial void OnAllowLanEnabledChanged(bool value)
        {
            QueueImmediateMixinApply();
        }

        partial void OnIpv6EnabledChanged(bool value)
        {
            QueueImmediateMixinApply();
        }

        partial void OnSelectedModeTagChanged(string value)
        {
            QueueImmediateMixinApply();
        }

        partial void OnSelectedLogLevelTagChanged(string value)
        {
            QueueImmediateMixinApply();
        }

        partial void OnIsUpdatingGeoDataChanged(bool value)
        {
            UpdateGeoDataCommand.NotifyCanExecuteChanged();
        }

        private bool CanModifyMixin()
        {
            return HasActiveMixinProfile;
        }

        private bool CanUpdateGeoData()
        {
            return !IsUpdatingGeoData;
        }

        private void ApplyMixinSettings(MixinSettings settings)
        {
            _isUpdatingMixinInputs = true;
            MixedPortInput = FormatPort(settings.MixedPort);
            HttpPortInput = FormatPort(settings.HttpPort);
            SocksPortInput = FormatPort(settings.SocksPort);
            RedirPortInput = FormatPort(settings.RedirPort);
            TProxyPortInput = FormatPort(settings.TProxyPort);
            TunEnabled = settings.TunEnabled;
            AllowLanEnabled = settings.AllowLan;
            Ipv6Enabled = settings.Ipv6Enabled;
            SelectedModeTag = NormalizeModeTag(settings.Mode);
            SelectedLogLevelTag = NormalizeLogLevelTag(settings.LogLevel);
            _isUpdatingMixinInputs = false;
        }

        private MixinSettings BuildMixinSettingsFromInputs()
        {
            return new MixinSettings
            {
                MixedPort = ParsePort(MixedPortInput),
                HttpPort = ParsePort(HttpPortInput),
                SocksPort = ParsePort(SocksPortInput),
                RedirPort = ParsePort(RedirPortInput),
                TProxyPort = ParsePort(TProxyPortInput),
                TunEnabled = TunEnabled,
                AllowLan = AllowLanEnabled,
                Ipv6Enabled = Ipv6Enabled,
                Mode = NormalizeModeTag(SelectedModeTag),
                LogLevel = NormalizeLogLevelTag(SelectedLogLevelTag),
            };
        }

        private void ResetMixinInputs()
        {
            ApplyMixinSettings(new MixinSettings());
        }

        public PortSettingsDraft CreatePortSettingsDraft()
        {
            return new PortSettingsDraft
            {
                MixedPortInput = MixedPortInput,
                HttpPortInput = HttpPortInput,
                SocksPortInput = SocksPortInput,
                RedirPortInput = RedirPortInput,
                TProxyPortInput = TProxyPortInput,
            };
        }

        public async Task<(bool Success, string Message)> ApplyPortSettingsDraftAsync(PortSettingsDraft draft)
        {
            if (_activeMixinProfile is null)
            {
                string message = _localizedStrings["SettingsMixinNoActiveProfile"];
                MixinStatusMessage = message;
                return (false, message);
            }

            MixinSettings settings = CloneMixinSettings(_currentMixinSettings);
            settings.MixedPort = ParsePort(draft.MixedPortInput);
            settings.HttpPort = ParsePort(draft.HttpPortInput);
            settings.SocksPort = ParsePort(draft.SocksPortInput);
            settings.RedirPort = ParsePort(draft.RedirPortInput);
            settings.TProxyPort = ParsePort(draft.TProxyPortInput);

            await _mixinApplySemaphore.WaitAsync();
            try
            {
                return await SaveAndApplyMixinSettingsAsync(settings, rollbackVisibleInputsOnFailure: false);
            }
            finally
            {
                _mixinApplySemaphore.Release();
            }
        }

        private static string FormatPort(int? value)
        {
            return value.HasValue && value.Value > 0
                ? value.Value.ToString()
                : "0";
        }

        private static int? ParsePort(string? raw)
        {
            if (!int.TryParse(raw?.Trim(), out int value))
            {
                return null;
            }

            return value > 0 && value <= 65535 ? value : null;
        }

        private static string NormalizeModeTag(string? tag)
        {
            return tag?.Trim().ToLowerInvariant() switch
            {
                ModeGlobal => ModeGlobal,
                ModeDirect => ModeDirect,
                _ => ModeRule,
            };
        }

        private static string NormalizeLogLevelTag(string? tag)
        {
            return tag?.Trim().ToLowerInvariant() switch
            {
                LogDebug => LogDebug,
                LogWarning => LogWarning,
                LogError => LogError,
                _ => LogInfo,
            };
        }

        private void QueueImmediateMixinApply()
        {
            if (_isUpdatingMixinInputs || _activeMixinProfile is null || !HasActiveMixinProfile)
            {
                return;
            }

            int requestVersion = Interlocked.Increment(ref _mixinApplyRequestVersion);
            _ = ProcessImmediateMixinApplyAsync(requestVersion);
        }

        private async Task ProcessImmediateMixinApplyAsync(int requestVersion)
        {
            await _mixinApplySemaphore.WaitAsync();
            try
            {
                if (_activeMixinProfile is null || _isUpdatingMixinInputs)
                {
                    return;
                }

                if (requestVersion != _mixinApplyRequestVersion)
                {
                    return;
                }

                MixinSettings settings = BuildMixinSettingsFromInputs();
                await SaveAndApplyMixinSettingsAsync(settings, rollbackVisibleInputsOnFailure: true);
            }
            finally
            {
                _mixinApplySemaphore.Release();
            }
        }

        private async Task<(bool Success, string Message)> SaveAndApplyMixinSettingsAsync(
            MixinSettings nextSettings,
            bool rollbackVisibleInputsOnFailure)
        {
            if (_activeMixinProfile is null)
            {
                string message = _localizedStrings["SettingsMixinNoActiveProfile"];
                MixinStatusMessage = message;
                return (false, message);
            }

            if (MixinSettingsEquals(_currentMixinSettings, nextSettings))
            {
                return (true, string.Empty);
            }

            MixinSettings previousSettings = CloneMixinSettings(_currentMixinSettings);
            try
            {
                _configService.SaveMixin(_activeMixinProfile, nextSettings);
                string runtimePath = _configService.BuildRuntime(_activeMixinProfile);
                bool applied = await _mihomoService.ApplyConfigAsync(runtimePath);
                if (!applied)
                {
                    RestorePersistedMixinSettings(previousSettings);
                    if (rollbackVisibleInputsOnFailure)
                    {
                        ApplyMixinSettings(previousSettings);
                    }

                    string failedMessage = GeoDataStatusTextHelper.TryBuildControllerFailureMessage(
                        _localizedStrings,
                        _processService,
                        _geoDataService,
                        out string geoDataMessage)
                        ? geoDataMessage
                        : _localizedStrings["SettingsMixinStatusApplyFailed"];
                    MixinStatusMessage = failedMessage;
                    return (false, failedMessage);
                }

                int proxyPort = _processService.ResolveProxyPort(runtimePath);
                await _systemProxyService.EnableAsync("127.0.0.1", proxyPort, AppConstants.SystemProxyBypassList);

                _currentMixinSettings = CloneMixinSettings(nextSettings);
                ApplyMixinSettings(_currentMixinSettings);

                string successMessage = _localizedStrings["SettingsMixinStatusApplied"];
                MixinStatusMessage = successMessage;
                return (true, successMessage);
            }
            catch (Exception ex)
            {
                RestorePersistedMixinSettings(previousSettings);
                if (rollbackVisibleInputsOnFailure)
                {
                    ApplyMixinSettings(previousSettings);
                }

                string failedMessage = string.Format(_localizedStrings["SettingsMixinStatusLoadFailed"], ex.Message);
                MixinStatusMessage = failedMessage;
                return (false, failedMessage);
            }
        }

        private void RestorePersistedMixinSettings(MixinSettings settings)
        {
            if (_activeMixinProfile is null)
            {
                return;
            }

            try
            {
                _configService.SaveMixin(_activeMixinProfile, settings);
                _configService.BuildRuntime(_activeMixinProfile);
            }
            catch
            {
                // Best-effort rollback to keep the workspace aligned with the last known good settings.
            }
        }

        private static MixinSettings CloneMixinSettings(MixinSettings settings)
        {
            return new MixinSettings
            {
                MixedPort = settings.MixedPort,
                HttpPort = settings.HttpPort,
                SocksPort = settings.SocksPort,
                RedirPort = settings.RedirPort,
                TProxyPort = settings.TProxyPort,
                TunEnabled = settings.TunEnabled,
                AllowLan = settings.AllowLan,
                Mode = NormalizeModeTag(settings.Mode),
                LogLevel = NormalizeLogLevelTag(settings.LogLevel),
                Ipv6Enabled = settings.Ipv6Enabled,
            };
        }

        private static bool MixinSettingsEquals(MixinSettings left, MixinSettings right)
        {
            return left.MixedPort == right.MixedPort
                && left.HttpPort == right.HttpPort
                && left.SocksPort == right.SocksPort
                && left.RedirPort == right.RedirPort
                && left.TProxyPort == right.TProxyPort
                && left.TunEnabled == right.TunEnabled
                && left.AllowLan == right.AllowLan
                && left.Ipv6Enabled == right.Ipv6Enabled
                && string.Equals(NormalizeModeTag(left.Mode), NormalizeModeTag(right.Mode), StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeLogLevelTag(left.LogLevel), NormalizeLogLevelTag(right.LogLevel), StringComparison.OrdinalIgnoreCase);
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(LocalizedStrings.CurrentLanguage) && e.PropertyName != "Item[]")
            {
                return;
            }

            Title = _localizedStrings["PageSettings"];
            if (!string.Equals(SelectedLanguageTag, _localizedStrings.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                _isUpdatingFromLocalization = true;
                SelectedLanguageTag = _localizedStrings.CurrentLanguage;
                _isUpdatingFromLocalization = false;
            }

            if (!HasActiveMixinProfile)
            {
                MixinStatusMessage = _localizedStrings["SettingsMixinNoActiveProfile"];
            }

            GeoDataStatusMessage = GeoDataStatusTextHelper.BuildSettingsStatusMessage(_localizedStrings, _geoDataService.LastResult);
        }

        private void OnAppSettingsChanged(object? sender, EventArgs e)
        {
            string behaviorTag = MapCloseBehaviorToTag(_appSettingsService.CloseBehavior);
            bool closeBehaviorMatches = string.Equals(
                SelectedCloseBehaviorTag,
                behaviorTag,
                StringComparison.OrdinalIgnoreCase);
            bool expansionMatches = ProxyGroupsExpandedByDefault == _appSettingsService.ProxyGroupsExpandedByDefault;

            if (closeBehaviorMatches && expansionMatches)
            {
                return;
            }

            _isUpdatingFromAppSettings = true;
            SelectedCloseBehaviorTag = behaviorTag;
            ProxyGroupsExpandedByDefault = _appSettingsService.ProxyGroupsExpandedByDefault;
            _isUpdatingFromAppSettings = false;
        }

        private static bool TryMapTagToAppTheme(string tag, out AppThemeMode mode)
        {
            mode = tag switch
            {
                ThemeLight => AppThemeMode.Light,
                ThemeDark => AppThemeMode.Dark,
                _ => AppThemeMode.System,
            };

            return true;
        }

        private static string MapAppThemeToTag(AppThemeMode mode)
        {
            return mode switch
            {
                AppThemeMode.Light => ThemeLight,
                AppThemeMode.Dark => ThemeDark,
                _ => ThemeSystem,
            };
        }

        private static bool TryMapTagToBackdrop(string tag, out BackdropMode mode)
        {
            mode = tag switch
            {
                BackdropMicaAlt => BackdropMode.MicaAlt,
                BackdropAcrylic => BackdropMode.Acrylic,
                _ => BackdropMode.Mica,
            };

            return true;
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

        private static bool TryMapTagToCloseBehavior(string tag, out CloseBehavior closeBehavior)
        {
            closeBehavior = tag switch
            {
                CloseBehaviorExit => CloseBehavior.Exit,
                _ => CloseBehavior.MinimizeToTray,
            };

            return true;
        }

        private static string MapCloseBehaviorToTag(CloseBehavior closeBehavior)
        {
            return closeBehavior switch
            {
                CloseBehavior.Exit => CloseBehaviorExit,
                _ => CloseBehaviorMinimizeToTray,
            };
        }
    }
}

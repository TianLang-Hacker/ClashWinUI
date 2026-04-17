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
    public partial class SettingsViewModel : ObservableObject, IDisposable
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
        private readonly ITunService _tunService;
        private readonly ISystemProxyService _systemProxyService;
        private readonly IUpdateService _updateService;
        private readonly IHomeOverviewSamplerService _homeOverviewSamplerService;
        private readonly SemaphoreSlim _mixinApplySemaphore = new(1, 1);

        private CancellationTokenSource? _profileStateLoadCancellation;
        private bool _isUpdatingFromLocalization;
        private bool _isUpdatingFromThemeService;
        private bool _isUpdatingFromAppSettings;
        private bool _isUpdatingMixinInputs;
        private int _mixinApplyRequestVersion;
        private int _profileStateLoadVersion;
        private ProfileItem? _activeMixinProfile;
        private MixinSettings _currentMixinSettings = new();
        private SystemProxyState _currentSystemProxyState = SystemProxyState.Disabled();
        private TunRuntimeStatus _currentTunRuntimeStatus = TunRuntimeStatus.Disabled();
        private bool _isDisposed;

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
        public partial string TunRuntimeStatusText { get; set; }

        [ObservableProperty]
        public partial string TunRuntimeSummaryText { get; set; }

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

        [ObservableProperty]
        public partial string UpdateStatusHeader { get; set; }

        [ObservableProperty]
        public partial bool IsUpdatingApp { get; set; }

        [ObservableProperty]
        public partial string CurrentAppVersionText { get; set; }

        [ObservableProperty]
        public partial bool IsCheckingForUpdates { get; set; }

        [ObservableProperty]
        public partial bool ShowUpdateDownloadProgress { get; set; }

        [ObservableProperty]
        public partial bool IsUpdateDownloadProgressIndeterminate { get; set; }

        [ObservableProperty]
        public partial double UpdateDownloadProgressValue { get; set; }

        [ObservableProperty]
        public partial string UpdateDownloadProgressText { get; set; }

        [ObservableProperty]
        public partial bool IsLoadingProfileState { get; set; }

        public IThemeService ThemeService => _themeService;

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
            ITunService tunService,
            ISystemProxyService systemProxyService,
            IUpdateService updateService,
            IHomeOverviewSamplerService homeOverviewSamplerService)
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
            _tunService = tunService;
            _systemProxyService = systemProxyService;
            _updateService = updateService;
            _homeOverviewSamplerService = homeOverviewSamplerService;

            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;
            _appSettingsService.SettingsChanged += OnAppSettingsChanged;
            _updateService.StateChanged += OnUpdateServiceStateChanged;

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
            TunRuntimeStatusText = string.Empty;
            TunRuntimeSummaryText = string.Empty;
            GeoDataStatusMessage = GeoDataStatusTextHelper.BuildSettingsStatusMessage(_localizedStrings, _geoDataService.LastResult);
            MixedPortInput = "0";
            HttpPortInput = "0";
            SocksPortInput = "0";
            RedirPortInput = "0";
            TProxyPortInput = "0";
            SelectedModeTag = ModeRule;
            SelectedLogLevelTag = LogInfo;
            UpdateStatusHeader = string.Empty;
            IsUpdatingApp = false;
            CurrentAppVersionText = string.Empty;
            IsCheckingForUpdates = false;
            ShowUpdateDownloadProgress = false;
            IsUpdateDownloadProgressIndeterminate = false;
            UpdateDownloadProgressValue = 0;
            UpdateDownloadProgressText = string.Empty;
            IsLoadingProfileState = false;

            ApplyOverviewSnapshot();
            RefreshUpdateState();
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            CancelProfileStateLoad();
            _localizedStrings.PropertyChanged -= OnLocalizedStringsPropertyChanged;
            _appSettingsService.SettingsChanged -= OnAppSettingsChanged;
            _updateService.StateChanged -= OnUpdateServiceStateChanged;
            _mixinApplySemaphore.Dispose();
        }

        public Task InitializeAsync()
        {
            if (_isDisposed)
            {
                return Task.CompletedTask;
            }

            CancelProfileStateLoad();

            _profileStateLoadCancellation = new CancellationTokenSource();
            int requestVersion = Interlocked.Increment(ref _profileStateLoadVersion);

            Stopwatch immediateStopwatch = Stopwatch.StartNew();
            ApplyImmediateProfileState();
            immediateStopwatch.Stop();
            PerformanceTraceHelper.LogElapsed(
                "settings init immediate",
                immediateStopwatch.Elapsed,
                TimeSpan.FromMilliseconds(16));

            return InitializeProfileStateAsync(requestVersion, _profileStateLoadCancellation.Token);
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

        [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
        private async Task CheckForUpdatesAsync()
        {
            if (IsUpdatingApp)
            {
                return;
            }

            await _updateService.CheckForUpdatesAsync(forceRefresh: true);
            if (_updateService.CurrentState.Status == UpdateStatus.UpdateAvailable)
            {
                await _updateService.DownloadAndInstallLatestAsync();
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

        partial void OnIsUpdatingAppChanged(bool value)
        {
            CheckForUpdatesCommand.NotifyCanExecuteChanged();
        }

        private bool CanModifyMixin()
        {
            return HasActiveMixinProfile;
        }

        private bool CanUpdateGeoData()
        {
            return !IsUpdatingGeoData;
        }

        private bool CanCheckForUpdates()
        {
            return !IsUpdatingApp;
        }

        private void ApplyImmediateProfileState()
        {
            GeoDataStatusMessage = GeoDataStatusTextHelper.BuildSettingsStatusMessage(_localizedStrings, _geoDataService.LastResult);
            IsLoadingProfileState = true;
            OpenMixinFolderCommand.NotifyCanExecuteChanged();
            ApplyOverviewSnapshot();

            if (!HasActiveMixinProfile && string.IsNullOrWhiteSpace(CurrentMixinProfileName))
            {
                CurrentMixinWorkspacePath = string.Empty;
                MixinStatusMessage = string.Empty;
                ResetMixinInputs();
            }
        }

        private async Task InitializeProfileStateAsync(int requestVersion, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (_isDisposed || cancellationToken.IsCancellationRequested || requestVersion != _profileStateLoadVersion)
            {
                return;
            }

            Stopwatch backgroundStopwatch = Stopwatch.StartNew();
            ActiveProfileLoadResult result;
            try
            {
                result = await Task.Run(() => LoadActiveProfileState(cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backgroundStopwatch.Stop();
            PerformanceTraceHelper.LogElapsed(
                "settings init background",
                backgroundStopwatch.Elapsed,
                TimeSpan.FromMilliseconds(120));

            if (_isDisposed || cancellationToken.IsCancellationRequested || requestVersion != _profileStateLoadVersion)
            {
                return;
            }

            Stopwatch applyStopwatch = Stopwatch.StartNew();
            ApplyLoadedProfileState(result);
            applyStopwatch.Stop();
            PerformanceTraceHelper.LogElapsed(
                "settings init apply",
                applyStopwatch.Elapsed,
                TimeSpan.FromMilliseconds(16));
        }

        private ActiveProfileLoadResult LoadActiveProfileState(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SystemProxyState systemProxyState = _systemProxyService.GetCurrentState();
            ProfileItem? activeProfile = _profileService.GetActiveProfile();
            if (activeProfile is null)
            {
                return ActiveProfileLoadResult.NoActive(systemProxyState, TunRuntimeStatus.Disabled());
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProfileConfigWorkspace workspace = _configService.EnsureWorkspace(activeProfile);
                cancellationToken.ThrowIfCancellationRequested();
                MixinSettings settings = _configService.LoadMixin(activeProfile);
                cancellationToken.ThrowIfCancellationRequested();
                TunRuntimeStatus tunRuntimeStatus = ResolveTunRuntimeStatus(activeProfile);

                return ActiveProfileLoadResult.FromSuccess(
                    activeProfile,
                    workspace.DirectoryPath,
                    settings,
                    systemProxyState,
                    tunRuntimeStatus);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                TunRuntimeStatus tunRuntimeStatus = ResolveTunRuntimeStatus(activeProfile);
                return ActiveProfileLoadResult.FromFailure(
                    activeProfile,
                    activeProfile.WorkspaceDirectory,
                    systemProxyState,
                    tunRuntimeStatus,
                    string.Format(_localizedStrings["SettingsMixinStatusLoadFailed"], ex.Message));
            }
        }

        private void ApplyLoadedProfileState(ActiveProfileLoadResult result)
        {
            IsLoadingProfileState = false;

            if (!result.HasActiveProfile)
            {
                ApplyNoActiveProfileState(result.SystemProxyState, result.TunRuntimeStatus);
                return;
            }

            _activeMixinProfile = result.Profile;
            HasActiveMixinProfile = true;
            CurrentMixinProfileName = result.Profile!.DisplayName;
            CurrentMixinWorkspacePath = result.WorkspacePath;
            OpenMixinFolderCommand.NotifyCanExecuteChanged();

            if (!result.Success)
            {
                _currentMixinSettings = new MixinSettings();
                ResetMixinInputs();
                MixinStatusMessage = result.Message;
                ApplyTunPresentation(result.TunRuntimeStatus, result.SystemProxyState);
                return;
            }

            _currentMixinSettings = CloneMixinSettings(result.Settings!);
            ApplyMixinSettings(result.Settings!);
            MixinStatusMessage = string.Empty;
            ApplyTunPresentation(result.TunRuntimeStatus, result.SystemProxyState);
        }

        private void ApplyNoActiveProfileState(SystemProxyState systemProxyState, TunRuntimeStatus tunRuntimeStatus)
        {
            _activeMixinProfile = null;
            HasActiveMixinProfile = false;
            CurrentMixinProfileName = _localizedStrings["ProfilesNoActive"];
            CurrentMixinWorkspacePath = string.Empty;
            MixinStatusMessage = _localizedStrings["SettingsMixinNoActiveProfile"];
            _currentMixinSettings = new MixinSettings();
            ResetMixinInputs();
            OpenMixinFolderCommand.NotifyCanExecuteChanged();
            ApplyTunPresentation(tunRuntimeStatus, systemProxyState);
        }

        private void ApplyOverviewSnapshot()
        {
            HomeOverviewState state = _homeOverviewSamplerService.GetState();
            ApplyTunPresentation(state.TunRuntimeStatus, state.SystemProxyState);
        }

        private void ApplyTunPresentation(TunRuntimeStatus runtimeStatus, SystemProxyState systemProxyState)
        {
            _currentTunRuntimeStatus = runtimeStatus;
            _currentSystemProxyState = systemProxyState;

            (TunRuntimeStatusText, TunRuntimeSummaryText) = RuntimeNetworkStatusTextHelper.BuildTunPresentation(
                _localizedStrings,
                runtimeStatus,
                systemProxyState);
        }

        private async Task RefreshTunRuntimeStatusAsync()
        {
            ProfileItem? activeProfile = _activeMixinProfile;

            Stopwatch stopwatch = Stopwatch.StartNew();
            (SystemProxyState SystemProxyState, TunRuntimeStatus TunRuntimeStatus) snapshot = await Task.Run(() =>
            {
                SystemProxyState systemProxyState = _systemProxyService.GetCurrentState();
                TunRuntimeStatus runtimeStatus = ResolveTunRuntimeStatus(activeProfile);
                return (systemProxyState, runtimeStatus);
            });
            stopwatch.Stop();

            if (_isDisposed)
            {
                return;
            }

            ApplyTunPresentation(snapshot.TunRuntimeStatus, snapshot.SystemProxyState);
            PerformanceTraceHelper.LogElapsed(
                "settings tun diagnostics refresh",
                stopwatch.Elapsed,
                TimeSpan.FromMilliseconds(120));
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
            if (_isUpdatingMixinInputs || IsLoadingProfileState || _activeMixinProfile is null || !HasActiveMixinProfile)
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
                await RefreshTunRuntimeStatusAsync();
                return (true, string.Empty);
            }

            MixinSettings previousSettings = CloneMixinSettings(_currentMixinSettings);
            if (nextSettings.TunEnabled)
            {
                TunPreparationResult tunPreparation = _tunService.ValidateEnvironment(_kernelPathService.ResolveKernelPath());
                if (!tunPreparation.Success)
                {
                    if (rollbackVisibleInputsOnFailure)
                    {
                        ApplyMixinSettings(previousSettings);
                    }

                    string failedMessage = BuildTunPreparationFailureMessage(tunPreparation);
                    MixinStatusMessage = failedMessage;
                    await RefreshTunRuntimeStatusAsync();
                    return (false, failedMessage);
                }
            }

            try
            {
                _configService.SaveMixin(_activeMixinProfile, nextSettings);
                string runtimePath = _configService.BuildRuntime(_activeMixinProfile);
                bool applied = await _mihomoService.ApplyConfigAsync(runtimePath);
                if (!applied)
                {
                    bool currentRuntimeUsesTarget = PathsEqual(_processService.CurrentConfigPath, runtimePath);
                    if (currentRuntimeUsesTarget)
                    {
                        await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                            _systemProxyService,
                            _processService,
                            _tunService,
                            runtimePath);
                    }

                    RestorePersistedMixinSettings(previousSettings, rebuildRuntime: !currentRuntimeUsesTarget);
                    if (rollbackVisibleInputsOnFailure)
                    {
                        ApplyMixinSettings(previousSettings);
                    }

                    string failedMessage = MihomoFailureTextHelper.TryBuildControllerFailureMessage(
                        _localizedStrings,
                        _processService,
                        _geoDataService,
                        _tunService,
                        runtimePath,
                        out string controllerMessage)
                        ? controllerMessage
                        : _localizedStrings["SettingsMixinStatusApplyFailed"];
                    MixinStatusMessage = failedMessage;
                    await RefreshTunRuntimeStatusAsync();
                    return (false, failedMessage);
                }

                await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                    _systemProxyService,
                    _processService,
                    _tunService,
                    runtimePath);

                _currentMixinSettings = CloneMixinSettings(nextSettings);
                ApplyMixinSettings(_currentMixinSettings);

                string successMessage = _localizedStrings["SettingsMixinStatusApplied"];
                MixinStatusMessage = successMessage;
                await RefreshTunRuntimeStatusAsync();
                return (true, successMessage);
            }
            catch (Exception ex)
            {
                RestorePersistedMixinSettings(previousSettings, rebuildRuntime: true);
                if (rollbackVisibleInputsOnFailure)
                {
                    ApplyMixinSettings(previousSettings);
                }

                string failedMessage = string.Format(_localizedStrings["SettingsMixinStatusLoadFailed"], ex.Message);
                MixinStatusMessage = failedMessage;
                await RefreshTunRuntimeStatusAsync();
                return (false, failedMessage);
            }
        }

        private void RestorePersistedMixinSettings(MixinSettings settings, bool rebuildRuntime)
        {
            if (_activeMixinProfile is null)
            {
                return;
            }

            try
            {
                _configService.SaveMixin(_activeMixinProfile, settings);
                if (rebuildRuntime)
                {
                    _configService.BuildRuntime(_activeMixinProfile);
                }
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

        private TunRuntimeStatus ResolveTunRuntimeStatus(ProfileItem? activeProfile)
        {
            try
            {
                string? currentConfigPath = _processService.CurrentConfigPath;
                if (!string.IsNullOrWhiteSpace(currentConfigPath) && _tunService.IsTunEnabled(currentConfigPath))
                {
                    return TunRuntimeDiagnosticHelper.ApplyPreferredKernelDiagnostic(
                        _processService,
                        _tunService.GetRuntimeStatus(currentConfigPath, _kernelPathService.ResolveKernelPath()));
                }

                if (activeProfile is null)
                {
                    return TunRuntimeStatus.Disabled();
                }

                string runtimePath = _configService.GetRuntimePath(activeProfile);
                if (!_tunService.IsTunEnabled(runtimePath))
                {
                    return TunRuntimeStatus.Disabled();
                }

                return TunRuntimeDiagnosticHelper.ApplyPreferredKernelDiagnostic(
                    _processService,
                    _tunService.GetRuntimeStatus(runtimePath, _kernelPathService.ResolveKernelPath()));
            }
            catch (Exception ex)
            {
                return new TunRuntimeStatus
                {
                    IsConfigured = true,
                    IsHealthy = false,
                    DriverLoaded = false,
                    DriverVersion = string.Empty,
                    AdapterPresent = false,
                    AdapterName = string.Empty,
                    RouteAttached = false,
                    EffectiveStack = string.Empty,
                    FirewallEnabled = WindowsFirewallHelper.IsAnyProfileEnabled(),
                    DnsHijackConfigured = false,
                    DnsManaged = false,
                    DnsAutoGenerated = false,
                    FailureKind = MihomoFailureKind.TunDependency,
                    Message = ex.Message,
                };
            }
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

        private string BuildTunPreparationFailureMessage(TunPreparationResult preparation)
        {
            string detail = string.IsNullOrWhiteSpace(preparation.Message)
                ? _localizedStrings["MihomoStatusUnknownReason"]
                : preparation.Message.Trim();

            return preparation.FailureKind switch
            {
                MihomoFailureKind.TunPermission => string.Format(_localizedStrings["TunStatusPermissionFailureFormat"], detail),
                MihomoFailureKind.TunDependency => string.Format(_localizedStrings["TunStatusDependencyFailureFormat"], detail),
                _ => string.Format(_localizedStrings["TunStatusControllerFailureFormat"], detail),
            };
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

            Title = _localizedStrings["PageSettings"];
            if (!string.Equals(SelectedLanguageTag, _localizedStrings.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                _isUpdatingFromLocalization = true;
                SelectedLanguageTag = _localizedStrings.CurrentLanguage;
                _isUpdatingFromLocalization = false;
            }

            if (!HasActiveMixinProfile && !IsLoadingProfileState)
            {
                CurrentMixinProfileName = _localizedStrings["ProfilesNoActive"];
                MixinStatusMessage = _localizedStrings["SettingsMixinNoActiveProfile"];
            }

            ApplyTunPresentation(_currentTunRuntimeStatus, _currentSystemProxyState);
            GeoDataStatusMessage = GeoDataStatusTextHelper.BuildSettingsStatusMessage(_localizedStrings, _geoDataService.LastResult);
            RefreshUpdateState();
        }

        private void OnAppSettingsChanged(object? sender, EventArgs e)
        {
            if (_isDisposed)
            {
                return;
            }

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

        private void OnUpdateServiceStateChanged(object? sender, EventArgs e)
        {
            if (_isDisposed)
            {
                return;
            }

            RefreshUpdateState();
        }

        private void RefreshUpdateState()
        {
            UpdateState state = _updateService.CurrentState;
            UpdateStatusHeader = state.Status switch
            {
                UpdateStatus.Checking => _localizedStrings["SettingsUpdateStatusChecking"],
                UpdateStatus.UpToDate => _localizedStrings["SettingsUpdateStatusLatest"],
                UpdateStatus.UpdateAvailable => _localizedStrings["SettingsUpdateStatusAvailable"],
                UpdateStatus.Unavailable => _localizedStrings["SettingsUpdateStatusUnavailable"],
                UpdateStatus.Downloading => _localizedStrings["SettingsUpdateStatusDownloading"],
                UpdateStatus.LaunchingInstaller => _localizedStrings["SettingsUpdateStatusLaunchingInstaller"],
                UpdateStatus.DownloadFailed => _localizedStrings["SettingsUpdateStatusDownloadFailed"],
                _ => string.Empty,
            };

            IsUpdatingApp = state.IsBusy;
            IsCheckingForUpdates = state.Status == UpdateStatus.Checking;
            ShowUpdateDownloadProgress = state.Status == UpdateStatus.Downloading || state.Status == UpdateStatus.LaunchingInstaller;
            IsUpdateDownloadProgressIndeterminate = state.IsDownloadProgressIndeterminate;
            UpdateDownloadProgressValue = state.DownloadProgressPercentage;
            UpdateDownloadProgressText = state.Status switch
            {
                UpdateStatus.Downloading when state.IsDownloadProgressIndeterminate => _localizedStrings["SettingsUpdateDownloadProgressIndeterminate"],
                UpdateStatus.Downloading => string.Format(
                    _localizedStrings["SettingsUpdateDownloadProgressFormat"],
                    Math.Round(state.DownloadProgressPercentage, MidpointRounding.AwayFromZero).ToString("0")),
                UpdateStatus.LaunchingInstaller => _localizedStrings["SettingsUpdateStatusLaunchingInstaller"],
                _ => string.Empty,
            };

            string version = string.IsNullOrWhiteSpace(state.CurrentVersion)
                ? AppPackageInfoHelper.Current.Version.ToString(4)
                : state.CurrentVersion;
            CurrentAppVersionText = string.Format(
                _localizedStrings["SettingsAboutVersionFormat"],
                version);
        }

        private void CancelProfileStateLoad()
        {
            CancellationTokenSource? cancellation = _profileStateLoadCancellation;
            _profileStateLoadCancellation = null;
            if (cancellation is null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch
            {
                // Ignore best-effort cancellation failures.
            }
            finally
            {
                cancellation.Dispose();
            }
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

        private sealed class ActiveProfileLoadResult
        {
            private ActiveProfileLoadResult(
                bool hasActiveProfile,
                bool success,
                ProfileItem? profile,
                string workspacePath,
                MixinSettings? settings,
                SystemProxyState systemProxyState,
                TunRuntimeStatus tunRuntimeStatus,
                string message)
            {
                HasActiveProfile = hasActiveProfile;
                Success = success;
                Profile = profile;
                WorkspacePath = workspacePath;
                Settings = settings;
                SystemProxyState = systemProxyState;
                TunRuntimeStatus = tunRuntimeStatus;
                Message = message;
            }

            public bool HasActiveProfile { get; }

            public bool Success { get; }

            public ProfileItem? Profile { get; }

            public string WorkspacePath { get; }

            public MixinSettings? Settings { get; }

            public SystemProxyState SystemProxyState { get; }

            public TunRuntimeStatus TunRuntimeStatus { get; }

            public string Message { get; }

            public static ActiveProfileLoadResult NoActive(SystemProxyState systemProxyState, TunRuntimeStatus tunRuntimeStatus)
            {
                return new ActiveProfileLoadResult(
                    hasActiveProfile: false,
                    success: true,
                    profile: null,
                    workspacePath: string.Empty,
                    settings: null,
                    systemProxyState,
                    tunRuntimeStatus,
                    string.Empty);
            }

            public static ActiveProfileLoadResult FromSuccess(
                ProfileItem profile,
                string workspacePath,
                MixinSettings settings,
                SystemProxyState systemProxyState,
                TunRuntimeStatus tunRuntimeStatus)
            {
                return new ActiveProfileLoadResult(
                    hasActiveProfile: true,
                    success: true,
                    profile,
                    workspacePath,
                    settings,
                    systemProxyState,
                    tunRuntimeStatus,
                    string.Empty);
            }

            public static ActiveProfileLoadResult FromFailure(
                ProfileItem profile,
                string workspacePath,
                SystemProxyState systemProxyState,
                TunRuntimeStatus tunRuntimeStatus,
                string message)
            {
                return new ActiveProfileLoadResult(
                    hasActiveProfile: true,
                    success: false,
                    profile,
                    workspacePath,
                    settings: null,
                    systemProxyState,
                    tunRuntimeStatus,
                    message);
            }
        }
    }
}

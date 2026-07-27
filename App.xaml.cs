using ClashWinUI.Common;
using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Implementations;
using ClashWinUI.Services.Implementations.Config;
using ClashWinUI.Services.Interfaces;
using ClashWinUI.ViewModels;
using ClashWinUI.Views;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI
{
    public partial class App : Application
    {
        private readonly IHost _host;
        private readonly HttpClient _controllerProbeClient = new();
        private readonly SemaphoreSlim _shutdownSync = new(1, 1);
        private readonly AppProcessBootstrapResult _bootstrapResult;
        private readonly DispatcherQueue? _dispatcherQueue;

        private Window? _window;
        private ITrayService? _trayService;
        private AppControlChannel? _controlChannel;
        private int _startupPipelineStarted;
        private int _uiServicesStarted;
        private int _shutdownRequested;
        private int _skipProcessExitCleanup;

        public App()
        {
            StartupTrace.Reset("App ctor");
            _bootstrapResult = AppProcessBootstrapper.Current;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            StartupTrace.Write("App ctor: before InitializeComponent");
            InitializeComponent();
            StartupTrace.Write("App ctor: after InitializeComponent");
            StartupTrace.Write($"App ctor: role={_bootstrapResult.Role}");

            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            StartupTrace.Write("App ctor: before host creation");
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(static (_, services) =>
                {
                    services.AddSingleton<LocalizedStrings>();

                    services.AddSingleton<IThemeService, ThemeService>();
                    services.AddSingleton<IAppLogService, AppLogService>();
                    services.AddSingleton<IConfigService, ConfigService>();
                    services.AddSingleton<IKernelPathService, KernelPathService>();
                    services.AddSingleton<IKernelBootstrapService, KernelBootstrapService>();
                    services.AddSingleton<ITunService, TunService>();
                    services.AddSingleton<IGeoDataService, GeoDataService>();
                    services.AddSingleton<INetworkInfoService, NetworkInfoService>();
                    services.AddSingleton<IAppSettingsService, AppSettingsService>();
                    services.AddSingleton<IProcessService, ProcessService>();
                    services.AddSingleton<ISystemProxyService, SystemProxyService>();
                    services.AddSingleton<ITrayMenuActionService, TrayMenuActionService>();
                    services.AddSingleton<ITrayService, TrayService>();
                    services.AddSingleton<IMihomoService, MihomoService>();
                    services.AddSingleton<IProfileService, ProfileService>();
                    services.AddSingleton<IProfileActivationService, ProfileActivationService>();
                    services.AddSingleton<INavigationService, NavigationService>();
                    services.AddSingleton<IHomeChartStateService, HomeChartStateService>();
                    services.AddSingleton<IHomeOverviewSamplerService, HomeOverviewSamplerService>();
                    services.AddSingleton<IPageWarmCacheService, PageWarmCacheService>();
                    services.AddSingleton<IUpdateService, UpdateService>();
                    services.AddSingleton<MainWindow>();

                    services.AddSingleton<MainViewModel>();
                    services.AddTransient<HomeViewModel>();
                    services.AddTransient<ProfilesViewModel>();
                    services.AddTransient<ProxiesViewModel>();
                    services.AddTransient<ConnectionsViewModel>();
                    services.AddTransient<LogsViewModel>();
                    services.AddTransient<RulesViewModel>();
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<WelcomeWizardViewModel>();
                })
                .Build();
            StartupTrace.Write("App ctor: host created");

            StartupTrace.Write("App ctor: resolving LocalizedStrings");
            LocalizedStrings localizedStrings = _host.Services.GetRequiredService<LocalizedStrings>();
            StartupTrace.Write("App ctor: LocalizedStrings resolved");
            StartupTrace.Write("App ctor: resolving AppSettingsService");
            IAppSettingsService appSettingsService = _host.Services.GetRequiredService<IAppSettingsService>();
            StartupTrace.Write("App ctor: AppSettingsService resolved");
            StartupTrace.Write("App ctor: initializing localization");
            localizedStrings.Initialize(appSettingsService);
            StartupTrace.Write("App ctor: localization initialized");

            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            StartupTrace.Write("App ctor: handlers attached");
        }

        public Window? ActiveWindow => _window;
        public IServiceProvider Services => _host.Services;
        internal AppProcessRole ProcessRole => _bootstrapResult.Role;
        internal bool IsUiRole => _bootstrapResult.Role == AppProcessRole.Ui;
        internal bool IsTrayRole => _bootstrapResult.Role == AppProcessRole.Tray;

        public bool IsShuttingDown => Interlocked.CompareExchange(ref _shutdownRequested, 0, 0) == 1;

        public T GetRequiredService<T>() where T : notnull
        {
            return _host.Services.GetRequiredService<T>();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            StartupTrace.Write("OnLaunched: start");
            try
            {
                IAppSettingsService appSettingsService = _host.Services.GetRequiredService<IAppSettingsService>();
                StartupTrace.Write($"OnLaunched: role={_bootstrapResult.Role}, WelcomeCompleted={appSettingsService.WelcomeCompleted}");
                if (IsUiRole)
                {
                    await LaunchUiRoleAsync(appSettingsService);
                }
                else
                {
                    await LaunchTrayRoleAsync(appSettingsService);
                }
            }
            catch (Exception ex)
            {
                StartupTrace.WriteException("OnLaunched failed", ex);
                _host.Services.GetRequiredService<IAppLogService>()
                    .Add($"OnLaunched failed: {ex}", LogLevel.Error);
            }
        }

        public async Task RequestExitAsync()
        {
            if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1)
            {
                return;
            }

            await _shutdownSync.WaitAsync();
            try
            {
                if (IsUiRole && AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Tray))
                {
                    bool delegated = await AppControlChannel.TrySendAsync(
                        AppProcessRole.Tray,
                        new AppControlCommand
                        {
                            CommandType = AppControlCommandType.ShutdownApp,
                            CreatedAt = DateTimeOffset.UtcNow,
                        });
                    if (delegated)
                    {
                        Interlocked.Exchange(ref _skipProcessExitCleanup, 1);
                        await ShutdownCurrentInstanceAsync(includeRuntimeCleanup: false);
                        TerminateProcessHard();
                        return;
                    }
                }

                await ShutdownCurrentInstanceAsync(includeRuntimeCleanup: true);
                TerminateProcessHard();
            }
            finally
            {
                _shutdownSync.Release();
            }
        }

        public async Task RequestRestartAsync()
        {
            if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1)
            {
                return;
            }

            await _shutdownSync.WaitAsync();
            try
            {
                IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();

                // Peer must exit first so delayed relaunch can acquire role mutexes.
                AppProcessRole peerRole = IsTrayRole ? AppProcessRole.Ui : AppProcessRole.Tray;
                if (AppProcessBootstrapper.IsRoleRunning(peerRole))
                {
                    await AppControlChannel.TrySendAsync(peerRole, AppControlCommand.CreateExitSelf());
                    await WaitUntilRoleExitsAsync(peerRole, TimeSpan.FromSeconds(5));
                }

                // This instance owns runtime cleanup and the delayed relaunch.
                await ShutdownCurrentInstanceAsync(includeRuntimeCleanup: true);

                ElevationRelaunchOutcome outcome = AppElevationHelper.TryRelaunchDelayed(delaySeconds: 2);
                if (outcome.Status != ElevationRelaunchStatus.Relaunched)
                {
                    logService.Add(
                        $"Application restart failed. Mode={outcome.Target.LaunchMode}; Path={outcome.Target.ExecutablePath}; Detail={outcome.Message}",
                        LogLevel.Warning);
                    Interlocked.Exchange(ref _shutdownRequested, 0);
                    return;
                }

                logService.Add(
                    $"Application restart scheduled. Mode={outcome.Target.LaunchMode}; Path={outcome.Target.ExecutablePath}");
                TerminateProcessHard();
            }
            finally
            {
                _shutdownSync.Release();
            }
        }

        public async Task<bool> RequestElevatedRestartForTunAsync(string? routeKey = null)
        {
            IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();
            if (AppElevationHelper.IsProcessElevated())
            {
                return false;
            }

            string normalizedRoute = string.IsNullOrWhiteSpace(routeKey)
                ? MainViewModel.SettingsRouteKey
                : routeKey;
            PendingLaunchStore.Save(normalizedRoute);
            PendingElevatedStartStore.Save();

            // Keep the non-elevated tray alive when elevating from UI so the taskbar icon remains visible.
            // Only dismiss the UI peer when the tray itself is requesting elevation.
            if (IsTrayRole && AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Ui))
            {
                await AppControlChannel.TrySendAsync(AppProcessRole.Ui, AppControlCommand.CreateExitSelf());
                await WaitUntilRoleExitsAsync(AppProcessRole.Ui, TimeSpan.FromSeconds(5));
            }

            bool isPackaged = AppPackageInfoHelper.IsPackaged();
            ElevationRelaunchOutcome outcome = AppElevationHelper.TryRelaunchAsAdministrator();
            switch (outcome.Status)
            {
                case ElevationRelaunchStatus.Relaunched:
                    logService.Add(
                        $"TUN enable requested elevation. Relaunching as administrator. Mode={(isPackaged ? "packaged" : "unpackaged")}; " +
                        $"Target={outcome.Target.ExecutablePath}; Route={normalizedRoute}");
                    break;
                case ElevationRelaunchStatus.UserCancelled:
                    PendingElevatedStartStore.Clear();
                    logService.Add(
                        $"TUN elevation cancelled by user. Mode={(isPackaged ? "packaged" : "unpackaged")}; " +
                        $"Target={outcome.Target.ExecutablePath}; Detail={outcome.Message}",
                        LogLevel.Warning);
                    return false;
                default:
                    PendingElevatedStartStore.Clear();
                    logService.Add(
                        $"TUN elevation failed. Mode={(isPackaged ? "packaged" : "unpackaged")}; " +
                        $"Target={outcome.Target.ExecutablePath}; Detail={outcome.Message}",
                        LogLevel.Warning);
                    return false;
            }

            if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1)
            {
                return true;
            }

            await _shutdownSync.WaitAsync();
            try
            {
                // Exit quickly: elevated instance is waiting to acquire the UI role mutex.
                await ShutdownCurrentInstanceAsync(includeRuntimeCleanup: true);
                AppProcessBootstrapper.ReleaseRole();
                Interlocked.Exchange(ref _skipProcessExitCleanup, 1);
                TerminateProcessHard();
                return true;
            }
            finally
            {
                _shutdownSync.Release();
            }
        }

        public async Task RequestExitSelfAsync()
        {
            if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1)
            {
                return;
            }

            await _shutdownSync.WaitAsync();
            try
            {
                Interlocked.Exchange(ref _skipProcessExitCleanup, 1);
                await ShutdownCurrentInstanceAsync(includeRuntimeCleanup: false);
                TerminateProcessHard();
            }
            finally
            {
                _shutdownSync.Release();
            }
        }

        public async Task RequestLightweightModeAsync()
        {
            if (!IsUiRole)
            {
                return;
            }

            if (!await EnsureTrayCompanionAsync())
            {
                _host.Services.GetRequiredService<IAppLogService>()
                    .Add("Lightweight mode request ignored because tray companion is unavailable.", LogLevel.Warning);
                return;
            }

            // If a previous exit attempt got stuck, force-kill this UI process.
            if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1)
            {
                TerminateProcessHard();
                return;
            }

            try
            {
                // Don't hang forever on shutdown locks/services while trying to free memory.
                bool lockTaken = await _shutdownSync.WaitAsync(TimeSpan.FromSeconds(2));
                try
                {
                    Interlocked.Exchange(ref _skipProcessExitCleanup, 1);
                    Task shutdownTask = ShutdownCurrentInstanceAsync(includeRuntimeCleanup: false);
                    Task completed = await Task.WhenAny(shutdownTask, Task.Delay(TimeSpan.FromSeconds(2)));
                    if (!ReferenceEquals(completed, shutdownTask))
                    {
                        _host.Services.GetRequiredService<IAppLogService>()
                            .Add("Lightweight mode shutdown timed out; forcing UI process exit.", LogLevel.Warning);
                    }
                }
                finally
                {
                    if (lockTaken)
                    {
                        try
                        {
                            _shutdownSync.Release();
                        }
                        catch
                        {
                            // Best-effort only.
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    _host.Services.GetRequiredService<IAppLogService>()
                        .Add($"Lightweight mode shutdown failed: {ex.Message}", LogLevel.Warning);
                }
                catch
                {
                    // Best-effort only.
                }
            }
            finally
            {
                // Always hard-exit so tray memory drops back to a single process.
                TerminateProcessHard();
            }
        }

        private async void OnMainWindowWelcomeCompleted(object? sender, EventArgs e)
        {
            StartupTrace.Write("OnMainWindowWelcomeCompleted: start");
            if (sender is MainWindow mainWindow)
            {
                mainWindow.WelcomeCompleted -= OnMainWindowWelcomeCompleted;
            }

            try
            {
                string startupConfigPath = ResolveStartupConfigPath();
                if (TryRelaunchAsAdministratorForTunStartup(startupConfigPath))
                {
                    Interlocked.Exchange(ref _skipProcessExitCleanup, 1);
                    AppProcessBootstrapper.ReleaseRole();
                    TerminateProcessHard();
                    return;
                }

                await StartRuntimeStartupPipelineAsync(startupConfigPath, initializeTrayOnCompletion: false);
                await EnsureTrayCompanionAsync();
            }
            catch (Exception ex)
            {
                StartupTrace.WriteException("Post-welcome startup failed", ex);
                _host.Services.GetRequiredService<IAppLogService>()
                    .Add($"Post-welcome startup failed: {ex}", LogLevel.Error);
            }
        }

        private async Task LaunchUiRoleAsync(IAppSettingsService appSettingsService)
        {
            string? startupConfigPath = null;
            if (appSettingsService.WelcomeCompleted && _bootstrapResult.ShouldOwnStartupPipeline)
            {
                StartupTrace.Write("LaunchUiRoleAsync: resolving startup config before window");
                startupConfigPath = ResolveStartupConfigPath();
                if (TryRelaunchAsAdministratorForTunStartup(startupConfigPath))
                {
                    Interlocked.Exchange(ref _skipProcessExitCleanup, 1);
                    AppProcessBootstrapper.ReleaseRole();
                    TerminateProcessHard();
                    return;
                }
            }

            StartupTrace.Write("LaunchUiRoleAsync: resolving MainWindow");
            _window = _host.Services.GetRequiredService<MainWindow>();
            StartupTrace.Write("LaunchUiRoleAsync: MainWindow resolved");
            if (_window is MainWindow mainWindow)
            {
                mainWindow.WelcomeCompleted += OnMainWindowWelcomeCompleted;
            }

            _window.Activate();
            await _host.StartAsync();
            InitializeControlChannel();

            if (!appSettingsService.WelcomeCompleted)
            {
                return;
            }

            if (_bootstrapResult.ShouldOwnStartupPipeline)
            {
                await StartRuntimeStartupPipelineAsync(startupConfigPath ?? ResolveStartupConfigPath(), initializeTrayOnCompletion: false);
                await EnsureTrayCompanionAsync();
            }
            else
            {
                await TryAttachToPersistedRuntimeAsync(waitForStartupOwner: false);
                await EnsureSystemProxyPolicyForCurrentRuntimeAsync();
                StartUiServices();
            }

            await ApplyPendingLaunchCommandAsync();
        }

        private async Task LaunchTrayRoleAsync(IAppSettingsService appSettingsService)
        {
            await _host.StartAsync();
            InitializeControlChannel();

            if (!appSettingsService.WelcomeCompleted)
            {
                InitializeTray();
                return;
            }

            bool attached = await TryAttachToPersistedRuntimeAsync(waitForStartupOwner: AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Ui));
            if (!attached)
            {
                string startupConfigPath = ResolveStartupConfigPath();
                if (TryRelaunchAsAdministratorForTunStartup(startupConfigPath))
                {
                    Interlocked.Exchange(ref _skipProcessExitCleanup, 1);
                    AppProcessBootstrapper.ReleaseRole();
                    TerminateProcessHard();
                    return;
                }

                await StartRuntimeStartupPipelineAsync(startupConfigPath, initializeTrayOnCompletion: true);
                return;
            }

            // Attaching to an existing Mihomo instance still needs system-proxy policy applied
            // in this process session (ownership is per-process).
            await EnsureSystemProxyPolicyForCurrentRuntimeAsync();
            InitializeTray();
        }

        private async Task StartRuntimeStartupPipelineAsync(string startupConfigPath, bool initializeTrayOnCompletion)
        {
            StartupTrace.Write("StartRuntimeStartupPipelineAsync: requested");
            if (Interlocked.Exchange(ref _startupPipelineStarted, 1) == 1)
            {
                StartupTrace.Write("StartRuntimeStartupPipelineAsync: already started");
                return;
            }

            if (IsUiRole)
            {
                StartUiServices();
            }

            await InitializeStartupPipelineAsync(startupConfigPath, initializeTrayOnCompletion);
        }

        private async Task InitializeStartupPipelineAsync(string startupConfigPath, bool initializeTrayOnCompletion)
        {
            IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();
            IKernelBootstrapService kernelBootstrapService = _host.Services.GetRequiredService<IKernelBootstrapService>();
            IKernelPathService kernelPathService = _host.Services.GetRequiredService<IKernelPathService>();
            IGeoDataService geoDataService = _host.Services.GetRequiredService<IGeoDataService>();
            IProcessService processService = _host.Services.GetRequiredService<IProcessService>();
            ITunService tunService = _host.Services.GetRequiredService<ITunService>();
            ISystemProxyService systemProxyService = _host.Services.GetRequiredService<ISystemProxyService>();
            IConfigService configService = _host.Services.GetRequiredService<IConfigService>();
            IProfileService profileService = _host.Services.GetRequiredService<IProfileService>();

            try
            {
                bool kernelReady = await kernelBootstrapService.EnsureKernelReadyAsync();
                if (!kernelReady)
                {
                    logService.Add("Kernel bootstrap failed. Skip Mihomo startup.", LogLevel.Error);
                    if (initializeTrayOnCompletion)
                    {
                        InitializeTray();
                    }
                    return;
                }

                GeoDataOperationResult geoDataEnsureResult = await geoDataService.EnsureGeoDataReadyAsync();
                if (!geoDataEnsureResult.Success)
                {
                    logService.Add($"GeoData ensure failed before startup: {geoDataEnsureResult.Details}", LogLevel.Warning);
                }

                bool controllerReady = await StartAndWaitControllerReadyAsync(processService, startupConfigPath);
                if (!controllerReady)
                {
                    controllerReady = await TryRecoverFromGeoDataFailureAsync(
                        processService,
                        geoDataService,
                        tunService,
                        kernelPathService,
                        startupConfigPath);
                }

                if (controllerReady && tunService.IsTunEnabled(startupConfigPath))
                {
                    TunRuntimeValidationOutcome tunValidation = await TunRuntimeValidationHelper.ValidateAsync(
                        tunService,
                        kernelPathService,
                        processService,
                        startupConfigPath).ConfigureAwait(false);
                    if (!tunValidation.Success)
                    {
                        processService.UpdateFailureDiagnostic(tunValidation.FailureKind, tunValidation.Message);
                        logService.Add(
                            $"Startup controller is ready, but TUN runtime is unhealthy: {tunValidation.Message}",
                            LogLevel.Warning);

                        string? recoveredPath = await TryDisableTunAfterStartupFailureAsync(
                            processService,
                            configService,
                            profileService,
                            tunService,
                            systemProxyService,
                            logService,
                            startupConfigPath,
                            tunValidation.Message);
                        if (!string.IsNullOrWhiteSpace(recoveredPath))
                        {
                            startupConfigPath = recoveredPath;
                            logService.Add(
                                "TUN startup failed; automatically disabled TUN and restarted without TUN so the app stays usable.",
                                LogLevel.Warning);
                        }
                        else
                        {
                            // Keep the controller if it is still alive; do not treat TUN failure as total startup death.
                            controllerReady = processService.IsRunning
                                && await WaitForControllerReadyAsync(
                                    processService.ControllerHost,
                                    processService.ControllerPort,
                                    TimeSpan.FromSeconds(3));
                            if (controllerReady)
                            {
                                logService.Add(
                                    "TUN startup failed and auto-disable recovery did not fully complete; continuing with current controller session.",
                                    LogLevel.Warning);
                            }
                        }
                    }
                }

                if (!controllerReady)
                {
                    string fallbackConfigPath = processService.EnsureStartupConfigPath();
                    MihomoFailureDiagnostic diagnostic = processService.LastFailureDiagnostic;
                    bool shouldFallbackToDefaultConfig = !MihomoFailureKindHelper.IsTunFailure(diagnostic.Kind);
                    if (shouldFallbackToDefaultConfig
                        && !string.Equals(fallbackConfigPath, startupConfigPath, StringComparison.OrdinalIgnoreCase))
                    {
                        logService.Add($"Primary config failed, fallback to default startup config: {fallbackConfigPath}", LogLevel.Warning);
                        bool fallbackStarted = await processService.RestartAsync(fallbackConfigPath);
                        if (fallbackStarted)
                        {
                            controllerReady = await WaitForControllerReadyAsync(
                                processService.ControllerHost,
                                processService.ControllerPort,
                                TimeSpan.FromSeconds(20));
                            if (controllerReady)
                            {
                                processService.ResetFailureDiagnostic();
                                startupConfigPath = fallbackConfigPath;
                            }
                        }
                    }
                    else if (MihomoFailureKindHelper.IsTunFailure(diagnostic.Kind))
                    {
                        logService.Add(
                            $"Primary startup failed after TUN issues. Detail={diagnostic.Message}",
                            LogLevel.Warning);
                    }
                }

                if (!controllerReady)
                {
                    logService.Add($"Mihomo controller not ready: {processService.ControllerHost}:{processService.ControllerPort}", LogLevel.Error);
                    if (initializeTrayOnCompletion)
                    {
                        InitializeTray();
                    }
                    return;
                }

                await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                    systemProxyService,
                    processService,
                    tunService,
                    startupConfigPath);
                processService.ResetFailureDiagnostic();

                int proxyPort = processService.ResolveProxyPort(startupConfigPath);
                logService.Add($"Startup completed. Controller={processService.ControllerHost}:{processService.ControllerPort}, ProxyPort={proxyPort}");
            }
            catch (Exception ex)
            {
                logService.Add($"Startup pipeline failed: {ex}", LogLevel.Error);
            }
            finally
            {
                if (initializeTrayOnCompletion)
                {
                    InitializeTray();
                }
            }
        }

        private string ResolveStartupConfigPath()
        {
            IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();
            IConfigService configService = _host.Services.GetRequiredService<IConfigService>();
            IProfileService profileService = _host.Services.GetRequiredService<IProfileService>();
            IProcessService processService = _host.Services.GetRequiredService<IProcessService>();

            string startupConfigPath = processService.EnsureStartupConfigPath();
            ProfileItem? activeProfile = profileService.GetActiveProfile();
            if (activeProfile is not null)
            {
                try
                {
                    startupConfigPath = configService.BuildRuntime(activeProfile);
                }
                catch (Exception ex)
                {
                    logService.Add(
                        $"Build runtime config failed for active profile. Use default startup profile instead: {ex.Message}",
                        LogLevel.Warning);
                }
            }

            logService.Add($"Startup config path: {startupConfigPath}");
            return startupConfigPath;
        }

        private bool TryRelaunchAsAdministratorForTunStartup(string startupConfigPath)
        {
            IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();
            ITunService tunService = _host.Services.GetRequiredService<ITunService>();
            bool isPackaged = AppPackageInfoHelper.IsPackaged();

            if (string.IsNullOrWhiteSpace(startupConfigPath) || !tunService.IsTunEnabled(startupConfigPath))
            {
                return false;
            }

            if (AppElevationHelper.IsProcessElevated())
            {
                return false;
            }

            // Ensure the elevated process waits for this instance to release role mutexes
            // instead of treating itself as a duplicate and flash-exiting.
            PendingElevatedStartStore.Save();
            PendingLaunchStore.Save(MainViewModel.SettingsRouteKey);

            ElevationRelaunchOutcome outcome = AppElevationHelper.TryRelaunchAsAdministrator();
            switch (outcome.Status)
            {
                case ElevationRelaunchStatus.Relaunched:
                    logService.Add(
                        $"Startup config enables TUN. Relaunching as administrator. Mode={(isPackaged ? "packaged" : "unpackaged")}; " +
                        $"StartupConfig={startupConfigPath}; Target={outcome.Target.ExecutablePath}");
                    return true;
                case ElevationRelaunchStatus.UserCancelled:
                    PendingElevatedStartStore.Clear();
                    logService.Add(
                        $"Administrator relaunch cancelled by user. Continue without elevation. " +
                        $"Mode={(isPackaged ? "packaged" : "unpackaged")}; StartupConfig={startupConfigPath}; " +
                        $"Target={outcome.Target.ExecutablePath}; Detail={outcome.Message}",
                        LogLevel.Warning);
                    return false;
                default:
                    PendingElevatedStartStore.Clear();
                    logService.Add(
                        $"Administrator relaunch failed. Continue without elevation. " +
                        $"Mode={(isPackaged ? "packaged" : "unpackaged")}; StartupConfig={startupConfigPath}; " +
                        $"Target={outcome.Target.ExecutablePath}; Detail={outcome.Message}",
                        LogLevel.Warning);
                    return false;
            }
        }

        private async Task<bool> WaitForControllerReadyAsync(string host, int port, TimeSpan timeout)
        {
            string url = $"http://{host}:{port}/version";
            DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    using HttpResponseMessage response = await _controllerProbeClient.GetAsync(url, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Retry until timeout.
                }

                await Task.Delay(400);
            }

            return false;
        }

        private void InitializeTray()
        {
            if (!IsTrayRole)
            {
                return;
            }

            try
            {
                _trayService ??= _host.Services.GetRequiredService<ITrayService>();
                if (_trayService.IsInitialized)
                {
                    _trayService.Show();
                    return;
                }

                _trayService.Initialize(
                    showMainWindowAsyncAction: ShowMainWindowAsync,
                    restartApplicationAsyncAction: RequestRestartAsync,
                    exitApplicationAsyncAction: RequestExitAsync);
                _trayService.Show();
            }
            catch (Exception ex)
            {
                _host.Services.GetRequiredService<IAppLogService>()
                    .Add($"Initialize tray failed: {ex}", LogLevel.Error);
            }
        }

        private Task ShowMainWindow()
        {
            return ShowMainWindowAsync(MainViewModel.HomeRouteKey);
        }

        private async Task ShowMainWindowAsync(string routeKey)
        {
            if (IsTrayRole)
            {
                await ShowOrLaunchUiAsync(routeKey);
                return;
            }

            if (_window is null)
            {
                return;
            }

            if (_window is MainWindow mainWindow)
            {
                await mainWindow.RestoreFromBackgroundAsync();
            }
            else
            {
                WindowExtensions.Show(_window);
                _window.Activate();
            }

            if (_host.Services.GetRequiredService<IAppSettingsService>().WelcomeCompleted)
            {
                MainViewModel mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
                if (mainViewModel.NavigateCommand.CanExecute(routeKey))
                {
                    mainViewModel.NavigateCommand.Execute(routeKey);
                }
            }
        }

        private async Task ShutdownCurrentInstanceAsync(bool includeRuntimeCleanup)
        {
            IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();
            try
            {
                _host.Services.GetRequiredService<IHomeOverviewSamplerService>().FlushState();
            }
            catch (Exception ex)
            {
                logService.Add($"Flush home overview state failed during exit: {ex.Message}", LogLevel.Warning);
            }

            try
            {
                if (includeRuntimeCleanup)
                {
                    if (IsTrayRole && AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Ui))
                    {
                        await AppControlChannel.TrySendAsync(
                            AppProcessRole.Ui,
                            new AppControlCommand
                            {
                                CommandType = AppControlCommandType.ShutdownUi,
                                CreatedAt = DateTimeOffset.UtcNow,
                            });
                    }

                    await CleanupRuntimeAsync();
                }
            }
            catch (Exception ex)
            {
                logService.Add($"Cleanup runtime failed during exit: {ex.Message}", LogLevel.Warning);
            }

            try
            {
                _trayService?.Shutdown();
                _trayService = null;
            }
            catch (Exception ex)
            {
                logService.Add($"Tray shutdown failed during exit: {ex.Message}", LogLevel.Warning);
            }

            try
            {
                _controlChannel?.Dispose();
                _controlChannel = null;
            }
            catch (Exception ex)
            {
                logService.Add($"Control channel shutdown failed during exit: {ex.Message}", LogLevel.Warning);
            }

            try
            {
                if (_window is not null)
                {
                    _window.Close();
                    _window = null;
                }
            }
            catch (Exception ex)
            {
                logService.Add($"Window close failed during exit: {ex.Message}", LogLevel.Warning);
            }

            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                logService.Add($"Host stop failed during exit: {ex.Message}", LogLevel.Warning);
            }

            // Release role mutex before process teardown so peer relaunch is not blocked by zombies.
            AppProcessBootstrapper.ReleaseRole();
        }

        private void StartUiServices()
        {
            if (Interlocked.Exchange(ref _uiServicesStarted, 1) == 1)
            {
                return;
            }

            _host.Services.GetRequiredService<IHomeOverviewSamplerService>().Start();
            _ = RunStartupUpdateCheckAsync();
        }

        private async Task<bool> EnsureTrayCompanionAsync()
        {
            if (AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Tray))
            {
                return true;
            }

            IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();
            // Elevated UI must spawn a medium-IL tray process; elevated tray icons often do not show.
            ElevationRelaunchOutcome outcome = AppElevationHelper.TryLaunchUnelevatedInstance();
            if (outcome.Status != ElevationRelaunchStatus.Relaunched)
            {
                logService.Add(
                    $"Tray companion launch failed. Mode={outcome.Target.LaunchMode}; Path={outcome.Target.ExecutablePath}; Detail={outcome.Message}",
                    LogLevel.Warning);
                return false;
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Tray))
                {
                    return true;
                }

                await Task.Delay(200);
            }

            return AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Tray);
        }

        private async Task<bool> TryAttachToPersistedRuntimeAsync(bool waitForStartupOwner)
        {
            IProcessService processService = _host.Services.GetRequiredService<IProcessService>();
            if (processService.TryAttachToPersistedRuntime())
            {
                return true;
            }

            if (!waitForStartupOwner)
            {
                return false;
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(250);
                if (processService.TryAttachToPersistedRuntime())
                {
                    return true;
                }
            }

            return false;
        }

        private async Task ApplyPendingLaunchCommandAsync()
        {
            PendingLaunchCommand? pendingLaunch = PendingLaunchStore.TryConsume();
            if (pendingLaunch is null || string.IsNullOrWhiteSpace(pendingLaunch.RouteKey))
            {
                return;
            }

            await ShowMainWindowAsync(pendingLaunch.RouteKey);
        }

        private void InitializeControlChannel()
        {
            _controlChannel?.Dispose();
            _controlChannel = new AppControlChannel(_bootstrapResult.Role, HandleControlCommandAsync);
            _controlChannel.Start();
        }

        private Task HandleControlCommandAsync(AppControlCommand command)
        {
            switch (command.CommandType)
            {
                case AppControlCommandType.ShowRoute:
                    // Fire-and-forget so the control channel never blocks on UI work.
                    _ = DispatchToUiThreadAsync(() => ShowMainWindowAsync(string.IsNullOrWhiteSpace(command.RouteKey)
                        ? MainViewModel.HomeRouteKey
                        : command.RouteKey));
                    break;
                case AppControlCommandType.ShutdownUi:
                    if (IsUiRole)
                    {
                        _ = DispatchToUiThreadAsync(RequestLightweightModeAsync);
                    }
                    break;
                case AppControlCommandType.ShutdownApp:
                    _ = DispatchToUiThreadAsync(RequestExitAsync);
                    break;
                case AppControlCommandType.ExitSelf:
                    _ = DispatchToUiThreadAsync(RequestExitSelfAsync);
                    break;
            }

            return Task.CompletedTask;
        }

        private async Task ShowOrLaunchUiAsync(string routeKey)
        {
            string normalizedRoute = string.IsNullOrWhiteSpace(routeKey) ? MainViewModel.HomeRouteKey : routeKey;
            IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();

            if (AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Ui))
            {
                bool forwarded = await AppControlChannel.TrySendAsync(
                    AppProcessRole.Ui,
                    AppControlCommand.CreateShowRoute(normalizedRoute));
                if (forwarded)
                {
                    return;
                }

                // Mutex still present but UI control channel is dead: wait for stale process to free the role.
                logService.Add("UI role is occupied but unresponsive. Waiting for stale UI process to exit...", LogLevel.Warning);
                await WaitUntilRoleExitsAsync(AppProcessRole.Ui, TimeSpan.FromSeconds(3));
                if (AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Ui))
                {
                    // Retry once more in case the process is recovering.
                    forwarded = await AppControlChannel.TrySendAsync(
                        AppProcessRole.Ui,
                        AppControlCommand.CreateShowRoute(normalizedRoute));
                    if (forwarded)
                    {
                        return;
                    }

                    logService.Add("Stale UI process still holding role mutex. Launching a replacement UI instance.", LogLevel.Warning);
                }
            }

            PendingLaunchStore.Save(normalizedRoute);
            ElevationRelaunchOutcome outcome = AppElevationHelper.TryLaunchNewInstance();
            if (outcome.Status != ElevationRelaunchStatus.Relaunched)
            {
                logService.Add(
                    $"UI launch failed. Mode={outcome.Target.LaunchMode}; Path={outcome.Target.ExecutablePath}; Detail={outcome.Message}",
                    LogLevel.Warning);
            }
        }

        private Task DispatchToUiThreadAsync(Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            {
                return action();
            }

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Failed to enqueue operation to the UI thread."));
            }

            return tcs.Task;
        }

        private async Task RunStartupUpdateCheckAsync()
        {
            try
            {
                IUpdateService updateService = _host.Services.GetRequiredService<IUpdateService>();
                await updateService.CheckForUpdatesAsync(forceRefresh: true);
            }
            catch (Exception ex)
            {
                _host.Services.GetRequiredService<IAppLogService>()
                    .Add($"Startup update check failed: {ex.Message}", LogLevel.Warning);
            }
        }

        private async Task EnsureSystemProxyPolicyForCurrentRuntimeAsync()
        {
            try
            {
                IProcessService processService = _host.Services.GetRequiredService<IProcessService>();
                ISystemProxyService systemProxyService = _host.Services.GetRequiredService<ISystemProxyService>();
                ITunService tunService = _host.Services.GetRequiredService<ITunService>();
                string? configPath = processService.CurrentConfigPath;
                if (string.IsNullOrWhiteSpace(configPath))
                {
                    RuntimeStateSnapshot? snapshot = processService.GetPersistedRuntimeState();
                    configPath = snapshot?.CurrentConfigPath;
                }

                if (string.IsNullOrWhiteSpace(configPath))
                {
                    // Attach/tray paths can race before CurrentConfigPath is populated.
                    // Fall back to the active-profile runtime so system proxy still applies.
                    configPath = ResolveStartupConfigPath();
                }

                if (string.IsNullOrWhiteSpace(configPath))
                {
                    return;
                }

                // Retry a few times: enabling right after controller attach can race with
                // Windows shell/policy readers and previously failed on first write.
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                        systemProxyService,
                        processService,
                        tunService,
                        configPath).ConfigureAwait(false);

                    if (tunService.IsTunEnabled(configPath))
                    {
                        if (!systemProxyService.GetCurrentState().IsEnabled)
                        {
                            return;
                        }
                    }
                    else
                    {
                        SystemProxyState state = systemProxyService.GetCurrentState();
                        int expectedPort = processService.ResolveProxyPort(configPath);
                        string expectedServer = $"127.0.0.1:{expectedPort}";
                        if (state.IsEnabled &&
                            state.ProxyServer.Contains($"{expectedPort}", StringComparison.Ordinal))
                        {
                            return;
                        }
                    }

                    if (attempt < 3)
                    {
                        await Task.Delay(100 * attempt).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _host.Services.GetRequiredService<IAppLogService>()
                    .Add($"Apply system proxy policy for current runtime failed: {ex.Message}", LogLevel.Warning);
            }
        }

        private void TerminateProcessHard()
        {
            try
            {
                AppProcessBootstrapper.ReleaseRole();
            }
            catch
            {
                // Best-effort only.
            }

            try
            {
                _controlChannel?.Dispose();
                _controlChannel = null;
            }
            catch
            {
                // Best-effort only.
            }

            try
            {
                _window?.Close();
                _window = null;
            }
            catch
            {
                // Best-effort only.
            }

            // Prefer immediate process termination. Application.Exit() can keep a WinUI process
            // alive when background work or COM apartments are still pumping.
            try
            {
                Environment.Exit(0);
            }
            catch
            {
                // Fall through to Kill.
            }

            try
            {
                System.Diagnostics.Process.GetCurrentProcess().Kill(entireProcessTree: true);
            }
            catch
            {
                // Last resort failed; nothing else we can do.
            }
        }

        private static async Task WaitUntilRoleExitsAsync(AppProcessRole role, TimeSpan timeout)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (!AppProcessBootstrapper.IsRoleRunning(role))
                {
                    return;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        private async Task CleanupRuntimeAsync()
        {
            // UI + tray share one Mihomo runtime and system-proxy session. Only the last
            // remaining role should disable proxy / stop the core.
            bool peerRunning = IsTrayRole
                ? AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Ui)
                : AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Tray);
            if (peerRunning)
            {
                _host.Services.GetRequiredService<IAppLogService>()
                    .Add("Skip runtime cleanup because peer process role is still running.");
                return;
            }

            ISystemProxyService systemProxyService = _host.Services.GetRequiredService<ISystemProxyService>();
            IProcessService processService = _host.Services.GetRequiredService<IProcessService>();

            await systemProxyService.DisableAsync();
            await processService.StopAsync();
        }

        private async void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            StartupTrace.WriteException("XAML unhandled exception", e.Exception);
            IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();
            try
            {
                logService.Add($"Unhandled exception: {e.Exception}", LogLevel.Error);

                if (IsTrayIconException(e.Exception))
                {
                    try
                    {
                        _trayService?.Shutdown();
                        _trayService = null;
                    }
                    catch (Exception ex)
                    {
                        logService.Add($"Tray shutdown failed after tray exception: {ex.Message}", LogLevel.Warning);
                    }

                    logService.Add("Tray unavailable, app continues without tray", LogLevel.Warning);
                    return;
                }

                if (ShouldCleanupRuntimeOnUnexpectedExit())
                {
                    await CleanupRuntimeAsync();
                }
            }
            catch
            {
                // Ignore cleanup errors for best-effort handling.
            }
            finally
            {
                e.Handled = true;
            }
        }

        private static void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception exception)
            {
                StartupTrace.WriteException("AppDomain unhandled exception", exception);
                return;
            }

            StartupTrace.Write($"AppDomain unhandled exception: {e.ExceptionObject}");
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            StartupTrace.WriteException("Unobserved task exception", e.Exception);
        }

        private static bool IsTrayIconException(Exception exception)
        {
            string details = exception.ToString();
            return details.Contains("H.NotifyIcon", StringComparison.OrdinalIgnoreCase)
                || details.Contains("TaskbarIcon", StringComparison.OrdinalIgnoreCase)
                || details.Contains("ToSmallIcon", StringComparison.OrdinalIgnoreCase)
                || details.Contains("Argument 'picture' must be a picture that can be used as a Icon", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> StartAndWaitControllerReadyAsync(IProcessService processService, string configPath)
        {
            bool started = await processService.EnsureStartedAsync(configPath);
            if (!started)
            {
                return false;
            }

            return await WaitForControllerReadyAsync(
                processService.ControllerHost,
                processService.ControllerPort,
                TimeSpan.FromSeconds(20));
        }

        private async Task<string?> TryDisableTunAfterStartupFailureAsync(
            IProcessService processService,
            IConfigService configService,
            IProfileService profileService,
            ITunService tunService,
            ISystemProxyService systemProxyService,
            IAppLogService logService,
            string failedConfigPath,
            string failureDetail)
        {
            try
            {
                ProfileItem? activeProfile = profileService.GetActiveProfile();
                if (activeProfile is null)
                {
                    logService.Add(
                        $"TUN auto-disable skipped because no active profile is available. FailedConfig={failedConfigPath}",
                        LogLevel.Warning);
                    return null;
                }

                MixinSettings settings = await Task.Run(() => configService.LoadMixin(activeProfile)).ConfigureAwait(false);
                if (!settings.TunEnabled)
                {
                    return null;
                }

                settings.TunEnabled = false;
                string runtimePath = await Task.Run(() => configService.SaveMixinAndBuildRuntime(activeProfile, settings))
                    .ConfigureAwait(false);

                bool restarted = await processService.RestartAsync(runtimePath).ConfigureAwait(false);
                if (!restarted)
                {
                    logService.Add(
                        $"TUN auto-disable rebuild succeeded but Mihomo restart failed. Runtime={runtimePath}; Detail={failureDetail}",
                        LogLevel.Warning);
                    return null;
                }

                bool ready = await WaitForControllerReadyAsync(
                    processService.ControllerHost,
                    processService.ControllerPort,
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                if (!ready)
                {
                    logService.Add(
                        $"TUN auto-disable restart completed but controller is not ready. Runtime={runtimePath}",
                        LogLevel.Warning);
                    return null;
                }

                await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                    systemProxyService,
                    processService,
                    tunService,
                    runtimePath).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(failureDetail))
                {
                    processService.UpdateFailureDiagnostic(MihomoFailureKind.TunDependency, failureDetail);
                }

                return runtimePath;
            }
            catch (Exception ex)
            {
                logService.Add($"TUN auto-disable recovery failed: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        private static async Task<bool> ValidateStartupTunRuntimeAsync(
            IProcessService processService,
            ITunService tunService,
            IKernelPathService kernelPathService,
            IAppLogService logService,
            string configPath)
        {
            TunRuntimeValidationOutcome validation = await TunRuntimeValidationHelper.ValidateAsync(
                tunService,
                kernelPathService,
                processService,
                configPath).ConfigureAwait(false);
            if (validation.Success)
            {
                return true;
            }

            processService.UpdateFailureDiagnostic(validation.FailureKind, validation.Message);
            logService.Add($"Startup controller is ready, but TUN runtime is unhealthy: {validation.Message}", LogLevel.Warning);
            return false;
        }

        private async Task<bool> TryRecoverFromGeoDataFailureAsync(
            IProcessService processService,
            IGeoDataService geoDataService,
            ITunService tunService,
            IKernelPathService kernelPathService,
            string configPath)
        {
            MihomoFailureDiagnostic diagnostic = processService.LastFailureDiagnostic;
            if (diagnostic.Kind != MihomoFailureKind.GeoData)
            {
                return false;
            }

            IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();
            logService.Add(
                $"GeoData issue detected during Mihomo startup. Force refresh GeoData and retry config: {configPath}. Detail={diagnostic.Message}",
                LogLevel.Warning);

            GeoDataOperationResult updateResult = await geoDataService.UpdateGeoDataAsync();
            if (!updateResult.Success)
            {
                logService.Add($"GeoData refresh failed during startup recovery: {updateResult.Details}", LogLevel.Warning);
                return false;
            }

            bool restarted = await processService.RestartAsync(configPath);
            if (!restarted)
            {
                logService.Add($"Mihomo restart failed after GeoData refresh: {configPath}", LogLevel.Warning);
                return false;
            }

            bool controllerReady = await WaitForControllerReadyAsync(
                processService.ControllerHost,
                processService.ControllerPort,
                TimeSpan.FromSeconds(20));

            if (controllerReady)
            {
                controllerReady = await ValidateStartupTunRuntimeAsync(
                    processService,
                    tunService,
                    kernelPathService,
                    logService,
                    configPath);
                if (controllerReady)
                {
                    processService.ResetFailureDiagnostic();
                }
            }

            return controllerReady;
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            if (Interlocked.CompareExchange(ref _skipProcessExitCleanup, 0, 0) == 1)
            {
                return;
            }

            try
            {
                _controlChannel?.Dispose();
                _trayService?.Shutdown();
                _host.Services.GetRequiredService<IHomeOverviewSamplerService>().FlushState();
                if (ShouldCleanupRuntimeOnUnexpectedExit())
                {
                    CleanupRuntimeAsync().GetAwaiter().GetResult();
                }
            }
            catch
            {
                // Ignore cleanup errors for best-effort handling.
            }
        }

        private bool ShouldCleanupRuntimeOnUnexpectedExit()
        {
            // Shared runtime/proxy must stay alive while the peer role is still running.
            // A dying tray process previously tore down system proxy even when UI was open.
            if (IsTrayRole)
            {
                return !AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Ui);
            }

            return !AppProcessBootstrapper.IsRoleRunning(AppProcessRole.Tray);
        }
    }
}

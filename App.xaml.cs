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

        private Window? _window;
        private ITrayService? _trayService;
        private int _startupPipelineStarted;
        private int _shutdownRequested;
        private int _skipProcessExitCleanup;

        public App()
        {
            StartupTrace.Reset("App ctor");
            StartupTrace.Write("App ctor: before InitializeComponent");
            InitializeComponent();
            StartupTrace.Write("App ctor: after InitializeComponent");

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
                    services.AddSingleton<ITrayService, TrayService>();
                    services.AddSingleton<IMihomoService, MihomoService>();
                    services.AddSingleton<IProfileService, ProfileService>();
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
                StartupTrace.Write($"OnLaunched: WelcomeCompleted={appSettingsService.WelcomeCompleted}");
                string? startupConfigPath = null;

                if (appSettingsService.WelcomeCompleted)
                {
                    StartupTrace.Write("OnLaunched: resolving startup config before window");
                    startupConfigPath = ResolveStartupConfigPath();
                    if (TryRelaunchAsAdministratorForTunStartup(startupConfigPath))
                    {
                        Interlocked.Exchange(ref _skipProcessExitCleanup, 1);
                        Exit();
                        return;
                    }
                }

                StartupTrace.Write("OnLaunched: resolving MainWindow");
                _window = _host.Services.GetRequiredService<MainWindow>();
                StartupTrace.Write("OnLaunched: MainWindow resolved");
                if (_window is MainWindow mainWindow)
                {
                    mainWindow.WelcomeCompleted += OnMainWindowWelcomeCompleted;
                }

                StartupTrace.Write("OnLaunched: before Activate");
                _window.Activate();
                StartupTrace.Write("OnLaunched: after Activate");

                StartupTrace.Write("OnLaunched: before host StartAsync");
                await _host.StartAsync();
                StartupTrace.Write("OnLaunched: host started");
                if (appSettingsService.WelcomeCompleted)
                {
                    StartupTrace.Write("OnLaunched: starting startup pipeline");
                    await StartStartupPipelineAsync(startupConfigPath ?? ResolveStartupConfigPath());
                    StartupTrace.Write("OnLaunched: startup pipeline completed");
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
                    await CleanupRuntimeAsync();
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

                Exit();
            }
            finally
            {
                _shutdownSync.Release();
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
                    Exit();
                    return;
                }

                await StartStartupPipelineAsync(startupConfigPath);
            }
            catch (Exception ex)
            {
                StartupTrace.WriteException("Post-welcome startup failed", ex);
                _host.Services.GetRequiredService<IAppLogService>()
                    .Add($"Post-welcome startup failed: {ex}", LogLevel.Error);
            }
        }

        private async Task StartStartupPipelineAsync(string startupConfigPath)
        {
            StartupTrace.Write("StartStartupPipelineAsync: requested");
            if (Interlocked.Exchange(ref _startupPipelineStarted, 1) == 1)
            {
                StartupTrace.Write("StartStartupPipelineAsync: already started");
                return;
            }

            _host.Services.GetRequiredService<IHomeOverviewSamplerService>().Start();
            StartupTrace.Write("StartStartupPipelineAsync: sampler started");
            await InitializeStartupPipelineAsync(startupConfigPath);
            _ = RunStartupUpdateCheckAsync();
            StartupTrace.Write("StartStartupPipelineAsync: update check launched");
        }

        private async Task InitializeStartupPipelineAsync(string startupConfigPath)
        {
            IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();
            IKernelBootstrapService kernelBootstrapService = _host.Services.GetRequiredService<IKernelBootstrapService>();
            IKernelPathService kernelPathService = _host.Services.GetRequiredService<IKernelPathService>();
            IGeoDataService geoDataService = _host.Services.GetRequiredService<IGeoDataService>();
            IProcessService processService = _host.Services.GetRequiredService<IProcessService>();
            ITunService tunService = _host.Services.GetRequiredService<ITunService>();
            ISystemProxyService systemProxyService = _host.Services.GetRequiredService<ISystemProxyService>();

            try
            {
                bool kernelReady = await kernelBootstrapService.EnsureKernelReadyAsync();
                if (!kernelReady)
                {
                    logService.Add("Kernel bootstrap failed. Skip Mihomo startup.", LogLevel.Error);
                    InitializeTray();
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

                if (controllerReady)
                {
                    controllerReady = await ValidateStartupTunRuntimeAsync(
                        processService,
                        tunService,
                        kernelPathService,
                        logService,
                        startupConfigPath);
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
                                controllerReady = await ValidateStartupTunRuntimeAsync(
                                    processService,
                                    tunService,
                                    kernelPathService,
                                    logService,
                                    fallbackConfigPath);
                                if (controllerReady)
                                {
                                    processService.ResetFailureDiagnostic();
                                    startupConfigPath = fallbackConfigPath;
                                }
                            }
                        }
                    }
                    else if (!shouldFallbackToDefaultConfig)
                    {
                        logService.Add(
                            $"Skip fallback to default startup config because TUN startup validation failed. Detail={diagnostic.Message}",
                            LogLevel.Warning);
                    }
                }

                if (!controllerReady)
                {
                    logService.Add($"Mihomo controller not ready: {processService.ControllerHost}:{processService.ControllerPort}", LogLevel.Error);
                    InitializeTray();
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
                InitializeTray();
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

            ElevationRelaunchOutcome outcome = AppElevationHelper.TryRelaunchAsAdministrator();
            switch (outcome.Status)
            {
                case ElevationRelaunchStatus.Relaunched:
                    logService.Add(
                        $"Startup config enables TUN. Relaunching as administrator. Mode={(isPackaged ? "packaged" : "unpackaged")}; " +
                        $"StartupConfig={startupConfigPath}; Target={outcome.Target.ExecutablePath}");
                    return true;
                case ElevationRelaunchStatus.UserCancelled:
                    logService.Add(
                        $"Administrator relaunch cancelled by user. Continue without elevation. " +
                        $"Mode={(isPackaged ? "packaged" : "unpackaged")}; StartupConfig={startupConfigPath}; " +
                        $"Target={outcome.Target.ExecutablePath}; Detail={outcome.Message}",
                        LogLevel.Warning);
                    return false;
                default:
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
            try
            {
                _trayService ??= _host.Services.GetRequiredService<ITrayService>();
                if (_trayService.IsInitialized)
                {
                    _trayService.Show();
                    return;
                }

                _trayService.Initialize(
                    showMainWindowAction: ShowMainWindow,
                    exitApplicationAsyncAction: RequestExitAsync);
                _trayService.Show();
            }
            catch (Exception ex)
            {
                _host.Services.GetRequiredService<IAppLogService>()
                    .Add($"Initialize tray failed: {ex}", LogLevel.Error);
            }
        }

        private void ShowMainWindow()
        {
            if (_window is null)
            {
                return;
            }

            if (_window is MainWindow mainWindow)
            {
                _ = mainWindow.RestoreFromBackgroundAsync();
                return;
            }

            WindowExtensions.Show(_window);
            _window.Activate();
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

        private async Task CleanupRuntimeAsync()
        {
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

                await CleanupRuntimeAsync();
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
                _trayService?.Shutdown();
                _host.Services.GetRequiredService<IHomeOverviewSamplerService>().FlushState();
                CleanupRuntimeAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore cleanup errors for best-effort handling.
            }
        }
    }
}

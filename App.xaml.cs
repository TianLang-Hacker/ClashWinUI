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
        private int _shutdownRequested;

        public App()
        {
            InitializeComponent();

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(static (_, services) =>
                {
                    services.AddSingleton(static _ => (LocalizedStrings)Application.Current.Resources["L"]);

                    services.AddSingleton<IThemeService, ThemeService>();
                    services.AddSingleton<IAppLogService, AppLogService>();
                    services.AddSingleton<IConfigService, ConfigService>();
                    services.AddSingleton<IKernelPathService, KernelPathService>();
                    services.AddSingleton<IKernelBootstrapService, KernelBootstrapService>();
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
                })
                .Build();

            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
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
            try
            {
                _window = _host.Services.GetRequiredService<MainWindow>();
                _window.Activate();

                await _host.StartAsync();
                _host.Services.GetRequiredService<IHomeOverviewSamplerService>().Start();
                await InitializeStartupPipelineAsync();
                _ = RunStartupUpdateCheckAsync();
            }
            catch (Exception ex)
            {
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

        private async Task InitializeStartupPipelineAsync()
        {
            IAppLogService logService = _host.Services.GetRequiredService<IAppLogService>();
            IKernelBootstrapService kernelBootstrapService = _host.Services.GetRequiredService<IKernelBootstrapService>();
            IGeoDataService geoDataService = _host.Services.GetRequiredService<IGeoDataService>();
            IConfigService configService = _host.Services.GetRequiredService<IConfigService>();
            IProfileService profileService = _host.Services.GetRequiredService<IProfileService>();
            IProcessService processService = _host.Services.GetRequiredService<IProcessService>();
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

                bool controllerReady = await StartAndWaitControllerReadyAsync(processService, startupConfigPath);
                if (!controllerReady)
                {
                    controllerReady = await TryRecoverFromGeoDataFailureAsync(
                        processService,
                        geoDataService,
                        startupConfigPath);
                }

                if (!controllerReady)
                {
                    string fallbackConfigPath = processService.EnsureStartupConfigPath();
                    if (!string.Equals(fallbackConfigPath, startupConfigPath, StringComparison.OrdinalIgnoreCase))
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
                }

                if (!controllerReady)
                {
                    logService.Add($"Mihomo controller not ready: {processService.ControllerHost}:{processService.ControllerPort}", LogLevel.Error);
                    InitializeTray();
                    return;
                }

                int proxyPort = processService.ResolveProxyPort(startupConfigPath);
                await systemProxyService.EnableAsync("127.0.0.1", proxyPort, AppConstants.SystemProxyBypassList);
                processService.ResetFailureDiagnostic();

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

        private async Task<bool> TryRecoverFromGeoDataFailureAsync(
            IProcessService processService,
            IGeoDataService geoDataService,
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
                processService.ResetFailureDiagnostic();
            }

            return controllerReady;
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
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

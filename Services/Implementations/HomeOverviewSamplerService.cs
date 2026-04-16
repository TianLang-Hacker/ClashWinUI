using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using ClashWinUI.Helpers;
using ClashWinUI.Services.Implementations.Native;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Implementations
{
    public sealed class HomeOverviewSamplerService : IHomeOverviewSamplerService
    {
        private const string UnavailableText = "--";
        private const int ChartCapacity = 60;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan KernelVersionRefreshInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TunRuntimeRefreshInterval = TimeSpan.FromSeconds(4);
        private static readonly double MemoryChartDefaultAxisMax = 10d * 1024d * 1024d;

        private readonly IMihomoService _mihomoService;
        private readonly IProcessService _processService;
        private readonly IProfileService _profileService;
        private readonly IConfigService _configService;
        private readonly IKernelPathService _kernelPathService;
        private readonly ISystemProxyService _systemProxyService;
        private readonly ITunService _tunService;
        private readonly IAppLogService _logService;
        private readonly IHomeChartStateService _homeChartStateService;
        private readonly Queue<double> _downloadSpeedSamples = CreateInitialSampleQueue();
        private readonly Queue<double> _uploadSpeedSamples = CreateInitialSampleQueue();
        private readonly Queue<double> _memoryUsageSamples = CreateInitialSampleQueue();
        private readonly object _stateGate = new();

        private CancellationTokenSource? _samplingCancellation;
        private Task? _samplingTask;
        private DateTimeOffset _lastKernelVersionRefresh = DateTimeOffset.MinValue;
        private string _lastKernelVersionText = UnavailableText;
        private ProfileItem? _activeProfile;
        private string? _cachedSummaryProfileId;
        private string? _cachedSummaryRuntimePath;
        private string _cachedMixinPortsText = UnavailableText;
        private string _cachedRulesCountText = UnavailableText;
        private bool _cachedTunConfigured;
        private bool _summaryCacheDirty = true;
        private bool _tunRuntimeCacheDirty = true;
        private int _connectionsCount;
        private long? _memoryUsageBytes;
        private long _downloadTotalBytes;
        private long _downloadSpeedBytes;
        private long _uploadTotalBytes;
        private long _uploadSpeedBytes;
        private int _runtimeEventsCount;
        private SystemProxyState _systemProxyState = SystemProxyState.Disabled();
        private TunRuntimeStatus _tunRuntimeStatus = TunRuntimeStatus.Disabled();
        private TunRuntimeStatus _cachedTunRuntimeSample = TunRuntimeStatus.Disabled();
        private DateTimeOffset _lastTunRuntimeRefresh = DateTimeOffset.MinValue;
        private string? _cachedTunRuntimeConfigPath;
        private string _lastTunRuntimeWarningFingerprint = string.Empty;
        private bool _lastObservedProcessRunning;
        private string? _lastObservedProcessConfigPath;
        private DateTimeOffset _lastObservedFailureDiagnosticAt = DateTimeOffset.MinValue;
        private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;
        private bool _isDisposed;

        public event EventHandler? StateChanged;

        public HomeOverviewSamplerService(
            IMihomoService mihomoService,
            IProcessService processService,
            IProfileService profileService,
            IConfigService configService,
            IKernelPathService kernelPathService,
            ISystemProxyService systemProxyService,
            ITunService tunService,
            IAppLogService logService,
            IHomeChartStateService homeChartStateService)
        {
            _mihomoService = mihomoService;
            _processService = processService;
            _profileService = profileService;
            _configService = configService;
            _kernelPathService = kernelPathService;
            _systemProxyService = systemProxyService;
            _tunService = tunService;
            _logService = logService;
            _homeChartStateService = homeChartStateService;

            RestoreChartState(_homeChartStateService.GetState());
            _activeProfile = _profileService.GetActiveProfile();
            _profileService.ActiveProfileChanged += OnActiveProfileChanged;
            _configService.ConfigurationChanged += OnConfigurationChanged;
            _mihomoService.ConfigApplied += OnMihomoConfigApplied;
        }

        public void Start()
        {
            if (_isDisposed || _samplingCancellation is not null)
            {
                return;
            }

            _samplingCancellation = new CancellationTokenSource();
            _samplingTask = RunSamplingLoopAsync(_samplingCancellation.Token);
        }

        public HomeOverviewState GetState()
        {
            lock (_stateGate)
            {
                return CreateStateSnapshot();
            }
        }

        public void FlushState()
        {
            lock (_stateGate)
            {
                _homeChartStateService.Save(new HomeChartState
                {
                    DownloadValues = _downloadSpeedSamples.ToArray(),
                    UploadValues = _uploadSpeedSamples.ToArray(),
                    MemoryValues = _memoryUsageSamples.ToArray(),
                    TrafficAxisMax = GetTrafficAxisMax(),
                    MemoryAxisMax = GetMemoryAxisMax(),
                    UpdatedAt = _updatedAt,
                });
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _profileService.ActiveProfileChanged -= OnActiveProfileChanged;
            _configService.ConfigurationChanged -= OnConfigurationChanged;
            _mihomoService.ConfigApplied -= OnMihomoConfigApplied;
            FlushState();
            CancellationTokenSource? cancellation = _samplingCancellation;
            _samplingCancellation = null;
            _samplingTask = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
        }

        private async Task RunSamplingLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await RefreshOnceAsync(forceKernelVersionRefresh: true, cancellationToken).ConfigureAwait(false);

                using var timer = new PeriodicTimer(RefreshInterval);
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    await RefreshOnceAsync(forceKernelVersionRefresh: false, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the app shuts down.
            }
        }

        private async Task RefreshOnceAsync(bool forceKernelVersionRefresh, CancellationToken cancellationToken)
        {
            try
            {
                ProfileItem? activeProfile = GetActiveProfileSnapshot();
                IReadOnlyList<ConnectionEntry> connections = await _mihomoService.GetConnectionsAsync(cancellationToken).ConfigureAwait(false);
                int connectionsCount = connections.Count;
                long downloadSpeedBytes = connections.Sum(item => item.DownloadSpeed);
                long uploadSpeedBytes = connections.Sum(item => item.UploadSpeed);
                long downloadTotalBytes = connections.Sum(item => item.Download);
                long uploadTotalBytes = connections.Sum(item => item.Upload);
                int runtimeEventsCount = _logService.Count;
                long? memoryUsageBytes = _processService.GetMihomoMemoryUsageBytes();
                UpdateRuntimeSummaryCacheIfNeeded(activeProfile);
                SystemProxyState systemProxyState = _systemProxyService.GetCurrentState();
                string? currentConfigPath = _processService.CurrentConfigPath;
                TunRuntimeStatus tunRuntimeStatus = ResolveTunRuntimeStatus(
                    currentConfigPath,
                    DetermineTunRuntimeRefreshNeed(currentConfigPath, _processService.IsRunning, _processService.LastFailureDiagnostic.OccurredAt));

                string kernelVersionText = await ResolveKernelVersionTextAsync(forceKernelVersionRefresh, cancellationToken).ConfigureAwait(false);

                lock (_stateGate)
                {
                    _connectionsCount = connectionsCount;
                    _memoryUsageBytes = memoryUsageBytes;
                    _downloadTotalBytes = downloadTotalBytes;
                    _downloadSpeedBytes = downloadSpeedBytes;
                    _uploadTotalBytes = uploadTotalBytes;
                    _uploadSpeedBytes = uploadSpeedBytes;
                    _runtimeEventsCount = runtimeEventsCount;
                    _systemProxyState = systemProxyState;
                    _tunRuntimeStatus = tunRuntimeStatus;
                    _lastKernelVersionText = kernelVersionText;
                    _updatedAt = DateTimeOffset.UtcNow;
                    EnqueueSample(_downloadSpeedSamples, downloadSpeedBytes);
                    EnqueueSample(_uploadSpeedSamples, uploadSpeedBytes);
                    EnqueueSample(_memoryUsageSamples, memoryUsageBytes ?? 0);
                }

                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.Add($"Home overview sampling failed: {ex.Message}", LogLevel.Warning);
            }
        }

        private async Task<string> ResolveKernelVersionTextAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            if (!_processService.IsRunning)
            {
                return UnavailableText;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!forceRefresh
                && !string.Equals(_lastKernelVersionText, UnavailableText, StringComparison.Ordinal)
                && now - _lastKernelVersionRefresh < KernelVersionRefreshInterval)
            {
                return _lastKernelVersionText;
            }

            string? version = await _mihomoService.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(version))
            {
                _lastKernelVersionRefresh = now;
                _lastKernelVersionText = version.Trim();
                return _lastKernelVersionText;
            }

            return _lastKernelVersionText;
        }

        private TunRuntimeStatus ResolveTunRuntimeStatus(string? currentConfigPath, bool forceRefresh)
        {
            string? configPath = null;

            if (!string.IsNullOrWhiteSpace(currentConfigPath) && _tunService.IsTunEnabled(currentConfigPath))
            {
                configPath = currentConfigPath;
            }
            else
            {
                lock (_stateGate)
                {
                    if (_cachedTunConfigured && !string.IsNullOrWhiteSpace(_cachedSummaryRuntimePath))
                    {
                        configPath = _cachedSummaryRuntimePath;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(configPath) || !_tunService.IsTunEnabled(configPath))
            {
                lock (_stateGate)
                {
                    _cachedTunRuntimeSample = TunRuntimeStatus.Disabled();
                    _cachedTunRuntimeConfigPath = null;
                    _lastTunRuntimeRefresh = DateTimeOffset.MinValue;
                    _lastTunRuntimeWarningFingerprint = string.Empty;
                    _tunRuntimeCacheDirty = false;
                }

                return TunRuntimeStatus.Disabled();
            }

            TunRuntimeStatus cachedStatus;
            DateTimeOffset lastRefreshAt;
            string? cachedConfigPath;
            bool cacheDirty;

            lock (_stateGate)
            {
                cachedStatus = _cachedTunRuntimeSample;
                lastRefreshAt = _lastTunRuntimeRefresh;
                cachedConfigPath = _cachedTunRuntimeConfigPath;
                cacheDirty = _tunRuntimeCacheDirty;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool cacheMatchesConfig = string.Equals(cachedConfigPath, configPath, StringComparison.OrdinalIgnoreCase);
            if (!forceRefresh
                && !cacheDirty
                && cacheMatchesConfig
                && now - lastRefreshAt < TunRuntimeRefreshInterval)
            {
                return TunRuntimeDiagnosticHelper.ApplyPreferredKernelDiagnostic(_processService, cachedStatus);
            }

            TunRuntimeStatus rawStatus;
            bool readSucceeded;
            string warningDetail = string.Empty;

            try
            {
                rawStatus = _tunService.GetRuntimeStatus(configPath, _kernelPathService.ResolveKernelPath());
                readSucceeded = !IsTunRuntimeInspectionReadFailure(rawStatus);
            }
            catch (Exception ex)
            {
                rawStatus = new TunRuntimeStatus
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
                    Message = IpHelperNative.RuntimeInspectionFailureMessage,
                };
                readSucceeded = false;
                warningDetail = ex.Message;
            }

            TunRuntimeStatus decoratedStatus = TunRuntimeDiagnosticHelper.ApplyPreferredKernelDiagnostic(_processService, rawStatus);
            if (readSucceeded)
            {
                lock (_stateGate)
                {
                    _cachedTunRuntimeSample = TunRuntimeDiagnosticHelper.Clone(decoratedStatus);
                    _cachedTunRuntimeConfigPath = configPath;
                    _lastTunRuntimeRefresh = now;
                    _lastTunRuntimeWarningFingerprint = string.Empty;
                    _tunRuntimeCacheDirty = false;
                }

                return decoratedStatus;
            }

            if (cacheMatchesConfig)
            {
                LogTunRuntimeReadWarningIfNeeded(configPath, rawStatus.Message, warningDetail);

                lock (_stateGate)
                {
                    _lastTunRuntimeRefresh = now;
                    _tunRuntimeCacheDirty = false;
                }

                return TunRuntimeDiagnosticHelper.ApplyPreferredKernelDiagnostic(_processService, cachedStatus);
            }

            lock (_stateGate)
            {
                _cachedTunRuntimeSample = TunRuntimeDiagnosticHelper.Clone(decoratedStatus);
                _cachedTunRuntimeConfigPath = configPath;
                _lastTunRuntimeRefresh = now;
                _tunRuntimeCacheDirty = false;
            }

            LogTunRuntimeReadWarningIfNeeded(configPath, rawStatus.Message, warningDetail);
            return decoratedStatus;
        }

        private bool DetermineTunRuntimeRefreshNeed(string? currentConfigPath, bool isProcessRunning, DateTimeOffset failureDiagnosticAt)
        {
            lock (_stateGate)
            {
                bool shouldRefresh = _tunRuntimeCacheDirty
                    || !string.Equals(_lastObservedProcessConfigPath, currentConfigPath, StringComparison.OrdinalIgnoreCase)
                    || _lastObservedProcessRunning != isProcessRunning
                    || _lastObservedFailureDiagnosticAt != failureDiagnosticAt;

                _lastObservedProcessConfigPath = currentConfigPath;
                _lastObservedProcessRunning = isProcessRunning;
                _lastObservedFailureDiagnosticAt = failureDiagnosticAt;
                return shouldRefresh;
            }
        }

        private bool IsTunRuntimeInspectionReadFailure(TunRuntimeStatus runtimeStatus)
        {
            return runtimeStatus.FailureKind == MihomoFailureKind.TunDependency
                && string.Equals(
                    runtimeStatus.Message?.Trim(),
                    IpHelperNative.RuntimeInspectionFailureMessage,
                    StringComparison.OrdinalIgnoreCase);
        }

        private void LogTunRuntimeReadWarningIfNeeded(string? configPath, string message, string warningDetail)
        {
            string fingerprint = $"{configPath}|{message}|{warningDetail}";

            lock (_stateGate)
            {
                if (string.Equals(_lastTunRuntimeWarningFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return;
                }

                _lastTunRuntimeWarningFingerprint = fingerprint;
            }

            string detail = string.IsNullOrWhiteSpace(warningDetail)
                ? message
                : $"{message} Detail={warningDetail}";
            _logService.Add($"TUN runtime sampling reused cached state because runtime inspection failed: {detail}", LogLevel.Warning);
        }

        private static TunRuntimeStatus CloneTunRuntimeStatus(TunRuntimeStatus status)
        {
            return new TunRuntimeStatus
            {
                IsConfigured = status.IsConfigured,
                IsHealthy = status.IsHealthy,
                DriverLoaded = status.DriverLoaded,
                DriverVersion = status.DriverVersion,
                AdapterPresent = status.AdapterPresent,
                AdapterName = status.AdapterName,
                RouteAttached = status.RouteAttached,
                EffectiveStack = status.EffectiveStack,
                FirewallEnabled = status.FirewallEnabled,
                DnsHijackConfigured = status.DnsHijackConfigured,
                DnsManaged = status.DnsManaged,
                DnsAutoGenerated = status.DnsAutoGenerated,
                FailureKind = status.FailureKind,
                Message = status.Message,
            };
        }

        private HomeOverviewState CreateStateSnapshot()
        {
            return new HomeOverviewState
            {
                ConnectionsCount = _connectionsCount,
                MemoryUsageBytes = _memoryUsageBytes,
                DownloadTotalBytes = _downloadTotalBytes,
                DownloadSpeedBytes = _downloadSpeedBytes,
                UploadTotalBytes = _uploadTotalBytes,
                UploadSpeedBytes = _uploadSpeedBytes,
                RuntimeEventsCount = _runtimeEventsCount,
                SystemProxyState = new SystemProxyState
                {
                    IsEnabled = _systemProxyState.IsEnabled,
                    ProxyServer = _systemProxyState.ProxyServer,
                    BypassList = _systemProxyState.BypassList,
                },
                MixinPortsText = _cachedMixinPortsText,
                RulesCountText = _cachedRulesCountText,
                KernelVersionText = _lastKernelVersionText,
                TunRuntimeStatus = CloneTunRuntimeStatus(_tunRuntimeStatus),
                DownloadValues = _downloadSpeedSamples.ToArray(),
                UploadValues = _uploadSpeedSamples.ToArray(),
                MemoryValues = _memoryUsageSamples.ToArray(),
                TrafficAxisMax = GetTrafficAxisMax(),
                MemoryAxisMax = GetMemoryAxisMax(),
                UpdatedAt = _updatedAt,
            };
        }

        private void RestoreChartState(HomeChartState state)
        {
            ReplaceQueueValues(_downloadSpeedSamples, state.DownloadValues);
            ReplaceQueueValues(_uploadSpeedSamples, state.UploadValues);
            ReplaceQueueValues(_memoryUsageSamples, state.MemoryValues);
        }

        private ProfileItem? GetActiveProfileSnapshot()
        {
            lock (_stateGate)
            {
                return _activeProfile;
            }
        }

        private void OnActiveProfileChanged(object? sender, EventArgs e)
        {
            ProfileItem? activeProfile = _profileService.GetActiveProfile();
            lock (_stateGate)
            {
                _activeProfile = activeProfile;
                _summaryCacheDirty = true;
                _tunRuntimeCacheDirty = true;
            }
        }

        private void OnConfigurationChanged(object? sender, EventArgs e)
        {
            lock (_stateGate)
            {
                _summaryCacheDirty = true;
                _tunRuntimeCacheDirty = true;
            }
        }

        private void OnMihomoConfigApplied(object? sender, string configPath)
        {
            lock (_stateGate)
            {
                _tunRuntimeCacheDirty = true;
            }
        }

        private void UpdateRuntimeSummaryCacheIfNeeded(ProfileItem? activeProfile)
        {
            string? activeProfileId = activeProfile?.Id;
            string runtimePath;
            bool shouldRefresh;

            lock (_stateGate)
            {
                shouldRefresh = _summaryCacheDirty
                    || !string.Equals(_cachedSummaryProfileId, activeProfileId, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(_cachedSummaryRuntimePath);
            }

            if (!shouldRefresh)
            {
                return;
            }

            runtimePath = activeProfile is not null
                ? _configService.GetRuntimePath(activeProfile)
                : _processService.EnsureStartupConfigPath();

            string mixinPortsText = UnavailableText;
            string rulesCountText = UnavailableText;
            bool isTunConfigured = _tunService.IsTunEnabled(runtimePath);

            if (activeProfile is not null)
            {
                MixinSettings mixin = _configService.LoadMixin(activeProfile);
                mixinPortsText = BuildMixinPortsSummary(mixin);
                rulesCountText = _configService.GetRuntimeRules(activeProfile).Count.ToString("N0", CultureInfo.CurrentCulture);
            }

            lock (_stateGate)
            {
                _cachedSummaryProfileId = activeProfileId;
                _cachedSummaryRuntimePath = runtimePath;
                _cachedMixinPortsText = mixinPortsText;
                _cachedRulesCountText = rulesCountText;
                _cachedTunConfigured = isTunConfigured;
                _summaryCacheDirty = false;
            }
        }

        private double GetTrafficAxisMax()
        {
            double maxValue = Math.Max(
                _downloadSpeedSamples.Count == 0 ? 0 : _downloadSpeedSamples.Max(),
                _uploadSpeedSamples.Count == 0 ? 0 : _uploadSpeedSamples.Max());

            return maxValue <= 0 ? 1 : GetNiceSpeedAxisMax(maxValue);
        }

        private double GetMemoryAxisMax()
        {
            double maxValue = _memoryUsageSamples.Count == 0 ? 0 : _memoryUsageSamples.Max();
            return maxValue <= 0 ? MemoryChartDefaultAxisMax : GetNiceMemoryAxisMax(maxValue);
        }

        private static Queue<double> CreateInitialSampleQueue()
        {
            return new Queue<double>(Enumerable.Repeat(0d, ChartCapacity));
        }

        private static void EnqueueSample(Queue<double> queue, double value)
        {
            queue.Enqueue(value);
            while (queue.Count > ChartCapacity)
            {
                queue.Dequeue();
            }
        }

        private static void ReplaceQueueValues(Queue<double> target, IReadOnlyList<double> source)
        {
            target.Clear();
            foreach (double value in source.TakeLast(ChartCapacity))
            {
                target.Enqueue(value);
            }

            while (target.Count < ChartCapacity)
            {
                target.Enqueue(0d);
            }
        }

        private static double GetNiceSpeedAxisMax(double value)
        {
            return GetNiceAxisMax(Math.Max(1, value * 1.1));
        }

        private static double GetNiceMemoryAxisMax(double value)
        {
            double mebiBytes = Math.Max(1, value * 1.1) / (1024d * 1024d);
            double roundedMebiBytes = Math.Max(10, Math.Ceiling(mebiBytes / 10d) * 10d);
            return roundedMebiBytes * 1024d * 1024d;
        }

        private static double GetNiceAxisMax(double value)
        {
            if (value <= 0)
            {
                return 1;
            }

            double exponent = Math.Floor(Math.Log10(value));
            double fraction = value / Math.Pow(10, exponent);
            double niceFraction = fraction switch
            {
                <= 1 => 1,
                <= 2 => 2,
                <= 5 => 5,
                _ => 10,
            };

            return niceFraction * Math.Pow(10, exponent);
        }

        private static string BuildMixinPortsSummary(MixinSettings settings)
        {
            var parts = new List<string>();

            AddPortSummary(parts, "mixed", settings.MixedPort);
            AddPortSummary(parts, "http", settings.HttpPort);
            AddPortSummary(parts, "socks", settings.SocksPort);
            AddPortSummary(parts, "redir", settings.RedirPort);
            AddPortSummary(parts, "tproxy", settings.TProxyPort);

            return parts.Count == 0 ? UnavailableText : string.Join(", ", parts);
        }

        private static void AddPortSummary(List<string> parts, string label, int? port)
        {
            if (port.HasValue && port.Value > 0)
            {
                parts.Add($"{label}:{port.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }

    }
}
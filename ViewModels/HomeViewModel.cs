using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace ClashWinUI.ViewModels
{
    public partial class HomeViewModel : ObservableObject
    {
        private const string UnavailableText = "--";
        private const string MaskedPublicIpText = "***.***.***.***";
        private const int ChartCapacity = 60;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ChartRefreshInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan KernelVersionRefreshInterval = TimeSpan.FromSeconds(30);
        private static readonly double TimeAxisMidpoint = (ChartCapacity - 1d) / 2d;
        private static readonly double MemoryChartDefaultAxisMax = 10d * 1024d * 1024d;
        private static readonly string[] SpeedUnits = ["B/s", "KB/s", "MB/s", "GB/s"];
        private static readonly string[] MemoryUnits = ["B", "KiB", "MiB", "GiB"];

        private readonly LocalizedStrings _localizedStrings;
        private readonly IMihomoService _mihomoService;
        private readonly IProcessService _processService;
        private readonly IProfileService _profileService;
        private readonly IConfigService _configService;
        private readonly IAppLogService _logService;
        private readonly INetworkInfoService _networkInfoService;
        private readonly DispatcherQueue? _dispatcherQueue;
        private readonly Queue<double> _downloadSpeedSamples = CreateInitialSampleQueue();
        private readonly Queue<double> _uploadSpeedSamples = CreateInitialSampleQueue();
        private readonly Queue<double> _memoryUsageSamples = CreateInitialSampleQueue();
        private readonly ObservableCollection<double> _downloadSpeedValues = CreateInitialChartValues();
        private readonly ObservableCollection<double> _uploadSpeedValues = CreateInitialChartValues();
        private readonly ObservableCollection<double> _memoryUsageValues = CreateInitialChartValues();
        private readonly LineSeries<double> _downloadSeries;
        private readonly LineSeries<double> _uploadSeries;
        private readonly LineSeries<double> _memorySeries;
        private readonly Axis _trafficXAxis;
        private readonly Axis _trafficYAxis;
        private readonly Axis _memoryXAxis;
        private readonly Axis _memoryYAxis;

        private CancellationTokenSource? _refreshLoopCancellation;
        private Task? _refreshLoopTask;
        private bool _networkInfoRequested;
        private bool _networkInfoFailed;
        private DateTimeOffset _lastKernelVersionRefresh = DateTimeOffset.MinValue;
        private ElementTheme _currentChartTheme = ElementTheme.Dark;

        private int _connectionsCount;
        private long? _memoryUsageBytes;
        private long _downloadTotalBytes;
        private long _downloadSpeedBytes;
        private long _uploadTotalBytes;
        private long _uploadSpeedBytes;
        private int _runtimeEventsCount;

        [ObservableProperty]
        public partial string Title { get; set; }

        [ObservableProperty]
        public partial string ConnectionsCountText { get; set; }

        [ObservableProperty]
        public partial string MemoryUsageText { get; set; }

        [ObservableProperty]
        public partial string DownloadTotalText { get; set; }

        [ObservableProperty]
        public partial string DownloadSpeedText { get; set; }

        [ObservableProperty]
        public partial string UploadTotalText { get; set; }

        [ObservableProperty]
        public partial string UploadSpeedText { get; set; }

        [ObservableProperty]
        public partial IEnumerable<ISeries> TrafficSeries { get; set; }

        [ObservableProperty]
        public partial IEnumerable<ISeries> MemorySeries { get; set; }

        [ObservableProperty]
        public partial IEnumerable<ICartesianAxis> TrafficXAxes { get; set; }

        [ObservableProperty]
        public partial IEnumerable<ICartesianAxis> TrafficYAxes { get; set; }

        [ObservableProperty]
        public partial IEnumerable<ICartesianAxis> MemoryXAxes { get; set; }

        [ObservableProperty]
        public partial IEnumerable<ICartesianAxis> MemoryYAxes { get; set; }

        [ObservableProperty]
        public partial SolidColorPaint? TooltipBackgroundPaint { get; set; }

        [ObservableProperty]
        public partial SolidColorPaint? TooltipTextPaint { get; set; }

        [ObservableProperty]
        public partial string PublicIpText { get; set; }

        [ObservableProperty]
        public partial bool IsPublicIpVisible { get; set; }

        [ObservableProperty]
        public partial string DisplayedPublicIpText { get; set; }

        [ObservableProperty]
        public partial string PublicIpVisibilityGlyph { get; set; }

        [ObservableProperty]
        public partial string PublicIpVisibilityToolTipText { get; set; }

        [ObservableProperty]
        public partial string LocationText { get; set; }

        [ObservableProperty]
        public partial string AsNumberText { get; set; }

        [ObservableProperty]
        public partial string ServiceProviderText { get; set; }

        [ObservableProperty]
        public partial string OrganizationText { get; set; }

        [ObservableProperty]
        public partial string TimeZoneText { get; set; }

        [ObservableProperty]
        public partial string CoordinatesText { get; set; }

        [ObservableProperty]
        public partial string NetworkInfoStatusMessage { get; set; }

        [ObservableProperty]
        public partial string KernelVersionText { get; set; }

        [ObservableProperty]
        public partial string SystemProxyAddressText { get; set; }

        [ObservableProperty]
        public partial string MixinPortsText { get; set; }

        [ObservableProperty]
        public partial string RuntimeEventsText { get; set; }

        [ObservableProperty]
        public partial string RulesCountText { get; set; }

        [ObservableProperty]
        public partial string OperatingSystemInfoText { get; set; }

        [ObservableProperty]
        public partial string SystemVersionText { get; set; }

        [ObservableProperty]
        public partial bool IsChartsReady { get; set; }

        public HomeViewModel(
            LocalizedStrings localizedStrings,
            IMihomoService mihomoService,
            IProcessService processService,
            IProfileService profileService,
            IConfigService configService,
            IAppLogService logService,
            INetworkInfoService networkInfoService)
        {
            _localizedStrings = localizedStrings;
            _mihomoService = mihomoService;
            _processService = processService;
            _profileService = profileService;
            _configService = configService;
            _logService = logService;
            _networkInfoService = networkInfoService;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            _downloadSeries = CreateTrafficSeries(_downloadSpeedValues, _localizedStrings["HomeLegendDownload"]);
            _uploadSeries = CreateTrafficSeries(_uploadSpeedValues, _localizedStrings["HomeLegendUpload"]);
            _memorySeries = CreateMemorySeries(_memoryUsageValues, _localizedStrings["HomeLegendMemory"]);
            _trafficXAxis = CreateTimeAxis();
            _trafficYAxis = CreateTrafficYAxis(GetTrafficAxisMax());
            _memoryXAxis = CreateTimeAxis();
            _memoryYAxis = CreateMemoryYAxis(GetMemoryAxisMax());

            Title = _localizedStrings["PageOverview"];
            ConnectionsCountText = "0";
            MemoryUsageText = UnavailableText;
            DownloadTotalText = "0 B";
            DownloadSpeedText = "0 B/s";
            UploadTotalText = "0 B";
            UploadSpeedText = "0 B/s";
            TrafficSeries = [_downloadSeries, _uploadSeries];
            MemorySeries = [_memorySeries];
            TrafficXAxes = [_trafficXAxis];
            TrafficYAxes = [_trafficYAxis];
            MemoryXAxes = [_memoryXAxis];
            MemoryYAxes = [_memoryYAxis];
            TooltipBackgroundPaint = new SolidColorPaint(new SKColor(32, 32, 32, 240));
            TooltipTextPaint = new SolidColorPaint(SKColors.White);
            PublicIpText = UnavailableText;
            IsPublicIpVisible = false;
            DisplayedPublicIpText = UnavailableText;
            PublicIpVisibilityGlyph = "\uE8A7";
            PublicIpVisibilityToolTipText = _localizedStrings["HomeShowPublicIpTooltip"];
            LocationText = UnavailableText;
            AsNumberText = UnavailableText;
            ServiceProviderText = UnavailableText;
            OrganizationText = UnavailableText;
            TimeZoneText = UnavailableText;
            CoordinatesText = UnavailableText;
            NetworkInfoStatusMessage = string.Empty;
            KernelVersionText = UnavailableText;
            SystemProxyAddressText = UnavailableText;
            MixinPortsText = UnavailableText;
            RuntimeEventsText = "0";
            RulesCountText = UnavailableText;
            IsChartsReady = false;

            (OperatingSystemInfoText, SystemVersionText) = LoadSystemInformation();
        }

        public async Task InitializeAsync()
        {
            if (!_networkInfoRequested)
            {
                _networkInfoRequested = true;
                _ = LoadNetworkInfoAsync();
            }

            HomeOverviewSnapshot snapshot = await CollectOverviewSnapshotAsync(includeChartUpdate: true, forceKernelVersionRefresh: true, CancellationToken.None).ConfigureAwait(false);
            await ApplyOverviewSnapshotAsync(snapshot).ConfigureAwait(false);
        }

        public void ApplyChartTheme(ElementTheme theme)
        {
            _currentChartTheme = theme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
            if (!IsChartsReady)
            {
                return;
            }

            RefreshChartAppearance();
        }

        public void ActivateCharts()
        {
            if (IsChartsReady)
            {
                ApplyChartTheme(_currentChartTheme);
                RefreshChartAxes(GetTrafficAxisMax(), GetMemoryAxisMax());
                return;
            }

            LiveChartsBootstrapper.EnsureInitialized();
            IsChartsReady = true;
            RefreshChartAppearance();
            RefreshChartAxes(GetTrafficAxisMax(), GetMemoryAxisMax());
        }

        public void StartAutoRefresh()
        {
            if (_dispatcherQueue is null || _refreshLoopCancellation is not null)
            {
                return;
            }

            _refreshLoopCancellation = new CancellationTokenSource();
            _refreshLoopTask = RunRefreshLoopAsync(_refreshLoopCancellation.Token);
        }

        public void StopAutoRefresh()
        {
            CancellationTokenSource? cancellation = _refreshLoopCancellation;
            _refreshLoopCancellation = null;
            _refreshLoopTask = null;
            cancellation?.Cancel();
        }

        private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var timer = new PeriodicTimer(RefreshInterval);
                DateTimeOffset lastChartRefreshAt = DateTimeOffset.UtcNow;

                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    bool includeChartUpdate = now - lastChartRefreshAt >= ChartRefreshInterval;
                    HomeOverviewSnapshot snapshot = await CollectOverviewSnapshotAsync(includeChartUpdate, forceKernelVersionRefresh: false, cancellationToken).ConfigureAwait(false);
                    await ApplyOverviewSnapshotAsync(snapshot).ConfigureAwait(false);
                    if (includeChartUpdate)
                    {
                        lastChartRefreshAt = now;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the page leaves and auto-refresh is stopped.
            }
        }

        private async Task<HomeOverviewSnapshot> CollectOverviewSnapshotAsync(bool includeChartUpdate, bool forceKernelVersionRefresh, CancellationToken cancellationToken)
        {
            ProfileItem? activeProfile = _profileService.GetActiveProfile();
            string runtimePath = activeProfile is not null
                ? _configService.GetRuntimePath(activeProfile)
                : _processService.EnsureStartupConfigPath();

            IReadOnlyList<ConnectionEntry> connections = await _mihomoService.GetConnectionsAsync(cancellationToken).ConfigureAwait(false);
            int connectionsCount = connections.Count;
            long downloadSpeedBytes = connections.Sum(item => item.DownloadSpeed);
            long uploadSpeedBytes = connections.Sum(item => item.UploadSpeed);
            long downloadTotalBytes = connections.Sum(item => item.Download);
            long uploadTotalBytes = connections.Sum(item => item.Upload);
            int runtimeEventsCount = _logService.GetLogs().Count;
            long? memoryUsageBytes = _processService.GetMihomoMemoryUsageBytes();

            string systemProxyAddressText = $"127.0.0.1:{_processService.ResolveProxyPort(runtimePath).ToString(CultureInfo.InvariantCulture)}";
            string runtimeEventsText = runtimeEventsCount.ToString("N0", CultureInfo.CurrentCulture);

            string mixinPortsText;
            string rulesCountText;
            if (activeProfile is not null)
            {
                MixinSettings mixin = _configService.LoadMixin(activeProfile);
                mixinPortsText = BuildMixinPortsSummary(mixin);
                rulesCountText = _configService.GetRuntimeRules(activeProfile).Count.ToString("N0", CultureInfo.CurrentCulture);
            }
            else
            {
                mixinPortsText = UnavailableText;
                rulesCountText = UnavailableText;
            }

            string kernelVersionText = await ResolveKernelVersionTextAsync(forceKernelVersionRefresh, cancellationToken).ConfigureAwait(false);
            HomeChartUpdate? chartUpdate = BuildChartUpdate(downloadSpeedBytes, uploadSpeedBytes, memoryUsageBytes, includeChartUpdate);

            return new HomeOverviewSnapshot
            {
                ConnectionsCount = connectionsCount,
                MemoryUsageBytes = memoryUsageBytes,
                DownloadTotalBytes = downloadTotalBytes,
                DownloadSpeedBytes = downloadSpeedBytes,
                UploadTotalBytes = uploadTotalBytes,
                UploadSpeedBytes = uploadSpeedBytes,
                RuntimeEventsCount = runtimeEventsCount,
                RuntimeEventsText = runtimeEventsText,
                SystemProxyAddressText = systemProxyAddressText,
                MixinPortsText = mixinPortsText,
                RulesCountText = rulesCountText,
                KernelVersionText = kernelVersionText,
                ChartUpdate = chartUpdate,
            };
        }

        private async Task ApplyOverviewSnapshotAsync(HomeOverviewSnapshot snapshot)
        {
            if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            {
                ApplyOverviewSnapshot(snapshot);
                return;
            }

            var completion = new TaskCompletionSource<object?>();
            if (!_dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        ApplyOverviewSnapshot(snapshot);
                        completion.SetResult(null);
                    }
                    catch (Exception ex)
                    {
                        completion.SetException(ex);
                    }
                }))
            {
                throw new InvalidOperationException("Failed to enqueue HomePage UI update.");
            }

            await completion.Task.ConfigureAwait(false);
        }

        private void ApplyOverviewSnapshot(HomeOverviewSnapshot snapshot)
        {
            _connectionsCount = snapshot.ConnectionsCount;
            _memoryUsageBytes = snapshot.MemoryUsageBytes;
            _downloadTotalBytes = snapshot.DownloadTotalBytes;
            _downloadSpeedBytes = snapshot.DownloadSpeedBytes;
            _uploadTotalBytes = snapshot.UploadTotalBytes;
            _uploadSpeedBytes = snapshot.UploadSpeedBytes;
            _runtimeEventsCount = snapshot.RuntimeEventsCount;

            UpdateMetricTexts();

            SystemProxyAddressText = snapshot.SystemProxyAddressText;
            RuntimeEventsText = snapshot.RuntimeEventsText;
            MixinPortsText = snapshot.MixinPortsText;
            RulesCountText = snapshot.RulesCountText;
            KernelVersionText = snapshot.KernelVersionText;

            if (snapshot.ChartUpdate is not null)
            {
                ReplaceChartValues(_downloadSpeedValues, snapshot.ChartUpdate.DownloadValues);
                ReplaceChartValues(_uploadSpeedValues, snapshot.ChartUpdate.UploadValues);
                ReplaceChartValues(_memoryUsageValues, snapshot.ChartUpdate.MemoryValues);
                RefreshChartAxes(snapshot.ChartUpdate.TrafficAxisMax, snapshot.ChartUpdate.MemoryAxisMax);
            }
        }

        private async Task LoadNetworkInfoAsync()
        {
            PublicNetworkInfo? info = await _networkInfoService.GetPublicNetworkInfoAsync();
            if (info is null)
            {
                _networkInfoFailed = true;
                NetworkInfoStatusMessage = _localizedStrings["HomeNetworkInfoLookupFailed"];
                return;
            }

            _networkInfoFailed = false;
            PublicIpText = WithFallback(info.Ip);
            LocationText = WithFallback(info.Location);
            AsNumberText = WithFallback(info.AsNumber);
            ServiceProviderText = WithFallback(info.ServiceProvider);
            OrganizationText = WithFallback(info.Organization);
            TimeZoneText = WithFallback(info.TimeZone);
            CoordinatesText = WithFallback(info.Coordinates);
            NetworkInfoStatusMessage = string.Empty;
        }

        partial void OnPublicIpTextChanged(string value)
        {
            UpdatePublicIpPresentation();
        }

        partial void OnIsPublicIpVisibleChanged(bool value)
        {
            UpdatePublicIpPresentation();
        }

        [RelayCommand]
        private void TogglePublicIpVisibility()
        {
            IsPublicIpVisible = !IsPublicIpVisible;
        }

        [RelayCommand]
        private void CopyPublicIp()
        {
            if (string.IsNullOrWhiteSpace(PublicIpText) || string.Equals(PublicIpText, UnavailableText, StringComparison.Ordinal))
            {
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(PublicIpText);
            Clipboard.SetContent(dataPackage);
        }

        private async Task<string> ResolveKernelVersionTextAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            if (!_processService.IsRunning)
            {
                return UnavailableText;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!forceRefresh
                && !string.Equals(KernelVersionText, UnavailableText, StringComparison.Ordinal)
                && now - _lastKernelVersionRefresh < KernelVersionRefreshInterval)
            {
                return KernelVersionText;
            }

            string? version = await _mihomoService.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(version))
            {
                _lastKernelVersionRefresh = now;
                return version.Trim();
            }

            return string.Equals(KernelVersionText, UnavailableText, StringComparison.Ordinal)
                ? UnavailableText
                : KernelVersionText;
        }

        private void UpdateMetricTexts()
        {
            ConnectionsCountText = _connectionsCount.ToString("N0", CultureInfo.CurrentCulture);
            MemoryUsageText = _memoryUsageBytes.HasValue ? FormatBytes(_memoryUsageBytes.Value) : UnavailableText;
            DownloadTotalText = FormatBytes(_downloadTotalBytes);
            DownloadSpeedText = $"{FormatBytes(_downloadSpeedBytes)}/s";
            UploadTotalText = FormatBytes(_uploadTotalBytes);
            UploadSpeedText = $"{FormatBytes(_uploadSpeedBytes)}/s";
        }

        private HomeChartUpdate? BuildChartUpdate(long downloadSpeedBytes, long uploadSpeedBytes, long? memoryUsageBytes, bool includeChartUpdate)
        {
            EnqueueSample(_downloadSpeedSamples, downloadSpeedBytes);
            EnqueueSample(_uploadSpeedSamples, uploadSpeedBytes);
            EnqueueSample(_memoryUsageSamples, memoryUsageBytes ?? 0);

            if (!includeChartUpdate)
            {
                return null;
            }

            return new HomeChartUpdate
            {
                DownloadValues = _downloadSpeedSamples.ToArray(),
                UploadValues = _uploadSpeedSamples.ToArray(),
                MemoryValues = _memoryUsageSamples.ToArray(),
                TrafficAxisMax = GetTrafficAxisMax(),
                MemoryAxisMax = GetMemoryAxisMax(),
            };
        }

        private void RefreshChartAppearance()
        {
            bool isLightTheme = _currentChartTheme == ElementTheme.Light;
            SKColor axisLabelColor = isLightTheme
                ? new SKColor(0, 0, 0, 0x7A)
                : new SKColor(255, 255, 255, 0xA8);
            SKColor separatorColor = isLightTheme
                ? new SKColor(0, 0, 0, 0x20)
                : new SKColor(255, 255, 255, 0x24);
            SKColor zeroLineColor = isLightTheme
                ? new SKColor(0, 0, 0, 0x34)
                : new SKColor(255, 255, 255, 0x3A);

            TooltipBackgroundPaint = isLightTheme
                ? new SolidColorPaint(new SKColor(255, 255, 255, 245))
                : new SolidColorPaint(new SKColor(32, 32, 32, 240));
            TooltipTextPaint = isLightTheme
                ? new SolidColorPaint(new SKColor(17, 17, 17))
                : new SolidColorPaint(SKColors.White);

            string downloadName = _localizedStrings["HomeLegendDownload"];
            string uploadName = _localizedStrings["HomeLegendUpload"];
            string memoryName = _localizedStrings["HomeLegendMemory"];

            ConfigureLineSeries(
                _downloadSeries,
                _downloadSpeedValues,
                downloadName,
                isLightTheme ? new SKColor(0x0A, 0x84, 0xFF) : new SKColor(0x3C, 0xB6, 0xFF),
                point => FormatSpeedAxisValue(point.Coordinate.PrimaryValue));
            ConfigureLineSeries(
                _uploadSeries,
                _uploadSpeedValues,
                uploadName,
                isLightTheme ? new SKColor(0xFF, 0x7A, 0x00) : new SKColor(0xFF, 0xB1, 0x4A),
                point => FormatSpeedAxisValue(point.Coordinate.PrimaryValue));
            ConfigureLineSeries(
                _memorySeries,
                _memoryUsageValues,
                memoryName,
                isLightTheme ? new SKColor(0x19, 0x87, 0x54) : new SKColor(0x5D, 0xD3, 0x9E),
                point => FormatMemoryAxisValue(point.Coordinate.PrimaryValue));

            ConfigureTimeAxis(_trafficXAxis, axisLabelColor);
            ConfigureTimeAxis(_memoryXAxis, axisLabelColor);
            ConfigureYAxis(_trafficYAxis, axisLabelColor, separatorColor, zeroLineColor, FormatSpeedAxisValue);
            ConfigureYAxis(_memoryYAxis, axisLabelColor, separatorColor, zeroLineColor, FormatMemoryAxisValue);

            OnPropertyChanged(nameof(TrafficSeries));
            OnPropertyChanged(nameof(MemorySeries));
            OnPropertyChanged(nameof(TrafficXAxes));
            OnPropertyChanged(nameof(TrafficYAxes));
            OnPropertyChanged(nameof(MemoryXAxes));
            OnPropertyChanged(nameof(MemoryYAxes));
            OnPropertyChanged(nameof(TooltipBackgroundPaint));
            OnPropertyChanged(nameof(TooltipTextPaint));
        }

        private void RefreshChartAxes(double trafficAxisMax, double memoryAxisMax)
        {
            SetAxisRange(_trafficYAxis, trafficAxisMax);
            SetAxisRange(_memoryYAxis, memoryAxisMax);
            OnPropertyChanged(nameof(TrafficYAxes));
            OnPropertyChanged(nameof(MemoryYAxes));
        }

        private static void EnqueueSample(Queue<double> queue, double value)
        {
            queue.Enqueue(value);
            while (queue.Count > ChartCapacity)
            {
                queue.Dequeue();
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

        private static ObservableCollection<double> CreateInitialChartValues()
        {
            return new ObservableCollection<double>(Enumerable.Repeat(0d, ChartCapacity));
        }

        private static Queue<double> CreateInitialSampleQueue()
        {
            return new Queue<double>(Enumerable.Repeat(0d, ChartCapacity));
        }

        private static void ReplaceChartValues(ObservableCollection<double> target, IReadOnlyList<double> source)
        {
            if (target.Count != source.Count)
            {
                target.Clear();
                foreach (double value in source)
                {
                    target.Add(value);
                }

                return;
            }

            for (int i = 0; i < source.Count; i++)
            {
                if (Math.Abs(target[i] - source[i]) > double.Epsilon)
                {
                    target[i] = source[i];
                }
            }
        }

        private static LineSeries<double> CreateTrafficSeries(ObservableCollection<double> values, string name)
        {
            return new LineSeries<double>(values)
            {
                Name = name,
                GeometrySize = 0,
                LineSmoothness = 0,
                Fill = null,
                GeometryFill = null,
                GeometryStroke = null,
            };
        }

        private static LineSeries<double> CreateMemorySeries(ObservableCollection<double> values, string name)
        {
            return new LineSeries<double>(values)
            {
                Name = name,
                GeometrySize = 0,
                LineSmoothness = 0,
                Fill = null,
                GeometryFill = null,
                GeometryStroke = null,
            };
        }

        private static Axis CreateTimeAxis()
        {
            return new Axis
            {
                MinLimit = 0,
                MaxLimit = ChartCapacity - 1,
                MinStep = TimeAxisMidpoint,
                ForceStepToMin = true,
                ShowSeparatorLines = false,
                TextSize = 11,
            };
        }

        private static Axis CreateTrafficYAxis(double axisMax)
        {
            return new Axis
            {
                MinLimit = 0,
                MaxLimit = axisMax,
                MinStep = Math.Max(axisMax / 2d, 1),
                ForceStepToMin = true,
                ShowSeparatorLines = true,
                TextSize = 11,
            };
        }

        private static Axis CreateMemoryYAxis(double axisMax)
        {
            return new Axis
            {
                MinLimit = 0,
                MaxLimit = axisMax,
                MinStep = Math.Max(axisMax / 2d, 1),
                ForceStepToMin = true,
                ShowSeparatorLines = true,
                TextSize = 11,
            };
        }

        private static void ConfigureLineSeries(
            LineSeries<double> series,
            ObservableCollection<double> values,
            string name,
            SKColor color,
            Func<LiveChartsCore.Kernel.ChartPoint, string> yFormatter)
        {
            series.Values = values;
            series.Name = name;
            series.Stroke = new SolidColorPaint(color, 2f);
            series.Fill = null;
            series.GeometryFill = null;
            series.GeometryStroke = null;
            series.GeometrySize = 0;
            series.LineSmoothness = 0;
            series.XToolTipLabelFormatter = static point => BuildTimeTooltipLabel(point.Coordinate.SecondaryValue);
            series.YToolTipLabelFormatter = yFormatter;
        }

        private static void ConfigureTimeAxis(Axis axis, SKColor labelColor)
        {
            axis.MinLimit = 0;
            axis.MaxLimit = ChartCapacity - 1;
            axis.MinStep = TimeAxisMidpoint;
            axis.ForceStepToMin = true;
            axis.ShowSeparatorLines = false;
            axis.TextSize = 11;
            axis.Labeler = BuildTimeAxisLabel;
            axis.LabelsPaint = new SolidColorPaint(labelColor);
            axis.SeparatorsPaint = null;
            axis.ZeroPaint = null;
            axis.TicksPaint = null;
        }

        private static void ConfigureYAxis(
            Axis axis,
            SKColor labelColor,
            SKColor separatorColor,
            SKColor zeroLineColor,
            Func<double, string> labeler)
        {
            axis.TextSize = 11;
            axis.Labeler = labeler;
            axis.LabelsPaint = new SolidColorPaint(labelColor);
            axis.SeparatorsPaint = new SolidColorPaint(separatorColor, 1f);
            axis.ZeroPaint = new SolidColorPaint(zeroLineColor, 1f);
            axis.TicksPaint = null;
            axis.ShowSeparatorLines = true;
        }

        private static void SetAxisRange(Axis axis, double axisMax)
        {
            axis.MinLimit = 0;
            axis.MaxLimit = axisMax;
            axis.MinStep = Math.Max(axisMax / 2d, 1);
            axis.ForceStepToMin = true;
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

        private static string FormatSpeedAxisValue(double value)
        {
            return FormatScaledValue(value, SpeedUnits, 1000d);
        }

        private static string FormatMemoryAxisValue(double value)
        {
            return FormatScaledValue(value, MemoryUnits, 1024d);
        }

        private static string FormatScaledValue(double value, IReadOnlyList<string> units, double unitBase)
        {
            if (value <= 0)
            {
                return $"0 {units[0]}";
            }

            double size = value;
            int unitIndex = 0;

            while (size >= unitBase && unitIndex < units.Count - 1)
            {
                size /= unitBase;
                unitIndex++;
            }

            string format = size >= 100 || unitIndex == 0 ? "0" : size >= 10 ? "0.0" : "0.##";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{size.ToString(format, CultureInfo.InvariantCulture)} {units[unitIndex]}");
        }

        private static string BuildSecondsAxisLabel(int seconds)
        {
            bool isChinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            return isChinese ? $"{seconds}秒" : $"{seconds}s";
        }

        private static string BuildNowAxisLabel()
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? "现在"
                : "Now";
        }

        private static string BuildLocalizedSecondsAxisLabel(int seconds)
        {
            bool isChinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            return isChinese ? $"{seconds}\u79D2" : $"{seconds}s";
        }

        private static string BuildLocalizedNowAxisLabel()
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? "\u73B0\u5728"
                : "Now";
        }

        private static string BuildTimeAxisLabel(double value)
        {
            if (value <= 0.75)
            {
                return BuildLocalizedSecondsAxisLabel(60);
            }

            if (Math.Abs(value - TimeAxisMidpoint) <= 0.75)
            {
                return BuildLocalizedSecondsAxisLabel(30);
            }

            if (value >= (ChartCapacity - 1) - 0.75)
            {
                return BuildLocalizedNowAxisLabel();
            }

            return string.Empty;
        }

        private static string BuildTimeTooltipLabel(double value)
        {
            int index = (int)Math.Round(Math.Clamp(value, 0, ChartCapacity - 1));
            int secondsAgo = ChartCapacity - 1 - index;
            return secondsAgo <= 0 ? BuildLocalizedNowAxisLabel() : BuildLocalizedSecondsAxisLabel(secondsAgo);
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

        private static (string OperatingSystemInfo, string SystemVersion) LoadSystemInformation()
        {
            const string keyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath);
                string productName = key?.GetValue("ProductName") as string ?? Environment.OSVersion.VersionString;
                string displayVersion = key?.GetValue("DisplayVersion") as string
                    ?? key?.GetValue("ReleaseId") as string
                    ?? string.Empty;
                string buildNumber = key?.GetValue("CurrentBuildNumber") as string
                    ?? key?.GetValue("CurrentBuild") as string
                    ?? string.Empty;
                string normalizedProductName = NormalizeOperatingSystemProductName(productName, buildNumber);

                string systemVersion = string.IsNullOrWhiteSpace(displayVersion)
                    ? WithFallback(buildNumber)
                    : string.IsNullOrWhiteSpace(buildNumber)
                        ? displayVersion
                        : $"{displayVersion}, {buildNumber}";

                return (WithFallback(normalizedProductName), systemVersion);
            }
            catch
            {
                string fallbackVersion = Environment.OSVersion.Version.ToString();
                return (Environment.OSVersion.VersionString, fallbackVersion);
            }
        }

        private static string NormalizeOperatingSystemProductName(string productName, string buildNumber)
        {
            if (string.IsNullOrWhiteSpace(productName))
            {
                return productName;
            }

            if (productName.StartsWith("Windows 11", StringComparison.OrdinalIgnoreCase))
            {
                return productName;
            }

            if (productName.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(buildNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out int build)
                && build >= 22000)
            {
                return "Windows 11" + productName["Windows 10".Length..];
            }

            return productName;
        }

        private static string FormatBytes(long value)
        {
            if (value <= 0)
            {
                return "0 B";
            }

            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = value;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            string format = size >= 100 || unitIndex == 0 ? "0" : size >= 10 ? "0.0" : "0.##";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{size.ToString(format, CultureInfo.InvariantCulture)} {units[unitIndex]}");
        }

        private static string WithFallback(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? UnavailableText : value.Trim();
        }

        private void UpdatePublicIpPresentation()
        {
            bool hasRealIp = !string.IsNullOrWhiteSpace(PublicIpText) && !string.Equals(PublicIpText, UnavailableText, StringComparison.Ordinal);

            DisplayedPublicIpText = hasRealIp
                ? IsPublicIpVisible ? PublicIpText : MaskedPublicIpText
                : UnavailableText;

            PublicIpVisibilityGlyph = IsPublicIpVisible ? "\uE890" : "\uE8A7";
            PublicIpVisibilityToolTipText = _localizedStrings[
                IsPublicIpVisible ? "HomeHidePublicIpTooltip" : "HomeShowPublicIpTooltip"];
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(LocalizedStrings.CurrentLanguage) && e.PropertyName != "Item[]")
            {
                return;
            }

            Title = _localizedStrings["PageOverview"];
            UpdateMetricTexts();
            RuntimeEventsText = _runtimeEventsCount.ToString("N0", CultureInfo.CurrentCulture);
            RulesCountText = RulesCountText == UnavailableText
                ? UnavailableText
                : RulesCountText;

            if (_networkInfoFailed)
            {
                NetworkInfoStatusMessage = _localizedStrings["HomeNetworkInfoLookupFailed"];
            }

            if (IsChartsReady)
            {
                RefreshChartAppearance();
                RefreshChartAxes(GetTrafficAxisMax(), GetMemoryAxisMax());
            }
            UpdatePublicIpPresentation();
        }

        private sealed class HomeOverviewSnapshot
        {
            public required int ConnectionsCount { get; init; }

            public required long? MemoryUsageBytes { get; init; }

            public required long DownloadTotalBytes { get; init; }

            public required long DownloadSpeedBytes { get; init; }

            public required long UploadTotalBytes { get; init; }

            public required long UploadSpeedBytes { get; init; }

            public required int RuntimeEventsCount { get; init; }

            public required string RuntimeEventsText { get; init; }

            public required string SystemProxyAddressText { get; init; }

            public required string MixinPortsText { get; init; }

            public required string RulesCountText { get; init; }

            public required string KernelVersionText { get; init; }

            public required HomeChartUpdate? ChartUpdate { get; init; }
        }

        private sealed class HomeChartUpdate
        {
            public required double[] DownloadValues { get; init; }

            public required double[] UploadValues { get; init; }

            public required double[] MemoryValues { get; init; }

            public required double TrafficAxisMax { get; init; }

            public required double MemoryAxisMax { get; init; }
        }
    }
}

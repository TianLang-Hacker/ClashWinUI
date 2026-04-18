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
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace ClashWinUI.ViewModels
{
    public partial class HomeViewModel : ObservableObject, IDisposable
    {
        private const string UnavailableText = "--";
        private const string MaskedPublicIpText = "***.***.***.***";
        private const int ChartCapacity = 60;
        private static readonly double TimeAxisMidpoint = (ChartCapacity - 1d) / 2d;
        private static readonly string[] SpeedUnits = ["B/s", "KB/s", "MB/s", "GB/s"];
        private static readonly string[] MemoryUnits = ["B", "KiB", "MiB", "GiB"];

        private readonly LocalizedStrings _localizedStrings;
        private readonly INetworkInfoService _networkInfoService;
        private readonly IHomeOverviewSamplerService _homeOverviewSamplerService;
        private readonly DispatcherQueue? _dispatcherQueue;
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

        private bool _networkInfoRequested;
        private bool _networkInfoFailed;
        private bool _isDisposed;
        private bool _isSubscribedToSampler;
        private ElementTheme _currentChartTheme = ElementTheme.Dark;

        private int _connectionsCount;
        private long? _memoryUsageBytes;
        private long _downloadTotalBytes;
        private long _downloadSpeedBytes;
        private long _uploadTotalBytes;
        private long _uploadSpeedBytes;
        private int _runtimeEventsCount;
        private SystemProxyState _systemProxyState = SystemProxyState.Disabled();
        private TunRuntimeStatus _tunRuntimeStatus = TunRuntimeStatus.Disabled();

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
        public partial string TunStatusText { get; set; }

        [ObservableProperty]
        public partial string TunSummaryText { get; set; }

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
            INetworkInfoService networkInfoService,
            IHomeOverviewSamplerService homeOverviewSamplerService)
        {
            _localizedStrings = localizedStrings;
            _networkInfoService = networkInfoService;
            _homeOverviewSamplerService = homeOverviewSamplerService;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            _downloadSeries = CreateTrafficSeries(_downloadSpeedValues, _localizedStrings["HomeLegendDownload"]);
            _uploadSeries = CreateTrafficSeries(_uploadSpeedValues, _localizedStrings["HomeLegendUpload"]);
            _memorySeries = CreateMemorySeries(_memoryUsageValues, _localizedStrings["HomeLegendMemory"]);
            _trafficXAxis = CreateTimeAxis();
            _trafficYAxis = CreateTrafficYAxis(1d);
            _memoryXAxis = CreateTimeAxis();
            _memoryYAxis = CreateMemoryYAxis(10d * 1024d * 1024d);

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
            PublicIpVisibilityGlyph = "\uE890";
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
            TunStatusText = UnavailableText;
            TunSummaryText = string.Empty;
            MixinPortsText = UnavailableText;
            RuntimeEventsText = "0";
            RulesCountText = UnavailableText;
            IsChartsReady = false;

            ApplyOverviewState(_homeOverviewSamplerService.GetState());
            (OperatingSystemInfoText, SystemVersionText) = LoadSystemInformation();
        }

        public async Task InitializeAsync()
        {
            if (!_networkInfoRequested)
            {
                _networkInfoRequested = true;
                _ = LoadNetworkInfoAsync();
            }

            HomeOverviewState state = _homeOverviewSamplerService.GetState();
            await ApplyOverviewStateAsync(state).ConfigureAwait(false);
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
                RefreshChartAxes(_trafficYAxis.MaxLimit ?? 1d, _memoryYAxis.MaxLimit ?? (10d * 1024d * 1024d));
                return;
            }

            LiveChartsBootstrapper.EnsureInitialized();
            IsChartsReady = true;
            RefreshChartAppearance();
            RefreshChartAxes(_trafficYAxis.MaxLimit ?? 1d, _memoryYAxis.MaxLimit ?? (10d * 1024d * 1024d));
        }

        public void StartAutoRefresh()
        {
            if (_isDisposed || _isSubscribedToSampler)
            {
                return;
            }

            _homeOverviewSamplerService.StateChanged += OnSamplerStateChanged;
            _isSubscribedToSampler = true;
        }

        public void StopAutoRefresh()
        {
            if (!_isSubscribedToSampler)
            {
                return;
            }

            _homeOverviewSamplerService.StateChanged -= OnSamplerStateChanged;
            _isSubscribedToSampler = false;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            StopAutoRefresh();
            _localizedStrings.PropertyChanged -= OnLocalizedStringsPropertyChanged;
        }

        private void OnSamplerStateChanged(object? sender, EventArgs e)
        {
            if (_isDisposed)
            {
                return;
            }

            _ = ApplyOverviewStateAsync(_homeOverviewSamplerService.GetState());
        }

        private async Task ApplyOverviewStateAsync(HomeOverviewState state)
        {
            if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            {
                ApplyOverviewState(state);
                return;
            }

            var completion = new TaskCompletionSource<object?>();
            if (!_dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        ApplyOverviewState(state);
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

        private void ApplyOverviewState(HomeOverviewState state)
        {
            _connectionsCount = state.ConnectionsCount;
            _memoryUsageBytes = state.MemoryUsageBytes;
            _downloadTotalBytes = state.DownloadTotalBytes;
            _downloadSpeedBytes = state.DownloadSpeedBytes;
            _uploadTotalBytes = state.UploadTotalBytes;
            _uploadSpeedBytes = state.UploadSpeedBytes;
            _runtimeEventsCount = state.RuntimeEventsCount;

            UpdateMetricTexts();

            _systemProxyState = state.SystemProxyState;
            _tunRuntimeStatus = state.TunRuntimeStatus;
            UpdateRuntimeStatusTexts();
            RuntimeEventsText = _runtimeEventsCount.ToString("N0", CultureInfo.CurrentCulture);
            MixinPortsText = state.MixinPortsText;
            RulesCountText = state.RulesCountText;
            KernelVersionText = state.KernelVersionText;

            ReplaceChartValues(_downloadSpeedValues, state.DownloadValues);
            ReplaceChartValues(_uploadSpeedValues, state.UploadValues);
            ReplaceChartValues(_memoryUsageValues, state.MemoryValues);
            RefreshChartAxes(state.TrafficAxisMax, state.MemoryAxisMax);
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

        private void UpdateMetricTexts()
        {
            ConnectionsCountText = _connectionsCount.ToString("N0", CultureInfo.CurrentCulture);
            MemoryUsageText = _memoryUsageBytes.HasValue ? FormatBytes(_memoryUsageBytes.Value) : UnavailableText;
            DownloadTotalText = FormatBytes(_downloadTotalBytes);
            DownloadSpeedText = $"{FormatBytes(_downloadSpeedBytes)}/s";
            UploadTotalText = FormatBytes(_uploadTotalBytes);
            UploadSpeedText = $"{FormatBytes(_uploadSpeedBytes)}/s";
        }

        private void UpdateRuntimeStatusTexts()
        {
            SystemProxyAddressText = RuntimeNetworkStatusTextHelper.BuildSystemProxyStatusText(_localizedStrings, _systemProxyState);
            (TunStatusText, TunSummaryText) = RuntimeNetworkStatusTextHelper.BuildTunPresentation(
                _localizedStrings,
                _tunRuntimeStatus,
                _systemProxyState);
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

        private static ObservableCollection<double> CreateInitialChartValues()
        {
            return new ObservableCollection<double>(Enumerable.Repeat(0d, ChartCapacity));
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

        private static string BuildLocalizedSecondsAxisLabel(int seconds)
        {
            bool isChinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            return isChinese ? $"{seconds}\u79d2" : $"{seconds}s";
        }

        private static string BuildLocalizedNowAxisLabel()
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? "\u73b0\u5728"
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

            PublicIpVisibilityGlyph = IsPublicIpVisible ? "\uE890" : "\uED1A";
            PublicIpVisibilityToolTipText = _localizedStrings[
                IsPublicIpVisible ? "HomeHidePublicIpTooltip" : "HomeShowPublicIpTooltip"];
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

            Title = _localizedStrings["PageOverview"];
            UpdateMetricTexts();
            UpdateRuntimeStatusTexts();
            RuntimeEventsText = _runtimeEventsCount.ToString("N0", CultureInfo.CurrentCulture);

            if (_networkInfoFailed)
            {
                NetworkInfoStatusMessage = _localizedStrings["HomeNetworkInfoLookupFailed"];
            }

            if (IsChartsReady)
            {
                RefreshChartAppearance();
                RefreshChartAxes(_trafficYAxis.MaxLimit ?? 1d, _memoryYAxis.MaxLimit ?? (10d * 1024d * 1024d));
            }

            UpdatePublicIpPresentation();
        }
    }
}

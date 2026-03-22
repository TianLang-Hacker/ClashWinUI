using ClashWinUI.Models;
using ClashWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class ConnectionsPage : Page, INotifyPropertyChanged
    {
        private const double PageHorizontalPadding = 48;
        private const double TableInnerHorizontalPadding = 24;
        private const double ColumnSpacing = 12;
        private const int ColumnCount = 10;
        private const double TechnicalMinimumColumnWidth = 1;

        private static readonly double[] InitialColumnMinimumWidths =
        [
            72,
            170,
            130,
            100,
            240,
            110,
            110,
            100,
            100,
            120,
        ];

        private static readonly double[] InitialColumnWeights =
        [
            0,
            1.8,
            1.2,
            0.9,
            3.0,
            1.0,
            1.0,
            0.9,
            0.9,
            1.1,
        ];

        private static double[]? s_sessionColumnWidths;

        private ConnectionsViewModel? _viewModel;
        private readonly ConnectionsColumnLayout _columnLayout;
        private bool _isSyncingHeaderScroll;
        private ConnectionColumn? _activeResizeColumn;
        private GridLength _closeColumnWidth = new(72);
        private GridLength _hostColumnWidth = new(170);
        private GridLength _typeColumnWidth = new(130);
        private GridLength _ruleColumnWidth = new(100);
        private GridLength _chainColumnWidth = new(240);
        private GridLength _downloadSpeedColumnWidth = new(110);
        private GridLength _uploadSpeedColumnWidth = new(110);
        private GridLength _downloadColumnWidth = new(100);
        private GridLength _uploadColumnWidth = new(100);
        private GridLength _durationColumnWidth = new(120);

        public event PropertyChangedEventHandler? PropertyChanged;

        public GridLength CloseColumnWidth
        {
            get => _closeColumnWidth;
            private set => SetProperty(ref _closeColumnWidth, value);
        }

        public GridLength HostColumnWidth
        {
            get => _hostColumnWidth;
            private set => SetProperty(ref _hostColumnWidth, value);
        }

        public GridLength TypeColumnWidth
        {
            get => _typeColumnWidth;
            private set => SetProperty(ref _typeColumnWidth, value);
        }

        public GridLength RuleColumnWidth
        {
            get => _ruleColumnWidth;
            private set => SetProperty(ref _ruleColumnWidth, value);
        }

        public GridLength ChainColumnWidth
        {
            get => _chainColumnWidth;
            private set => SetProperty(ref _chainColumnWidth, value);
        }

        public GridLength DownloadSpeedColumnWidth
        {
            get => _downloadSpeedColumnWidth;
            private set => SetProperty(ref _downloadSpeedColumnWidth, value);
        }

        public GridLength UploadSpeedColumnWidth
        {
            get => _uploadSpeedColumnWidth;
            private set => SetProperty(ref _uploadSpeedColumnWidth, value);
        }

        public GridLength DownloadColumnWidth
        {
            get => _downloadColumnWidth;
            private set => SetProperty(ref _downloadColumnWidth, value);
        }

        public GridLength UploadColumnWidth
        {
            get => _uploadColumnWidth;
            private set => SetProperty(ref _uploadColumnWidth, value);
        }

        public GridLength DurationColumnWidth
        {
            get => _durationColumnWidth;
            private set => SetProperty(ref _durationColumnWidth, value);
        }

        public ConnectionsPage()
        {
            InitializeComponent();
            _columnLayout = (ConnectionsColumnLayout)Resources["ConnectionsColumnLayoutResource"];
            SizeChanged += OnPageSizeChanged;
            ApplySessionOrResponsiveWidths(0);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is ConnectionsViewModel viewModel)
            {
                _viewModel = viewModel;
                DataContext = viewModel;
                await viewModel.InitializeAsync();
                viewModel.StartAutoRefresh();
            }

            ApplySessionOrResponsiveWidths(ActualWidth);
            SyncHeaderScrollToBody();
            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _viewModel?.StopAutoRefresh();
            base.OnNavigatedFrom(e);
        }

        private async void CloseConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null)
            {
                return;
            }

            if (sender is not FrameworkElement element || element.DataContext is not ConnectionEntry connection)
            {
                return;
            }

            if (_viewModel.CloseConnectionCommand.CanExecute(connection))
            {
                await _viewModel.CloseConnectionCommand.ExecuteAsync(connection);
            }
        }

        private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (sender is Thumb thumb && TryGetColumnFromTag(thumb.Tag, out ConnectionColumn column))
            {
                _activeResizeColumn = column;
            }
        }

        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb || !TryGetColumnFromTag(thumb.Tag, out ConnectionColumn column))
            {
                return;
            }

            double updatedWidth = GetColumnWidth(column) + e.HorizontalChange;
            SetColumnWidth(column, updatedWidth);
            SyncHeaderScrollToBody();
        }

        private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_activeResizeColumn is not null)
            {
                PersistSessionColumnWidths();
                _activeResizeColumn = null;
            }
        }

        private void BodyScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            SyncHeaderScrollToBody();
        }

        private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (HasSessionColumnWidths)
            {
                return;
            }

            UpdateResponsiveColumnWidths(e.NewSize.Width);
        }

        private void ApplySessionOrResponsiveWidths(double pageWidth)
        {
            if (HasSessionColumnWidths)
            {
                ApplyColumnWidths(s_sessionColumnWidths!);
                return;
            }

            UpdateResponsiveColumnWidths(pageWidth);
        }

        private void UpdateResponsiveColumnWidths(double pageWidth)
        {
            double effectivePageWidth = pageWidth > 0 ? pageWidth : ActualWidth;
            if (effectivePageWidth <= 0)
            {
                return;
            }

            double availableTableWidth = Math.Max(0, effectivePageWidth - PageHorizontalPadding - TableInnerHorizontalPadding);
            double availableColumnsWidth = Math.Max(0, availableTableWidth - (ColumnSpacing * (ColumnCount - 1)));
            double minimumColumnsWidth = 0;
            double totalWeight = 0;

            for (int i = 0; i < InitialColumnMinimumWidths.Length; i++)
            {
                minimumColumnsWidth += InitialColumnMinimumWidths[i];
                totalWeight += InitialColumnWeights[i];
            }

            double extraWidth = Math.Max(0, availableColumnsWidth - minimumColumnsWidth);
            var widths = new double[ColumnCount];
            for (int i = 0; i < ColumnCount; i++)
            {
                widths[i] = CreateResponsiveColumnWidth(i, extraWidth, totalWeight);
            }

            ApplyColumnWidths(widths);
        }

        private static double CreateResponsiveColumnWidth(int columnIndex, double extraWidth, double totalWeight)
        {
            double width = InitialColumnMinimumWidths[columnIndex];
            double weight = InitialColumnWeights[columnIndex];
            if (extraWidth > 0 && weight > 0 && totalWeight > 0)
            {
                width += extraWidth * (weight / totalWeight);
            }

            return width;
        }

        private static bool TryGetColumnFromTag(object? tag, out ConnectionColumn column)
        {
            if (tag is string text && Enum.TryParse(text, true, out column))
            {
                return true;
            }

            column = default;
            return false;
        }

        private bool HasSessionColumnWidths => s_sessionColumnWidths is { Length: ColumnCount };

        private void PersistSessionColumnWidths()
        {
            s_sessionColumnWidths = CaptureColumnWidths();
        }

        private double[] CaptureColumnWidths()
        {
            return
            [
                _columnLayout.CloseColumnWidth.Value,
                _columnLayout.HostColumnWidth.Value,
                _columnLayout.TypeColumnWidth.Value,
                _columnLayout.RuleColumnWidth.Value,
                _columnLayout.ChainColumnWidth.Value,
                _columnLayout.DownloadSpeedColumnWidth.Value,
                _columnLayout.UploadSpeedColumnWidth.Value,
                _columnLayout.DownloadColumnWidth.Value,
                _columnLayout.UploadColumnWidth.Value,
                _columnLayout.DurationColumnWidth.Value,
            ];
        }

        private void ApplyColumnWidths(IReadOnlyList<double> widths)
        {
            _columnLayout.CloseColumnWidth = CreateGridLength(widths[(int)ConnectionColumn.Close]);
            _columnLayout.HostColumnWidth = CreateGridLength(widths[(int)ConnectionColumn.Host]);
            _columnLayout.TypeColumnWidth = CreateGridLength(widths[(int)ConnectionColumn.Type]);
            _columnLayout.RuleColumnWidth = CreateGridLength(widths[(int)ConnectionColumn.Rule]);
            _columnLayout.ChainColumnWidth = CreateGridLength(widths[(int)ConnectionColumn.Chain]);
            _columnLayout.DownloadSpeedColumnWidth = CreateGridLength(widths[(int)ConnectionColumn.DownloadSpeed]);
            _columnLayout.UploadSpeedColumnWidth = CreateGridLength(widths[(int)ConnectionColumn.UploadSpeed]);
            _columnLayout.DownloadColumnWidth = CreateGridLength(widths[(int)ConnectionColumn.Download]);
            _columnLayout.UploadColumnWidth = CreateGridLength(widths[(int)ConnectionColumn.Upload]);
            _columnLayout.DurationColumnWidth = CreateGridLength(widths[(int)ConnectionColumn.Duration]);
        }

        private double GetColumnWidth(ConnectionColumn column)
        {
            return column switch
            {
                ConnectionColumn.Close => _columnLayout.CloseColumnWidth.Value,
                ConnectionColumn.Host => _columnLayout.HostColumnWidth.Value,
                ConnectionColumn.Type => _columnLayout.TypeColumnWidth.Value,
                ConnectionColumn.Rule => _columnLayout.RuleColumnWidth.Value,
                ConnectionColumn.Chain => _columnLayout.ChainColumnWidth.Value,
                ConnectionColumn.DownloadSpeed => _columnLayout.DownloadSpeedColumnWidth.Value,
                ConnectionColumn.UploadSpeed => _columnLayout.UploadSpeedColumnWidth.Value,
                ConnectionColumn.Download => _columnLayout.DownloadColumnWidth.Value,
                ConnectionColumn.Upload => _columnLayout.UploadColumnWidth.Value,
                ConnectionColumn.Duration => _columnLayout.DurationColumnWidth.Value,
                _ => TechnicalMinimumColumnWidth,
            };
        }

        private void SetColumnWidth(ConnectionColumn column, double width)
        {
            GridLength gridLength = CreateGridLength(width);
            switch (column)
            {
                case ConnectionColumn.Close:
                    _columnLayout.CloseColumnWidth = gridLength;
                    break;
                case ConnectionColumn.Host:
                    _columnLayout.HostColumnWidth = gridLength;
                    break;
                case ConnectionColumn.Type:
                    _columnLayout.TypeColumnWidth = gridLength;
                    break;
                case ConnectionColumn.Rule:
                    _columnLayout.RuleColumnWidth = gridLength;
                    break;
                case ConnectionColumn.Chain:
                    _columnLayout.ChainColumnWidth = gridLength;
                    break;
                case ConnectionColumn.DownloadSpeed:
                    _columnLayout.DownloadSpeedColumnWidth = gridLength;
                    break;
                case ConnectionColumn.UploadSpeed:
                    _columnLayout.UploadSpeedColumnWidth = gridLength;
                    break;
                case ConnectionColumn.Download:
                    _columnLayout.DownloadColumnWidth = gridLength;
                    break;
                case ConnectionColumn.Upload:
                    _columnLayout.UploadColumnWidth = gridLength;
                    break;
                case ConnectionColumn.Duration:
                    _columnLayout.DurationColumnWidth = gridLength;
                    break;
            }
        }

        private static GridLength CreateGridLength(double width)
        {
            return new GridLength(Math.Max(TechnicalMinimumColumnWidth, Math.Round(width, MidpointRounding.AwayFromZero)), GridUnitType.Pixel);
        }

        private void SyncHeaderScrollToBody()
        {
            if (_isSyncingHeaderScroll)
            {
                return;
            }

            _isSyncingHeaderScroll = true;
            try
            {
                HeaderScrollViewer.ChangeView(BodyScrollViewer.HorizontalOffset, null, null, true);
            }
            finally
            {
                _isSyncingHeaderScroll = false;
            }
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private enum ConnectionColumn
        {
            Close = 0,
            Host = 1,
            Type = 2,
            Rule = 3,
            Chain = 4,
            DownloadSpeed = 5,
            UploadSpeed = 6,
            Download = 7,
            Upload = 8,
            Duration = 9,
        }
    }
}

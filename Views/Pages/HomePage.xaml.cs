using ClashWinUI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class HomePage : Page
    {
        private HomeViewModel? _viewModel;
        private CancellationTokenSource? _chartLoadCancellation;
        private bool _chartsLoaded;

        public HomePage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is HomeViewModel viewModel)
            {
                ActualThemeChanged -= OnActualThemeChanged;
                _viewModel = viewModel;
                DataContext = viewModel;
                viewModel.ApplyChartTheme(ActualTheme);
                ActualThemeChanged += OnActualThemeChanged;
                await viewModel.InitializeAsync();
                viewModel.StartAutoRefresh();
                QueueChartsLoad(viewModel);
            }

            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ActualThemeChanged -= OnActualThemeChanged;
            _chartLoadCancellation?.Cancel();
            _viewModel?.StopAutoRefresh();
            base.OnNavigatedFrom(e);
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            _viewModel?.ApplyChartTheme(ActualTheme);
        }

        private void QueueChartsLoad(HomeViewModel viewModel)
        {
            if (_chartsLoaded)
            {
                viewModel.ActivateCharts();
                TrafficChartHost.Content = viewModel;
                MemoryChartHost.Content = viewModel;
                viewModel.ApplyChartTheme(ActualTheme);
                return;
            }

            _chartLoadCancellation?.Cancel();
            _chartLoadCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = _chartLoadCancellation.Token;
            _ = LoadChartsAsync(viewModel, cancellationToken);
        }

        private async Task LoadChartsAsync(HomeViewModel viewModel, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested || _viewModel != viewModel)
            {
                return;
            }

            if (DispatcherQueue is null)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (cancellationToken.IsCancellationRequested || _viewModel != viewModel || _chartsLoaded)
                {
                    return;
                }

                viewModel.ActivateCharts();
                TrafficChartHost.ContentTemplate = (DataTemplate)Resources["TrafficChartTemplate"];
                TrafficChartHost.Content = viewModel;
                MemoryChartHost.ContentTemplate = (DataTemplate)Resources["MemoryChartTemplate"];
                MemoryChartHost.Content = viewModel;
                _chartsLoaded = true;
                viewModel.ApplyChartTheme(ActualTheme);
            });
        }
    }
}

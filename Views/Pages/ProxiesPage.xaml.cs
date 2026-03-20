using ClashWinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class ProxiesPage : Page
    {
        private ProxiesViewModel? _viewModel;

        public ProxiesPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is ProxiesViewModel viewModel)
            {
                _viewModel = viewModel;
                DataContext = viewModel;
                await viewModel.InitializeAsync();
                viewModel.StartWatchingRuntimeChanges();
            }

            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _viewModel?.StopWatchingRuntimeChanges();
            base.OnNavigatedFrom(e);
        }
    }
}

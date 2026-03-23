using ClashWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class ProxiesPage : Page, IShellFreezablePage
    {
        private ProxiesViewModel? _viewModel;

        public ProxiesPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            ProxiesViewModel viewModel = ResolveViewModel();
            if (!ReferenceEquals(_viewModel, viewModel))
            {
                ReleaseViewModel();
                _viewModel = viewModel;
                DataContext = viewModel;
            }

            await viewModel.InitializeAsync();
            viewModel.StartWatchingRuntimeChanges();

            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ReleaseViewModel();
            base.OnNavigatedFrom(e);
        }

        private void ReleaseViewModel()
        {
            if (_viewModel is null)
            {
                DataContext = null;
                return;
            }

            _viewModel.StopWatchingRuntimeChanges();
            _viewModel.Dispose();
            _viewModel = null;
            DataContext = null;
        }

        private static ProxiesViewModel ResolveViewModel()
        {
            return ((App)Application.Current).GetRequiredService<ProxiesViewModel>();
        }

        public void PrepareForShellFreeze()
        {
            ReleaseViewModel();
        }
    }
}

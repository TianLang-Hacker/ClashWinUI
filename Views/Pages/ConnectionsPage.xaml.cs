using ClashWinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class ConnectionsPage : Page
    {
        public ConnectionsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is ConnectionsViewModel viewModel)
            {
                DataContext = viewModel;
            }

            base.OnNavigatedTo(e);
        }
    }
}

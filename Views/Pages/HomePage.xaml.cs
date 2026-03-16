using ClashWinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is HomeViewModel viewModel)
            {
                DataContext = viewModel;
            }

            base.OnNavigatedTo(e);
        }
    }
}

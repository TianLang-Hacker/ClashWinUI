using ClashWinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class RulesPage : Page
    {
        public RulesPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is RulesViewModel viewModel)
            {
                DataContext = viewModel;
            }

            base.OnNavigatedTo(e);
        }
    }
}

using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using System.Threading.Tasks;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class RulesPage : Page, IShellFreezablePage
    {
        private RulesViewModel? _viewModel;

        public RulesPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            RulesViewModel viewModel = ResolveViewModel();
            if (!ReferenceEquals(_viewModel, viewModel))
            {
                ReleaseViewModel();
                _viewModel = viewModel;
                DataContext = viewModel;
            }

            RebindRulesList();
            await viewModel.InitializeAsync();

            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ReleaseViewModel();
            base.OnNavigatedFrom(e);
        }

        private async void RuleToggleSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null
                || sender is not ToggleSwitch toggleSwitch
                || toggleSwitch.DataContext is not RuntimeRuleItem rule
                || rule.IsToggleSynchronizing)
            {
                return;
            }

            bool desiredState = toggleSwitch.IsOn;
            bool success = await _viewModel.SetRuleEnabledAsync(rule, desiredState);
            if (success)
            {
                return;
            }

            await RevertToggleAsync(toggleSwitch, rule);
        }

        private static Task RevertToggleAsync(ToggleSwitch toggleSwitch, RuntimeRuleItem rule)
        {
            rule.IsToggleSynchronizing = true;
            toggleSwitch.IsOn = rule.IsEnabled;
            rule.IsToggleSynchronizing = false;
            return Task.CompletedTask;
        }

        private void ReleaseViewModel()
        {
            RulesListView.ItemsSource = null;

            if (_viewModel is null)
            {
                DataContext = null;
                return;
            }

            DataContext = null;
            _viewModel.Dispose();
            _viewModel = null;
        }

        private static RulesViewModel ResolveViewModel()
        {
            return ((App)Application.Current).GetRequiredService<RulesViewModel>();
        }

        private void RebindRulesList()
        {
            if (RulesListView.ItemsSource is not null)
            {
                return;
            }

            RulesListView.SetBinding(ItemsControl.ItemsSourceProperty, new Binding
            {
                Path = new PropertyPath(nameof(RulesViewModel.Rules)),
            });
        }

        public void PrepareForShellFreeze()
        {
            ReleaseViewModel();
        }
    }
}

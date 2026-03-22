using ClashWinUI.Models;
using ClashWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Threading.Tasks;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class RulesPage : Page
    {
        private RulesViewModel? _viewModel;

        public RulesPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is RulesViewModel viewModel)
            {
                _viewModel = viewModel;
                DataContext = viewModel;
                await viewModel.InitializeAsync();
            }

            base.OnNavigatedTo(e);
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
    }
}

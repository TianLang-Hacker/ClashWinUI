using ClashWinUI.ViewModels;
using ClashWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private static PortSettingsWindow? _portSettingsWindow;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is SettingsViewModel viewModel)
            {
                DataContext = viewModel;
                viewModel.RefreshActiveProfileState();
            }

            base.OnNavigatedTo(e);
        }

        private async void BrowseKernelPathButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel viewModel)
            {
                return;
            }

            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add("*");

            if (Application.Current is App app && app.ActiveWindow is not null)
            {
                nint hwnd = WindowNative.GetWindowHandle(app.ActiveWindow);
                InitializeWithWindow.Initialize(picker, hwnd);
            }

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            viewModel.KernelPathInput = file.Path;
        }

        private void OpenPortSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel viewModel || !viewModel.HasActiveMixinProfile)
            {
                return;
            }

            if (_portSettingsWindow is not null)
            {
                _portSettingsWindow.Activate();
                return;
            }

            var window = new PortSettingsWindow(viewModel, viewModel.CreatePortSettingsDraft());
            if (Application.Current is App app && app.ActiveWindow is not null)
            {
                window.PositionNear(app.ActiveWindow);
            }

            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_portSettingsWindow, window))
                {
                    _portSettingsWindow = null;
                }
            };

            _portSettingsWindow = window;
            window.Activate();
        }

        private void UpdateGeoDataCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel viewModel)
            {
                return;
            }

            if (viewModel.UpdateGeoDataCommand.CanExecute(null))
            {
                viewModel.UpdateGeoDataCommand.Execute(null);
            }
        }
    }
}

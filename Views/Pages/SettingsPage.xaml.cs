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
    public sealed partial class SettingsPage : Page, IShellFreezablePage
    {
        private static PortSettingsWindow? _portSettingsWindow;
        private SettingsViewModel? _viewModel;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            SettingsViewModel viewModel = ResolveViewModel();
            if (!ReferenceEquals(_viewModel, viewModel))
            {
                ReleaseViewModel(disposeImmediately: true);
                _viewModel = viewModel;
                DataContext = viewModel;
            }

            _ = viewModel.InitializeAsync();

            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            bool canDisposeImmediately = _portSettingsWindow is null
                || !ReferenceEquals(_portSettingsWindow.SettingsViewModel, _viewModel);
            ReleaseViewModel(canDisposeImmediately);
            base.OnNavigatedFrom(e);
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

            var window = new PortSettingsWindow(viewModel, viewModel.CreatePortSettingsDraft(), viewModel.ThemeService);
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

                if (!ReferenceEquals(_viewModel, viewModel))
                {
                    viewModel.Dispose();
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

        private void ReleaseViewModel(bool disposeImmediately)
        {
            if (_viewModel is null)
            {
                DataContext = null;
                return;
            }

            SettingsViewModel viewModel = _viewModel;
            _viewModel = null;
            DataContext = null;

            if (disposeImmediately)
            {
                viewModel.Dispose();
            }
        }

        private static SettingsViewModel ResolveViewModel()
        {
            if (_portSettingsWindow is not null)
            {
                return _portSettingsWindow.SettingsViewModel;
            }

            return ((App)Application.Current).GetRequiredService<SettingsViewModel>();
        }

        public void PrepareForShellFreeze()
        {
            ReleaseViewModel(disposeImmediately: _portSettingsWindow is null);
        }
    }
}

using ClashWinUI.ViewModels;
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is SettingsViewModel viewModel)
            {
                DataContext = viewModel;
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
    }
}

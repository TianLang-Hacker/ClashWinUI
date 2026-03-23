using ClashWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class ProfilesPage : Page, IShellFreezablePage
    {
        private ProfilesViewModel? _viewModel;

        public ProfilesPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            ProfilesViewModel viewModel = ResolveViewModel();
            if (!ReferenceEquals(_viewModel, viewModel))
            {
                ReleaseViewModel();
                _viewModel = viewModel;
                DataContext = viewModel;
            }

            await viewModel.InitializeAsync();

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

            _viewModel.Dispose();
            _viewModel = null;
            DataContext = null;
        }

        private static ProfilesViewModel ResolveViewModel()
        {
            return ((App)Application.Current).GetRequiredService<ProfilesViewModel>();
        }

        private async void ImportLocalFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null)
            {
                return;
            }

            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".yaml");
            picker.FileTypeFilter.Add(".yml");
            picker.FileTypeFilter.Add(".json");
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

            await _viewModel.ImportLocalFileAsync(file.Path);
        }

        public void PrepareForShellFreeze()
        {
            ReleaseViewModel();
        }
    }
}

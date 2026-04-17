using ClashWinUI.Helpers;
using ClashWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClashWinUI.Views.Pages
{
    public sealed partial class LogsPage : Page, IShellFreezablePage
    {
        private LogsViewModel? _viewModel;

        public LogsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            LogsViewModel viewModel = ResolveViewModel();
            AttachViewModel(viewModel);
            RebindLogsList();
            await viewModel.InitializeAsync();

            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            DetachViewModel();
            base.OnNavigatedFrom(e);
        }

        private void CopyLogsButton_Click(object sender, RoutedEventArgs e)
        {
            string logsText = _viewModel?.LogsText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(logsText))
            {
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(logsText);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
        }

        private async void SaveLogsButton_Click(object sender, RoutedEventArgs e)
        {
            string logsText = _viewModel?.LogsText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(logsText))
            {
                return;
            }

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"clashwinui-logs-{DateTime.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add("Log", new List<string> { ".log" });

            if (Application.Current is App app && app.ActiveWindow is not null)
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(app.ActiveWindow);
                InitializeWithWindow.Initialize(picker, hwnd);
            }

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await FileIO.WriteTextAsync(file, logsText);
        }

        private void AttachViewModel(LogsViewModel viewModel)
        {
            if (ReferenceEquals(_viewModel, viewModel))
            {
                return;
            }

            DetachViewModel();

            _viewModel = viewModel;
            DataContext = viewModel;
            _viewModel.FilteredLogEntries.CollectionChanged += OnFilteredLogEntriesCollectionChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ScrollToBottomIfNeeded();
        }

        private void DetachViewModel()
        {
            LogsListView.ItemsSource = null;

            if (_viewModel is null)
            {
                DataContext = null;
                return;
            }

            LogsViewModel viewModel = _viewModel;
            _viewModel.FilteredLogEntries.CollectionChanged -= OnFilteredLogEntriesCollectionChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
            DataContext = null;
            viewModel.Dispose();
        }

        private static LogsViewModel ResolveViewModel()
        {
            return ((App)Application.Current).GetRequiredService<LogsViewModel>();
        }

        private void RebindLogsList()
        {
            if (LogsListView.ItemsSource is not null)
            {
                return;
            }

            LogsListView.SetBinding(ItemsControl.ItemsSourceProperty, new Binding
            {
                Path = new PropertyPath(nameof(LogsViewModel.FilteredLogEntries)),
            });
        }

        private void OnFilteredLogEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            {
                ScrollToBottomIfNeeded();
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LogsViewModel.IsAutoScrollEnabled) && _viewModel?.IsAutoScrollEnabled == true)
            {
                ScrollToBottomIfNeeded();
            }
        }

        private void ScrollToBottomIfNeeded()
        {
            if (_viewModel is null || !_viewModel.IsAutoScrollEnabled || _viewModel.FilteredLogEntries.Count == 0)
            {
                return;
            }

            object lastItem = _viewModel.FilteredLogEntries[_viewModel.FilteredLogEntries.Count - 1];
            _ = DispatcherQueue.TryEnqueue(() => LogsListView.ScrollIntoView(lastItem));
        }

        public void PrepareForShellFreeze()
        {
            DetachViewModel();
        }
    }
}

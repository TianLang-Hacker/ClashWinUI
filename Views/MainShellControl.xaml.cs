using ClashWinUI.Services.Interfaces;
using ClashWinUI.ViewModels;
using ClashWinUI.Views.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;

namespace ClashWinUI.Views
{
    public sealed partial class MainShellControl : UserControl, IDisposable
    {
        private readonly MainViewModel _viewModel;
        private readonly INavigationService _navigationService;
        private bool _isSynchronizingSelection;
        private bool _isDisposed;

        public MainShellControl(MainViewModel viewModel, INavigationService navigationService)
        {
            _viewModel = viewModel;
            _navigationService = navigationService;

            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        public Frame NavigationFrame => contentFrame;

        public void InitializeNavigation(bool resetNavigation)
        {
            _navigationService.Initialize(contentFrame);

            if (resetNavigation)
            {
                _viewModel.Initialize();
            }
            else
            {
                _navigationService.Navigate(_viewModel.SelectedRoute);
                SyncNavigationSelection();
            }
        }

        public void PrepareForFreeze()
        {
            if (contentFrame.Content is IShellFreezablePage freezablePage)
            {
                freezablePage.PrepareForShellFreeze();
            }

            contentFrame.Content = null;
            contentFrame.BackStack.Clear();
            contentFrame.ForwardStack.Clear();
            RootNavigationView.SelectedItem = null;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            DataContext = null;
            contentFrame.Content = null;
        }

        private void SettingsNavItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(SettingsAnimatedIcon, "PointerOver");
        }

        private void SettingsNavItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(SettingsAnimatedIcon, "Normal");
        }

        private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (_isSynchronizingSelection)
            {
                return;
            }

            if (args.SelectedItemContainer?.Tag is string routeKey)
            {
                _viewModel.NavigateCommand.Execute(routeKey);
            }
        }

        private void NavigationView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            if (_viewModel.GoBackCommand.CanExecute(null))
            {
                _viewModel.GoBackCommand.Execute(null);
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedRoute))
            {
                SyncNavigationSelection();
            }
        }

        private void SyncNavigationSelection()
        {
            _isSynchronizingSelection = true;
            RootNavigationView.SelectedItem = _viewModel.SelectedRoute switch
            {
                MainViewModel.ProfilesRouteKey => ProfilesNavItem,
                MainViewModel.ProxiesRouteKey => ProxiesNavItem,
                MainViewModel.ConnectionsRouteKey => ConnectionsNavItem,
                MainViewModel.LogsRouteKey => LogsNavItem,
                MainViewModel.RulesRouteKey => RulesNavItem,
                MainViewModel.SettingsRouteKey => SettingsNavItem,
                _ => HomeNavItem,
            };
            _isSynchronizingSelection = false;
        }
    }
}

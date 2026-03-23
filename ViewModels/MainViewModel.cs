using ClashWinUI.Helpers;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace ClashWinUI.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        public const string HomeRouteKey = "home";
        public const string ProfilesRouteKey = "profiles";
        public const string ProxiesRouteKey = "proxies";
        public const string ConnectionsRouteKey = "connections";
        public const string LogsRouteKey = "logs";
        public const string RulesRouteKey = "rules";
        public const string SettingsRouteKey = "settings";

        private readonly INavigationService _navigationService;
        private readonly LocalizedStrings _localizedStrings;
        private readonly Stack<string> _navigationHistory = new();

        [ObservableProperty]
        public partial string Title { get; set; }

        [ObservableProperty]
        public partial string SelectedRoute { get; set; }

        [ObservableProperty]
        public partial string HeaderText { get; set; }

        [ObservableProperty]
        public partial bool CanGoBack { get; set; }

        public MainViewModel(INavigationService navigationService, LocalizedStrings localizedStrings)
        {
            _navigationService = navigationService;
            _localizedStrings = localizedStrings;
            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;

            Title = _localizedStrings["AppTitle"];
            SelectedRoute = HomeRouteKey;
            HeaderText = _localizedStrings["NavOverview"];
            CanGoBack = false;
        }

        public void Initialize()
        {
            _navigationHistory.Clear();
            NavigateCore(HomeRouteKey, false);
        }

        [RelayCommand]
        private void Navigate(string? routeKey)
        {
            string targetRoute = NormalizeRoute(routeKey);
            if (string.Equals(targetRoute, SelectedRoute, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            NavigateCore(targetRoute, true);
        }

        [RelayCommand(CanExecute = nameof(CanGoBack))]
        private void GoBack()
        {
            if (_navigationHistory.Count == 0)
            {
                return;
            }

            SelectedRoute = _navigationHistory.Pop();
            _navigationService.Navigate(SelectedRoute);
            RefreshLocalizedText();
            CanGoBack = _navigationHistory.Count > 0;
        }

        partial void OnCanGoBackChanged(bool value)
        {
            GoBackCommand.NotifyCanExecuteChanged();
        }

        private void NavigateCore(string routeKey, bool addToHistory)
        {
            if (addToHistory && !string.IsNullOrWhiteSpace(SelectedRoute))
            {
                _navigationHistory.Push(SelectedRoute);
            }

            SelectedRoute = routeKey;
            _navigationService.Navigate(routeKey);
            RefreshLocalizedText();
            CanGoBack = _navigationHistory.Count > 0;
        }

        private void RefreshLocalizedText()
        {
            Title = _localizedStrings["AppTitle"];
            HeaderText = _localizedStrings[GetHeaderResourceKey(SelectedRoute)];
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocalizedStrings.CurrentLanguage) || e.PropertyName == "Item[]")
            {
                RefreshLocalizedText();
            }
        }

        private static string NormalizeRoute(string? routeKey)
        {
            return routeKey?.ToLowerInvariant() switch
            {
                ProfilesRouteKey => ProfilesRouteKey,
                ProxiesRouteKey => ProxiesRouteKey,
                ConnectionsRouteKey => ConnectionsRouteKey,
                LogsRouteKey => LogsRouteKey,
                RulesRouteKey => RulesRouteKey,
                SettingsRouteKey => SettingsRouteKey,
                _ => HomeRouteKey,
            };
        }

        private static string GetHeaderResourceKey(string routeKey)
        {
            return routeKey switch
            {
                ProfilesRouteKey => "NavProfiles",
                ProxiesRouteKey => "NavProxies",
                ConnectionsRouteKey => "NavConnections",
                LogsRouteKey => "NavLogs",
                RulesRouteKey => "NavRules",
                SettingsRouteKey => "NavSettings",
                _ => "NavOverview",
            };
        }
    }
}

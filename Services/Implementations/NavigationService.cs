using ClashWinUI.Services.Interfaces;
using ClashWinUI.ViewModels;
using ClashWinUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;

namespace ClashWinUI.Services.Implementations
{
    public class NavigationService : INavigationService
    {
        private readonly IServiceProvider _serviceProvider;
        private Frame? _frame;

        private readonly Dictionary<string, RouteDefinition> _routes = new(StringComparer.OrdinalIgnoreCase)
        {
            [MainViewModel.HomeRouteKey] = new(typeof(HomePage), typeof(HomeViewModel)),
            [MainViewModel.ProfilesRouteKey] = new(typeof(ProfilesPage), typeof(ProfilesViewModel)),
            [MainViewModel.ProxiesRouteKey] = new(typeof(ProxiesPage), typeof(ProxiesViewModel)),
            [MainViewModel.ConnectionsRouteKey] = new(typeof(ConnectionsPage), typeof(ConnectionsViewModel)),
            [MainViewModel.LogsRouteKey] = new(typeof(LogsPage), typeof(LogsViewModel)),
            [MainViewModel.RulesRouteKey] = new(typeof(RulesPage), typeof(RulesViewModel)),
            [MainViewModel.SettingsRouteKey] = new(typeof(SettingsPage), typeof(SettingsViewModel)),
        };

        public NavigationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void Initialize(Frame frame)
        {
            _frame = frame;
        }

        public void Navigate(string routeKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);

            if (_frame is null)
            {
                throw new InvalidOperationException("Navigation frame has not been initialized.");
            }

            if (!_routes.TryGetValue(routeKey, out RouteDefinition? route))
            {
                throw new ArgumentException($"Unknown route key: {routeKey}", nameof(routeKey));
            }

            object viewModel = _serviceProvider.GetRequiredService(route.ViewModelType);
            _frame.Navigate(route.PageType, viewModel);
        }

        public void GoBack()
        {
            if (CanGoBack)
            {
                _frame!.GoBack();
            }
        }

        public bool CanGoBack => _frame?.CanGoBack ?? false;

        private sealed record RouteDefinition(Type PageType, Type ViewModelType);
    }
}

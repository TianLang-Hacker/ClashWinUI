using ClashWinUI.Services.Interfaces;
using ClashWinUI.ViewModels;
using ClashWinUI.Views.Pages;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;

namespace ClashWinUI.Services.Implementations
{
    public class NavigationService : INavigationService
    {
        private Frame? _frame;

        private readonly Dictionary<string, Type> _routes = new(StringComparer.OrdinalIgnoreCase)
        {
            [MainViewModel.HomeRouteKey] = typeof(HomePage),
            [MainViewModel.ProfilesRouteKey] = typeof(ProfilesPage),
            [MainViewModel.ProxiesRouteKey] = typeof(ProxiesPage),
            [MainViewModel.ConnectionsRouteKey] = typeof(ConnectionsPage),
            [MainViewModel.LogsRouteKey] = typeof(LogsPage),
            [MainViewModel.RulesRouteKey] = typeof(RulesPage),
            [MainViewModel.SettingsRouteKey] = typeof(SettingsPage),
        };

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

            if (!_routes.TryGetValue(routeKey, out Type? pageType))
            {
                throw new ArgumentException($"Unknown route key: {routeKey}", nameof(routeKey));
            }

            _frame.Navigate(pageType);
            _frame.BackStack.Clear();
            _frame.ForwardStack.Clear();
        }
    }
}

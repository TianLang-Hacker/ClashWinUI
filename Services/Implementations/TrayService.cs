using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using ClashWinUI.ViewModels;
using ClashWinUI.Views;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Implementations
{
    public class TrayService : ITrayService
    {
        private const string TrayIconRelativePath = "Assets\\ClashWinUI.ico";
        private const string TrayToolTipText = "Clash WinUI";
        private readonly IAppLogService _logService;
        private readonly ITrayMenuActionService _menuActionService;
        private readonly IThemeService _themeService;
        private readonly LocalizedStrings _localizedStrings;
        private readonly DispatcherQueue? _dispatcherQueue;

        private TaskbarIcon? _taskbarIcon;
        private MenuFlyout? _contextMenu;
        private TrayMenuHostWindow? _menuHostWindow;
        private Func<string, Task>? _showMainWindowAsyncAction;
        private Func<Task>? _restartApplicationAsyncAction;
        private Func<Task>? _exitApplicationAsyncAction;
        private bool _isExitInProgress;
        private bool _isMenuRefreshRunning;

        public TrayService(
            IAppLogService logService,
            ITrayMenuActionService menuActionService,
            IThemeService themeService,
            LocalizedStrings localizedStrings)
        {
            _logService = logService;
            _menuActionService = menuActionService;
            _themeService = themeService;
            _localizedStrings = localizedStrings;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _menuActionService.SnapshotChanged += OnSnapshotChanged;
            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;
        }

        public bool IsInitialized => _taskbarIcon is not null;

        public void Initialize(
            Func<string, Task> showMainWindowAsyncAction,
            Func<Task> restartApplicationAsyncAction,
            Func<Task> exitApplicationAsyncAction)
        {
            ArgumentNullException.ThrowIfNull(showMainWindowAsyncAction);
            ArgumentNullException.ThrowIfNull(restartApplicationAsyncAction);
            ArgumentNullException.ThrowIfNull(exitApplicationAsyncAction);

            if (_taskbarIcon is not null)
            {
                return;
            }

            _showMainWindowAsyncAction = showMainWindowAsyncAction;
            _restartApplicationAsyncAction = restartApplicationAsyncAction;
            _exitApplicationAsyncAction = exitApplicationAsyncAction;

            _menuHostWindow = new TrayMenuHostWindow(_themeService);
            if (TryCreateTrayIcon(out TaskbarIcon? trayIcon))
            {
                _taskbarIcon = trayIcon;
                _logService.Add("Tray icon initialized.");
                _ = RefreshMenuSnapshotAsync();
                return;
            }

            _showMainWindowAsyncAction = null;
            _restartApplicationAsyncAction = null;
            _exitApplicationAsyncAction = null;
            _contextMenu = null;
            _menuHostWindow.CloseHost();
            _menuHostWindow = null;
            _logService.Add("Tray unavailable, app continues without tray", LogLevel.Warning);
        }

        public void Show()
        {
            _taskbarIcon?.ForceCreate();
        }

        public void Shutdown()
        {
            if (_taskbarIcon is null)
            {
                return;
            }

            _taskbarIcon.Dispose();
            _taskbarIcon = null;
            _contextMenu = null;
            _menuHostWindow?.CloseHost();
            _menuHostWindow = null;
            _showMainWindowAsyncAction = null;
            _restartApplicationAsyncAction = null;
            _exitApplicationAsyncAction = null;
            _isExitInProgress = false;
            _isMenuRefreshRunning = false;
            _logService.Add("Tray icon disposed.");
        }

        public void Dispose()
        {
            Shutdown();
            _menuActionService.SnapshotChanged -= OnSnapshotChanged;
            _localizedStrings.PropertyChanged -= OnLocalizedStringsPropertyChanged;
            GC.SuppressFinalize(this);
        }

        private MenuFlyout BuildContextMenu(TrayMenuSnapshot snapshot)
        {
            bool hasActiveProfile = !string.IsNullOrWhiteSpace(snapshot.ActiveProfileId);
            bool isLoaded = snapshot.IsLoaded;
            var menu = new MenuFlyout
            {
                MenuFlyoutPresenterStyle = CreateMenuFlyoutPresenterStyle(),
            };

            menu.Items.Add(CreateRouteItem(T("TrayMenuHome"), MainViewModel.HomeRouteKey, Symbol.Home));
            menu.Items.Add(BuildRulesMenu(snapshot, hasActiveProfile));
            menu.Items.Add(BuildProfilesMenu(snapshot, isLoaded));
            menu.Items.Add(BuildProxiesMenu(snapshot, isLoaded));
            menu.Items.Add(BuildTunMenuItem(snapshot, hasActiveProfile));
            menu.Items.Add(CreateActionItem(T("TrayMenuOpenProfilesDirectory"), Symbol.Folder, () =>
            {
                _menuActionService.OpenProfilesDirectory();
                return Task.FromResult(true);
            }));
            menu.Items.Add(BuildMoreMenu(hasActiveProfile));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(CreateActionItem(T("TrayMenuExit"), Symbol.Cancel, ExecuteExitCommandAsync));

            return menu;
        }

        private MenuFlyoutSubItem BuildRulesMenu(TrayMenuSnapshot snapshot, bool hasActiveProfile)
        {
            return CreateSubItem(
                T("TrayMenuRules"),
                Symbol.Bullets,
                new MenuFlyoutItemBase[]
                {
                    CreateModeItem(T("TrayMenuModeRule"), "rule", snapshot.Mode, hasActiveProfile),
                    CreateModeItem(T("TrayMenuModeGlobal"), "global", snapshot.Mode, hasActiveProfile),
                    CreateModeItem(T("TrayMenuModeDirect"), "direct", snapshot.Mode, hasActiveProfile),
                },
                hasActiveProfile);
        }

        private MenuFlyoutSubItem BuildProfilesMenu(TrayMenuSnapshot snapshot, bool isLoaded)
        {
            return CreateSubItem(
                T("TrayMenuProfiles"),
                Symbol.Library,
                BuildProfilesMenuItems(snapshot, isLoaded));
        }

        private MenuFlyoutSubItem BuildProxiesMenu(TrayMenuSnapshot snapshot, bool isLoaded)
        {
            return CreateSubItem(
                T("TrayMenuProxies"),
                Symbol.Globe,
                BuildProxyGroupMenuItems(snapshot, isLoaded));
        }

        private IReadOnlyList<MenuFlyoutItemBase> BuildProfilesMenuItems(TrayMenuSnapshot snapshot, bool isLoaded)
        {
            var items = new List<MenuFlyoutItemBase>();
            if (!isLoaded)
            {
                items.Add(CreateDisabledItem(T("TrayMenuLoading")));
                return items;
            }

            if (snapshot.Profiles.Count == 0)
            {
                items.Add(CreateDisabledItem(T("TrayMenuNoProfiles")));
                return items;
            }

            foreach (TrayProfileMenuItem profile in snapshot.Profiles)
            {
                items.Add(CreateToggleActionItem(
                    profile.DisplayName,
                    Symbol.Library,
                    profile.IsActive,
                    () => _menuActionService.ActivateProfileAsync(profile.Id)));
            }

            return items;
        }

        private IReadOnlyList<MenuFlyoutItemBase> BuildProxyGroupMenuItems(TrayMenuSnapshot snapshot, bool isLoaded)
        {
            var items = new List<MenuFlyoutItemBase>();
            if (!isLoaded || (snapshot.ProxyGroupsLoading && snapshot.ProxyGroups.Count == 0))
            {
                items.Add(CreateDisabledItem(T("TrayMenuLoading")));
                return items;
            }

            if (snapshot.ProxyGroups.Count == 0)
            {
                items.Add(CreateDisabledItem(T("TrayMenuNoProxies")));
                return items;
            }

            foreach (TrayProxyGroupMenuItem group in snapshot.ProxyGroups)
            {
                items.Add(CreateSubItem(
                    FormatProxyGroupText(group),
                    Symbol.Globe,
                    BuildProxyNodeMenuItems(group),
                    group.Nodes.Count > 0));
            }

            return items;
        }

        private IReadOnlyList<MenuFlyoutItemBase> BuildProxyNodeMenuItems(TrayProxyGroupMenuItem group)
        {
            var items = new List<MenuFlyoutItemBase>();
            foreach (TrayProxyNodeMenuItem node in group.Nodes)
            {
                string controllerGroupName = string.IsNullOrWhiteSpace(group.ControllerName)
                    ? group.Name
                    : group.ControllerName;
                string controllerNodeName = string.IsNullOrWhiteSpace(node.ControllerName)
                    ? node.Name
                    : node.ControllerName;
                items.Add(CreateToggleActionItem(
                    node.Name,
                    Symbol.Globe,
                    node.IsCurrent,
                    () => _menuActionService.SelectProxyAsync(controllerGroupName, controllerNodeName)));
            }

            if (group.HasMoreNodes)
            {
                items.Add(new MenuFlyoutSeparator());
                items.Add(CreateRouteItem(
                    T("TrayMenuOpenProxiesForMore"),
                    MainViewModel.ProxiesRouteKey,
                    Symbol.More));
            }

            return items;
        }

        private ToggleMenuFlyoutItem BuildTunMenuItem(TrayMenuSnapshot snapshot, bool hasActiveProfile)
        {
            bool targetState = !snapshot.TunEnabled;
            return CreateToggleActionItem(
                T("TrayMenuTunMode"),
                Symbol.Switch,
                snapshot.TunEnabled,
                () => _menuActionService.SetTunEnabledAsync(targetState),
                hasActiveProfile);
        }

        private MenuFlyoutSubItem BuildMoreMenu(bool hasActiveProfile)
        {
            return CreateSubItem(
                T("TrayMenuMore"),
                Symbol.More,
                new MenuFlyoutItemBase[]
                {
                    CreateActionItem(
                        T("TrayMenuRestartMihomo"),
                        Symbol.Refresh,
                        () => _menuActionService.RestartMihomoCoreAsync(),
                        hasActiveProfile),
                    CreateActionItem(
                        T("TrayMenuRestartApp"),
                        Symbol.Sync,
                        ExecuteRestartApplicationCommandAsync),
                });
        }

        private MenuFlyoutItem CreateRouteItem(string text, string routeKey, Symbol symbol)
        {
            return CreateActionItem(text, symbol, () => ExecuteShowWindowCommandAsync(routeKey));
        }

        private MenuFlyoutItem CreateActionItem(string text, Symbol symbol, Func<Task<bool>> action, bool isEnabled = true)
        {
            return new MenuFlyoutItem
            {
                Text = text,
                Icon = new SymbolIcon(symbol),
                IsEnabled = isEnabled,
                MinWidth = 240,
                Command = new AsyncRelayCommand(async () =>
                {
                    await ExecuteMenuActionAsync(text, action);
                }),
            };
        }

        private ToggleMenuFlyoutItem CreateModeItem(string text, string mode, string currentMode, bool isEnabled)
        {
            return CreateToggleActionItem(
                text,
                Symbol.Bullets,
                string.Equals(mode, currentMode, StringComparison.OrdinalIgnoreCase),
                () => _menuActionService.ApplyModeAsync(mode),
                isEnabled);
        }

        private ToggleMenuFlyoutItem CreateToggleActionItem(
            string text,
            Symbol symbol,
            bool isChecked,
            Func<Task<bool>> action,
            bool isEnabled = true)
        {
            return new ToggleMenuFlyoutItem
            {
                Text = text,
                Icon = new SymbolIcon(symbol),
                IsChecked = isChecked,
                IsEnabled = isEnabled,
                MinWidth = 240,
                Command = new AsyncRelayCommand(async () =>
                {
                    await ExecuteMenuActionAsync(text, action);
                }),
            };
        }

        private static MenuFlyoutSubItem CreateSubItem(
            string text,
            Symbol symbol,
            IEnumerable<MenuFlyoutItemBase> children,
            bool isEnabled = true)
        {
            var item = new MenuFlyoutSubItem
            {
                Text = text,
                Icon = new SymbolIcon(symbol),
                IsEnabled = isEnabled,
                MinWidth = 240,
            };

            foreach (MenuFlyoutItemBase child in children)
            {
                item.Items.Add(child);
            }

            return item;
        }

        private static MenuFlyoutItem CreateDisabledItem(string text)
        {
            return new MenuFlyoutItem
            {
                Text = text,
                IsEnabled = false,
                MinWidth = 240,
            };
        }

        private Style CreateMenuFlyoutPresenterStyle()
        {
            var style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 260d));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 14d));
            style.Setters.Add(new Setter(Control.BackgroundProperty, CreateMenuBackgroundBrush()));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, CreateMenuBorderBrush()));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
            return style;
        }

        private Brush CreateMenuBackgroundBrush()
        {
            bool light = IsLightTheme();
            return new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(246, 250, 250, 250)
                : Windows.UI.Color.FromArgb(246, 42, 42, 42));
        }

        private SolidColorBrush CreateMenuBorderBrush()
        {
            return new SolidColorBrush(IsLightTheme()
                ? Windows.UI.Color.FromArgb(60, 0, 0, 0)
                : Windows.UI.Color.FromArgb(58, 255, 255, 255));
        }

        private bool IsLightTheme()
        {
            return _themeService.CurrentAppTheme switch
            {
                AppThemeMode.Light => true,
                AppThemeMode.Dark => false,
                _ => Application.Current?.RequestedTheme == ApplicationTheme.Light,
            };
        }

        private TaskbarIcon BuildTaskbarIcon()
        {
            return new TaskbarIcon
            {
                ToolTipText = TrayToolTipText,
                MenuActivation = PopupActivationMode.None,
                PopupActivation = PopupActivationMode.None,
                ContextMenuMode = ContextMenuMode.PopupMenu,
                RightClickCommand = new AsyncRelayCommand(PrepareContextMenuOpenAsync),
            };
        }

        private bool TryCreateTrayIcon(out TaskbarIcon? createdIcon)
        {
            if (TryCreateWithPrimaryIcon(out createdIcon))
            {
                return true;
            }

            _logService.Add("Tray primary icon failed, fallback to generated icon.", LogLevel.Warning);
            return TryCreateWithGeneratedIcon(out createdIcon);
        }

        private bool TryCreateWithPrimaryIcon(out TaskbarIcon? createdIcon)
        {
            createdIcon = null;
            TaskbarIcon? icon = null;
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, TrayIconRelativePath);
                if (!File.Exists(iconPath))
                {
                    _logService.Add($"Tray primary icon not found: {iconPath}", LogLevel.Warning);
                    return false;
                }

                icon = BuildTaskbarIcon();
                icon.Icon = new System.Drawing.Icon(iconPath);
                icon.ForceCreate();
                createdIcon = icon;
                return true;
            }
            catch (Exception ex)
            {
                _logService.Add($"Tray primary icon error: {ex.Message}", LogLevel.Warning);
                icon?.Dispose();
                createdIcon = null;
                return false;
            }
        }

        private bool TryCreateWithGeneratedIcon(out TaskbarIcon? createdIcon)
        {
            createdIcon = null;
            TaskbarIcon? icon = null;
            try
            {
                icon = BuildTaskbarIcon();
                icon.IconSource = new GeneratedIconSource
                {
                    Text = "C",
                    Foreground = new SolidColorBrush(Colors.White),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4)),
                };
                icon.ForceCreate();
                createdIcon = icon;
                return true;
            }
            catch (Exception ex)
            {
                _logService.Add($"Tray generated icon error: {ex.Message}", LogLevel.Warning);
                icon?.Dispose();
                createdIcon = null;
                return false;
            }
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(LocalizedStrings.CurrentLanguage) && e.PropertyName != "Item[]")
            {
                return;
            }

            _ = RunOnUiThreadAsync(() =>
            {
                LogSnapshotCached(_menuActionService.GetCachedSnapshot());
                return Task.CompletedTask;
            });
        }

        private async Task RefreshMenuSnapshotAsync()
        {
            if (_taskbarIcon is null || _isMenuRefreshRunning)
            {
                return;
            }

            _isMenuRefreshRunning = true;
            try
            {
                await _menuActionService.RefreshSnapshotAsync();
            }
            catch (Exception ex)
            {
                _logService.Add($"Tray menu refresh failed: {ex.Message}", LogLevel.Warning);
            }
            finally
            {
                _isMenuRefreshRunning = false;
            }
        }

        private Task PrepareContextMenuOpenAsync()
        {
            return RunOnUiThreadAsync(() =>
            {
                _ = RefreshMenuSnapshotAsync();
                ShowContextMenu();
                return Task.CompletedTask;
            });
        }

        private void OnSnapshotChanged(object? sender, TrayMenuSnapshot snapshot)
        {
            _ = RunOnUiThreadAsync(() =>
            {
                LogSnapshotCached(snapshot);
                return Task.CompletedTask;
            });
        }

        private void LogSnapshotCached(TrayMenuSnapshot snapshot)
        {
            if (_taskbarIcon is null)
            {
                return;
            }

            LogSnapshot("Tray menu snapshot cached", snapshot);
        }

        private void ShowContextMenu()
        {
            if (_menuHostWindow is null || _taskbarIcon is null)
            {
                return;
            }

            MenuFlyout menu = BuildContextMenu(_menuActionService.GetCachedSnapshot());
            _contextMenu = menu;
            TrayIcon? trayIcon = _taskbarIcon.TrayIcon;
            _menuHostWindow.ShowMenu(menu, trayIcon?.WindowHandle ?? IntPtr.Zero, trayIcon?.Id ?? Guid.Empty);
            LogSnapshot("Tray menu shown", _menuActionService.GetCachedSnapshot());
        }

        private async Task<bool> ExecuteMenuActionAsync(string actionName, Func<Task<bool>> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            try
            {
                _logService.Add($"Tray menu action started: {actionName}.");
                bool succeeded = await action();
                if (succeeded)
                {
                    _logService.Add($"Tray menu action succeeded: {actionName}.");
                    await RefreshMenuSnapshotAsync();
                    return true;
                }
                else
                {
                    _logService.Add($"Tray menu action returned false: {actionName}.", LogLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                _logService.Add($"Tray menu action failed ({actionName}): {ex.Message}", LogLevel.Warning);
            }

            return false;
        }

        private async Task<bool> ExecuteShowWindowCommandAsync(string routeKey)
        {
            if (_showMainWindowAsyncAction is null)
            {
                _logService.Add("Tray show-window action is not available.", LogLevel.Warning);
                return false;
            }

            _logService.Add($"Tray menu clicked: Show main window ({routeKey}).");
            await RunOnUiThreadAsync(() => _showMainWindowAsyncAction(routeKey));
            return true;
        }

        private async Task<bool> ExecuteRestartApplicationCommandAsync()
        {
            if (_restartApplicationAsyncAction is null)
            {
                _logService.Add("Tray restart action is not available.", LogLevel.Warning);
                return false;
            }

            _logService.Add("Tray menu clicked: Restart application.");
            await RunOnUiThreadAsync(_restartApplicationAsyncAction);
            return true;
        }

        private async Task<bool> ExecuteExitCommandAsync()
        {
            if (_isExitInProgress)
            {
                return false;
            }

            _isExitInProgress = true;
            try
            {
                _logService.Add("Tray menu clicked: Exit application.");
                if (_exitApplicationAsyncAction is not null)
                {
                    await RunOnUiThreadAsync(_exitApplicationAsyncAction);
                    return true;
                }
                else
                {
                    _logService.Add("Tray exit action is not available.", LogLevel.Warning);
                    return false;
                }
            }
            finally
            {
                _isExitInProgress = false;
            }
        }

        private Task RunOnUiThreadAsync(Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            {
                return action();
            }

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Failed to enqueue tray command to UI thread."));
            }

            return tcs.Task;
        }

        private string T(string key)
        {
            return _localizedStrings[key];
        }

        private void LogSnapshot(string prefix, TrayMenuSnapshot snapshot)
        {
            string proxyState = snapshot.ProxyGroupsLoading
                ? "loading"
                : snapshot.ProxyGroupsUnavailable
                    ? "unavailable"
                    : "ready";
            _logService.Add(
                $"{prefix}: profiles={snapshot.Profiles.Count}, active={snapshot.ActiveProfileId}, mode={snapshot.Mode}, tun={snapshot.TunEnabled}, proxyGroups={snapshot.ProxyGroups.Count}, proxyState={proxyState}");
        }

        private static string FormatProxyGroupText(TrayProxyGroupMenuItem group)
        {
            return string.IsNullOrWhiteSpace(group.CurrentProxyName)
                ? group.Name
                : $"{group.Name} ({group.CurrentProxyName})";
        }
    }
}

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
using Windows.UI;

namespace ClashWinUI.Services.Implementations
{
    public class TrayService : ITrayService
    {
        private const string TrayIconRelativePath = "Assets\\ClashWinUI.ico";
        private const string TrayToolTipText = "Clash WinUI";
        private const string ModeRadioGroupName = "ClashWinUI.Tray.Mode";
        private const string ProfileRadioGroupName = "ClashWinUI.Tray.Profile";
        private const double MenuMinWidth = 280d;
        private const double MenuItemMinWidth = 256d;
        private const double MenuItemMinHeight = 36d;

        private static readonly Thickness MenuItemPadding = new(12, 8, 12, 8);

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
                ShouldConstrainToRootBounds = false,
                AreOpenCloseAnimationsEnabled = true,
            };

            menu.Items.Add(CreateRouteItem(T("TrayMenuHome"), MainViewModel.HomeRouteKey, "\uE80F"));
            menu.Items.Add(BuildRulesMenu(snapshot, hasActiveProfile));
            menu.Items.Add(BuildProfilesMenu(snapshot, isLoaded));
            menu.Items.Add(BuildProxiesMenu(snapshot, isLoaded));
            menu.Items.Add(BuildTunMenuItem(snapshot, hasActiveProfile));
            menu.Items.Add(CreateActionItem(T("TrayMenuOpenProfilesDirectory"), "\uE8B7", () =>
            {
                _menuActionService.OpenProfilesDirectory();
                return Task.FromResult(true);
            }));
            menu.Items.Add(BuildMoreMenu(hasActiveProfile));
            menu.Items.Add(CreateSeparator());
            menu.Items.Add(CreateActionItem(T("TrayMenuExit"), "\uE711", ExecuteExitCommandAsync));

            return menu;
        }

        private MenuFlyoutSubItem BuildRulesMenu(TrayMenuSnapshot snapshot, bool hasActiveProfile)
        {
            return CreateSubItem(
                T("TrayMenuRules"),
                "\uE8FD",
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
                "\uE8F1",
                BuildProfilesMenuItems(snapshot, isLoaded));
        }

        private MenuFlyoutSubItem BuildProxiesMenu(TrayMenuSnapshot snapshot, bool isLoaded)
        {
            return CreateSubItem(
                T("TrayMenuProxies"),
                "\uE774",
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
                items.Add(CreateRadioActionItem(
                    profile.DisplayName,
                    "\uE8F1",
                    ProfileRadioGroupName,
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
                    "\uE774",
                    BuildProxyNodeMenuItems(group),
                    group.Nodes.Count > 0));
            }

            return items;
        }

        private IReadOnlyList<MenuFlyoutItemBase> BuildProxyNodeMenuItems(TrayProxyGroupMenuItem group)
        {
            var items = new List<MenuFlyoutItemBase>();
            string radioGroupName = $"ClashWinUI.Tray.Proxy.{group.ControllerName}:{group.Name}";

            foreach (TrayProxyNodeMenuItem node in group.Nodes)
            {
                string controllerGroupName = string.IsNullOrWhiteSpace(group.ControllerName)
                    ? group.Name
                    : group.ControllerName;
                string controllerNodeName = string.IsNullOrWhiteSpace(node.ControllerName)
                    ? node.Name
                    : node.ControllerName;
                items.Add(CreateRadioActionItem(
                    node.Name,
                    "\uE968",
                    radioGroupName,
                    node.IsCurrent,
                    () => _menuActionService.SelectProxyAsync(controllerGroupName, controllerNodeName)));
            }

            if (group.HasMoreNodes)
            {
                items.Add(CreateSeparator());
                items.Add(CreateRouteItem(
                    T("TrayMenuOpenProxiesForMore"),
                    MainViewModel.ProxiesRouteKey,
                    "\uE712"));
            }

            return items;
        }

        private ToggleMenuFlyoutItem BuildTunMenuItem(TrayMenuSnapshot snapshot, bool hasActiveProfile)
        {
            bool targetState = !snapshot.TunEnabled;
            return CreateToggleActionItem(
                T("TrayMenuTunMode"),
                "\uE8AB",
                snapshot.TunEnabled,
                () => _menuActionService.SetTunEnabledAsync(targetState),
                hasActiveProfile);
        }

        private MenuFlyoutSubItem BuildMoreMenu(bool hasActiveProfile)
        {
            return CreateSubItem(
                T("TrayMenuMore"),
                "\uE712",
                new MenuFlyoutItemBase[]
                {
                    CreateActionItem(
                        T("TrayMenuRestartMihomo"),
                        "\uE72C",
                        async () => await Task.Run(async () => await _menuActionService.RestartMihomoCoreAsync()),
                        hasActiveProfile),
                    CreateActionItem(
                        T("TrayMenuRestartApp"),
                        "\uE777",
                        ExecuteRestartApplicationCommandAsync),
                });
        }

        private MenuFlyoutItem CreateRouteItem(string text, string routeKey, string glyph)
        {
            return CreateActionItem(text, glyph, () => ExecuteShowWindowCommandAsync(routeKey));
        }

        private MenuFlyoutItem CreateActionItem(string text, string glyph, Func<Task<bool>> action, bool isEnabled = true)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Icon = CreateFluentIcon(glyph),
                IsEnabled = isEnabled,
                Command = new AsyncRelayCommand(async () =>
                {
                    await ExecuteMenuActionAsync(text, action);
                }),
            };
            ApplyMenuItemChrome(item);
            return item;
        }

        private RadioMenuFlyoutItem CreateModeItem(string text, string mode, string currentMode, bool isEnabled)
        {
            return CreateRadioActionItem(
                text,
                "\uE8FD",
                ModeRadioGroupName,
                string.Equals(mode, currentMode, StringComparison.OrdinalIgnoreCase),
                () => _menuActionService.ApplyModeAsync(mode),
                isEnabled);
        }

        private RadioMenuFlyoutItem CreateRadioActionItem(
            string text,
            string glyph,
            string groupName,
            bool isChecked,
            Func<Task<bool>> action,
            bool isEnabled = true)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = text,
                Icon = CreateFluentIcon(glyph),
                GroupName = groupName,
                IsChecked = isChecked,
                IsEnabled = isEnabled,
                Command = new AsyncRelayCommand(async () =>
                {
                    await ExecuteMenuActionAsync(text, action);
                }),
            };
            ApplyMenuItemChrome(item);
            return item;
        }

        private ToggleMenuFlyoutItem CreateToggleActionItem(
            string text,
            string glyph,
            bool isChecked,
            Func<Task<bool>> action,
            bool isEnabled = true)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = text,
                Icon = CreateFluentIcon(glyph),
                IsChecked = isChecked,
                IsEnabled = isEnabled,
                Command = new AsyncRelayCommand(async () =>
                {
                    await ExecuteMenuActionAsync(text, action);
                }),
            };
            ApplyMenuItemChrome(item);
            return item;
        }

        private MenuFlyoutSubItem CreateSubItem(
            string text,
            string glyph,
            IEnumerable<MenuFlyoutItemBase> children,
            bool isEnabled = true)
        {
            var item = new MenuFlyoutSubItem
            {
                Text = text,
                Icon = CreateFluentIcon(glyph),
                IsEnabled = isEnabled,
            };
            ApplyMenuItemChrome(item);

            foreach (MenuFlyoutItemBase child in children)
            {
                item.Items.Add(child);
            }

            return item;
        }

        private MenuFlyoutItem CreateDisabledItem(string text)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                IsEnabled = false,
            };
            ApplyMenuItemChrome(item);
            return item;
        }

        private MenuFlyoutSeparator CreateSeparator()
        {
            return new MenuFlyoutSeparator
            {
                Margin = new Thickness(12, 4, 12, 4),
            };
        }

        private static void ApplyMenuItemChrome(Control item)
        {
            item.MinWidth = MenuItemMinWidth;
            item.MinHeight = MenuItemMinHeight;
            item.Padding = MenuItemPadding;
            item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            item.CornerRadius = new CornerRadius(4);
        }

        private static FontIcon CreateFluentIcon(string glyph)
        {
            return new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 16,
            };
        }

        private Style CreateMenuFlyoutPresenterStyle()
        {
            var style = new Style(typeof(MenuFlyoutPresenter));
            if (TryGetDefaultStyle(typeof(MenuFlyoutPresenter), out Style? defaultStyle) && defaultStyle is not null)
            {
                style.BasedOn = defaultStyle;
            }

            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, MenuMinWidth));
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 360d));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 14d));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 6, 4, 6)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, CreateMenuBackgroundBrush()));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, CreateMenuBorderBrush()));
            style.Setters.Add(new Setter(Control.ForegroundProperty, CreateMenuForegroundBrush()));
            return style;
        }

        private Brush CreateMenuBackgroundBrush()
        {
            if (_themeService.CurrentBackdrop == BackdropMode.Mica
                || _themeService.CurrentBackdrop == BackdropMode.MicaAlt)
            {
                return new SolidColorBrush(Colors.Transparent);
            }

            return CreateMenuBackgroundBrushFallback();
        }

        private Brush CreateMenuBackgroundBrushFallback()
        {
            if (TryGetThemeBrush(
                    out Brush? brush,
                    "AcrylicInAppFillColorDefaultBrush",
                    "AcrylicBackgroundFillColorDefaultBrush",
                    "SolidBackgroundFillColorBaseBrush",
                    "MenuFlyoutPresenterBackground")
                && brush is not null)
            {
                return brush;
            }

            bool light = IsLightTheme();
            return new SolidColorBrush(light
                ? Color.FromArgb(0xF2, 0xF9, 0xF9, 0xF9)
                : Color.FromArgb(0xF2, 0x2C, 0x2C, 0x2C));
        }

        private Brush CreateMenuBorderBrush()
        {
            if (TryGetThemeBrush(
                    out Brush? brush,
                    "SurfaceStrokeColorFlyoutBrush",
                    "SurfaceStrokeColorDefaultBrush",
                    "MenuFlyoutPresenterBorderBrush")
                && brush is not null)
            {
                return brush;
            }

            return new SolidColorBrush(IsLightTheme()
                ? Color.FromArgb(0x0F, 0x00, 0x00, 0x00)
                : Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
        }

        private Brush CreateMenuForegroundBrush()
        {
            if (TryGetThemeBrush(
                    out Brush? brush,
                    "TextFillColorPrimaryBrush",
                    "MenuFlyoutItemForeground")
                && brush is not null)
            {
                return brush;
            }

            return new SolidColorBrush(IsLightTheme()
                ? Color.FromArgb(0xE4, 0x00, 0x00, 0x00)
                : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        }

        private static bool TryGetDefaultStyle(Type targetType, out Style? style)
        {
            style = null;
            if (Application.Current?.Resources is null)
            {
                return false;
            }

            if (Application.Current.Resources.TryGetValue(targetType, out object? value) && value is Style typedStyle)
            {
                style = typedStyle;
                return true;
            }

            return false;
        }

        private static bool TryGetThemeBrush(out Brush? brush, params string[] resourceKeys)
        {
            brush = null;
            ResourceDictionary? resources = Application.Current?.Resources;
            if (resources is null)
            {
                return false;
            }

            foreach (string key in resourceKeys)
            {
                if (resources.TryGetValue(key, out object? value) && value is Brush themeBrush)
                {
                    brush = themeBrush;
                    return true;
                }
            }

            return false;
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
                    Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4)),
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

        private Task<bool> ExecuteRestartApplicationCommandAsync()
        {
            if (_restartApplicationAsyncAction is null)
            {
                _logService.Add("Tray restart action is not available.", LogLevel.Warning);
                return Task.FromResult(false);
            }

            _logService.Add("Tray menu clicked: Restart application.");
            // Do not await process exit/relaunch on the menu command path; that can freeze the host.
            _ = RunOnUiThreadAsync(_restartApplicationAsyncAction);
            return Task.FromResult(true);
        }

        private Task<bool> ExecuteExitCommandAsync()
        {
            if (_isExitInProgress)
            {
                return Task.FromResult(false);
            }

            _isExitInProgress = true;
            _logService.Add("Tray menu clicked: Exit application.");
            if (_exitApplicationAsyncAction is null)
            {
                _isExitInProgress = false;
                _logService.Add("Tray exit action is not available.", LogLevel.Warning);
                return Task.FromResult(false);
            }

            // Fire-and-forget: awaiting Exit() would never complete cleanly on this command path.
            _ = RunOnUiThreadAsync(async () =>
            {
                try
                {
                    await _exitApplicationAsyncAction();
                }
                finally
                {
                    _isExitInProgress = false;
                }
            });
            return Task.FromResult(true);
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


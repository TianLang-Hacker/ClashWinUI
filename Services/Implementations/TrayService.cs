using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Implementations
{
    public class TrayService : ITrayService
    {
        private const string TrayIconRelativePath = "Assets\\ClashWinUI.ico";
        private const string TrayToolTipText = "Clash WinUI";

        private readonly IAppLogService _logService;
        private readonly DispatcherQueue? _dispatcherQueue;

        private TaskbarIcon? _taskbarIcon;
        private Action? _showMainWindowAction;
        private Func<Task>? _exitApplicationAsyncAction;
        private IRelayCommand? _showWindowCommand;
        private IAsyncRelayCommand? _exitCommand;
        private bool _isExitInProgress;

        public TrayService(IAppLogService logService)
        {
            _logService = logService;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }

        public bool IsInitialized => _taskbarIcon is not null;

        public void Initialize(Action showMainWindowAction, Func<Task> exitApplicationAsyncAction)
        {
            ArgumentNullException.ThrowIfNull(showMainWindowAction);
            ArgumentNullException.ThrowIfNull(exitApplicationAsyncAction);

            if (_taskbarIcon is not null)
            {
                return;
            }

            _showMainWindowAction = showMainWindowAction;
            _exitApplicationAsyncAction = exitApplicationAsyncAction;
            _showWindowCommand = new RelayCommand(ExecuteShowWindowCommand);
            _exitCommand = new AsyncRelayCommand(ExecuteExitCommandAsync);

            MenuFlyout menu = BuildContextMenu();
            if (TryCreateWithPrimaryIcon(menu, out TaskbarIcon? primaryIcon))
            {
                _taskbarIcon = primaryIcon;
                _logService.Add("Tray icon initialized.");
                return;
            }

            _logService.Add("Tray icon primary failed, fallback to generated icon", Models.LogLevel.Warning);
            if (TryCreateWithGeneratedIcon(menu, out TaskbarIcon? generatedIcon))
            {
                _taskbarIcon = generatedIcon;
                _logService.Add("Tray icon initialized.");
                return;
            }

            _showMainWindowAction = null;
            _exitApplicationAsyncAction = null;
            _showWindowCommand = null;
            _exitCommand = null;
            _logService.Add("Tray unavailable, app continues without tray", Models.LogLevel.Warning);
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
            _showMainWindowAction = null;
            _exitApplicationAsyncAction = null;
            _showWindowCommand = null;
            _exitCommand = null;
            _isExitInProgress = false;
            _logService.Add("Tray icon disposed.");
        }

        public void Dispose()
        {
            Shutdown();
            GC.SuppressFinalize(this);
        }

        private MenuFlyout BuildContextMenu()
        {
            var menu = new MenuFlyout();
            var showWindowItem = new MenuFlyoutItem
            {
                Text = "Show main window",
                Command = _showWindowCommand,
            };

            var exitItem = new MenuFlyoutItem
            {
                Text = "Exit",
                Command = _exitCommand,
            };

            menu.Items.Add(showWindowItem);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(exitItem);
            return menu;
        }

        private static TaskbarIcon BuildTaskbarIcon(MenuFlyout menu)
        {
            return new TaskbarIcon
            {
                ToolTipText = TrayToolTipText,
                ContextFlyout = menu,
                ContextMenuMode = ContextMenuMode.PopupMenu,
            };
        }

        private bool TryCreateWithPrimaryIcon(MenuFlyout menu, out TaskbarIcon? createdIcon)
        {
            createdIcon = null;
            TaskbarIcon? icon = null;
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, TrayIconRelativePath);
                if (!File.Exists(iconPath))
                {
                    _logService.Add($"Tray primary icon not found: {iconPath}", Models.LogLevel.Warning);
                    return false;
                }

                icon = BuildTaskbarIcon(menu);
                icon.Icon = new System.Drawing.Icon(iconPath);
                icon.ForceCreate();
                createdIcon = icon;
                return true;
            }
            catch (Exception ex)
            {
                _logService.Add($"Tray primary icon error: {ex.Message}", Models.LogLevel.Warning);
                icon?.Dispose();
                createdIcon = null;
                return false;
            }
        }

        private bool TryCreateWithGeneratedIcon(MenuFlyout menu, out TaskbarIcon? createdIcon)
        {
            createdIcon = null;
            TaskbarIcon? icon = null;
            try
            {
                icon = BuildTaskbarIcon(menu);
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
                _logService.Add($"Tray generated icon error: {ex.Message}", Models.LogLevel.Warning);
                icon?.Dispose();
                createdIcon = null;
                return false;
            }
        }

        private void ExecuteShowWindowCommand()
        {
            if (_showMainWindowAction is null)
            {
                _logService.Add("Tray show-window action is not available.", Models.LogLevel.Warning);
                return;
            }

            _logService.Add("Tray menu clicked: Show main window.");
            if (!TryRunOnUiThread(_showMainWindowAction))
            {
                _logService.Add("Tray show-window enqueue failed.", Models.LogLevel.Warning);
            }
        }

        private async Task ExecuteExitCommandAsync()
        {
            if (_isExitInProgress)
            {
                return;
            }

            _isExitInProgress = true;
            try
            {
                _logService.Add("Tray menu clicked: Exit application.");
                if (_exitApplicationAsyncAction is not null)
                {
                    await RunOnUiThreadAsync(_exitApplicationAsyncAction);
                }
                else
                {
                    _logService.Add("Tray exit action is not available.", Models.LogLevel.Warning);
                }
            }
            finally
            {
                _isExitInProgress = false;
            }
        }

        private bool TryRunOnUiThread(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            {
                action();
                return true;
            }

            return _dispatcherQueue.TryEnqueue(() => action());
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
    }
}

using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using ClashWinUI.ViewModels;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace ClashWinUI.Views
{
    public sealed partial class MainWindow : Window
    {
        private const int GwlWndProc = -4;
        private const uint WmGetMinMaxInfo = 0x0024;

        // Reference target: 982x718 effective size on a 2560x1440 @ 150% setup.
        // Ratio-based limits behave like viewport-relative units.
        private const double MinWidthViewportRatio = 982d / (2560d / 1.5d);
        private const double MinHeightViewportRatio = 718d / (1440d / 1.5d);

        private readonly MainViewModel _viewModel;
        private readonly INavigationService _navigationService;
        private readonly IAppSettingsService _appSettingsService;
        private readonly ITrayService _trayService;
        private readonly WndProcDelegate _windowProcDelegate;

        private bool _isSynchronizingSelection;
        private IntPtr _windowHandle;
        private IntPtr _previousWindowProc;
        private int _minimumTrackWidth;
        private int _minimumTrackHeight;

        private void SettingsNavItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(SettingsAnimatedIcon, "PointerOver");
        }

        private void SettingsNavItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            AnimatedIcon.SetState(SettingsAnimatedIcon, "Normal");
        }

        public MainWindow(
            MainViewModel viewModel,
            INavigationService navigationService,
            IThemeService themeService,
            IAppSettingsService appSettingsService,
            ITrayService trayService)
        {
            _viewModel = viewModel;
            _navigationService = navigationService;
            _appSettingsService = appSettingsService;
            _trayService = trayService;
            _windowProcDelegate = WindowProc;

            InitializeComponent();

            if (Content is FrameworkElement root)
            {
                root.DataContext = _viewModel;
            }

            ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

            InitializeMinimumWindowSize();
            AppWindow.Changed += OnAppWindowChanged;
            AppWindow.Closing += OnAppWindowClosing;
            Closed += OnWindowClosed;

            themeService.Initialize(this);
            _navigationService.Initialize(contentFrame);
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Initialize();

            SyncNavigationSelection();
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

        private void InitializeMinimumWindowSize()
        {
            _windowHandle = WindowNative.GetWindowHandle(this);
            if (_windowHandle == IntPtr.Zero)
            {
                return;
            }

            UpdateMinimumTrackSize();
            EnsureWindowMeetsMinimumSize();

            IntPtr newWindowProc = Marshal.GetFunctionPointerForDelegate(_windowProcDelegate);
            _previousWindowProc = SetWindowLongPtr(_windowHandle, GwlWndProc, newWindowProc);
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            UpdateMinimumTrackSize();
        }

        private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (Application.Current is not App app || app.IsShuttingDown)
            {
                return;
            }

            if (_appSettingsService.CloseBehavior == CloseBehavior.MinimizeToTray)
            {
                args.Cancel = true;
                WindowExtensions.Hide(this);
                _trayService.Show();
                return;
            }

            args.Cancel = true;
            await app.RequestExitAsync();
        }

        private void EnsureWindowMeetsMinimumSize()
        {
            SizeInt32 currentSize = AppWindow.Size;
            int targetWidth = Math.Max(currentSize.Width, _minimumTrackWidth);
            int targetHeight = Math.Max(currentSize.Height, _minimumTrackHeight);

            if (targetWidth == currentSize.Width && targetHeight == currentSize.Height)
            {
                return;
            }

            AppWindow.Resize(new SizeInt32(targetWidth, targetHeight));
        }

        private void UpdateMinimumTrackSize()
        {
            DisplayArea displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            RectInt32 workArea = displayArea.WorkArea;

            _minimumTrackWidth = Math.Max(1, (int)Math.Round(workArea.Width * MinWidthViewportRatio));
            _minimumTrackHeight = Math.Max(1, (int)Math.Round(workArea.Height * MinHeightViewportRatio));
        }

        private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WmGetMinMaxInfo && lParam != IntPtr.Zero)
            {
                UpdateMinimumTrackSize();

                MINMAXINFO minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                minMaxInfo.ptMinTrackSize.X = _minimumTrackWidth;
                minMaxInfo.ptMinTrackSize.Y = _minimumTrackHeight;
                Marshal.StructureToPtr(minMaxInfo, lParam, true);
            }

            return _previousWindowProc != IntPtr.Zero
                ? CallWindowProc(_previousWindowProc, hWnd, message, wParam, lParam)
                : DefWindowProc(hWnd, message, wParam, lParam);
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            AppWindow.Changed -= OnAppWindowChanged;
            AppWindow.Closing -= OnAppWindowClosing;
            Closed -= OnWindowClosed;

            if (_windowHandle != IntPtr.Zero && _previousWindowProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_windowHandle, GwlWndProc, _previousWindowProc);
                _previousWindowProc = IntPtr.Zero;
            }
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW", SetLastError = true)]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}

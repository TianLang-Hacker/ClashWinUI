using ClashWinUI.Common;
using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using ClashWinUI.ViewModels;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
        private readonly IThemeService _themeService;
        private readonly IAppSettingsService _appSettingsService;
        private readonly ITrayService _trayService;
        private readonly IHomeOverviewSamplerService _homeOverviewSamplerService;
        private readonly WelcomeWizardViewModel _welcomeWizardViewModel;
        private readonly WndProcDelegate _windowProcDelegate;

        private MainShellControl? _shellControl;
        private bool _isWelcomeVisible;
        private bool _isHiddenToTray;
        private bool _isShellFrozen;
        private bool _isShellTransitioning;
        private bool _isWindowMinimized;
        private bool _pendingFreezeDueToSecondaryWindow;
        private bool _isBackdropSuspended;
        private IntPtr _windowHandle;
        private IntPtr _previousWindowProc;
        private int _minimumTrackWidth;
        private int _minimumTrackHeight;

        public MainWindow(
            MainViewModel viewModel,
            INavigationService navigationService,
            IThemeService themeService,
            IAppSettingsService appSettingsService,
            ITrayService trayService,
            IHomeOverviewSamplerService homeOverviewSamplerService,
            WelcomeWizardViewModel welcomeWizardViewModel)
        {
            _viewModel = viewModel;
            _navigationService = navigationService;
            _themeService = themeService;
            _appSettingsService = appSettingsService;
            _trayService = trayService;
            _homeOverviewSamplerService = homeOverviewSamplerService;
            _welcomeWizardViewModel = welcomeWizardViewModel;
            _windowProcDelegate = WindowProc;

            StartupTrace.Write("MainWindow ctor: before InitializeComponent");
            InitializeComponent();
            StartupTrace.Write("MainWindow ctor: after InitializeComponent");

            ExtendsContentIntoTitleBar = true;
            StartupTrace.Write("MainWindow ctor: ExtendsContentIntoTitleBar set");
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            StartupTrace.Write("MainWindow ctor: title bar height set");

            InitializeMinimumWindowSize();
            StartupTrace.Write("MainWindow ctor: minimum window size initialized");
            AppWindow.Changed += OnAppWindowChanged;
            AppWindow.Closing += OnAppWindowClosing;
            Closed += OnWindowClosed;
            PortSettingsWindow.OpenWindowsChanged += OnPortSettingsWindowsChanged;
            StartupTrace.Write("MainWindow ctor: window event handlers attached");

            _themeService.Initialize(this);
            StartupTrace.Write("MainWindow ctor: theme initialized");
            if (_appSettingsService.WelcomeCompleted)
            {
                CreateShell(resetNavigation: true);
            }
            else
            {
                ShowWelcomeWizard();
            }

            SyncMinimizedState();
            StartupTrace.Write("MainWindow ctor: minimized state synced");
        }

        public event EventHandler? WelcomeCompleted;

        public async Task RestoreFromBackgroundAsync()
        {
            _isHiddenToTray = false;
            WindowExtensions.Show(this);

            if (_isShellFrozen)
            {
                await ThawShellAsync();
            }

            Activate();
        }

        private void CreateShell(bool resetNavigation)
        {
            StartupTrace.Write($"MainWindow.CreateShell: start resetNavigation={resetNavigation}");
            _welcomeWizardViewModel.Completed -= OnWelcomeWizardCompleted;
            _isWelcomeVisible = false;
            _shellControl?.Dispose();
            _shellControl = new MainShellControl(_viewModel, _navigationService);
            ShellHost.Content = _shellControl;
            _shellControl.InitializeNavigation(resetNavigation);
            _isShellFrozen = false;
            StartupTrace.Write("MainWindow.CreateShell: completed");
        }

        private void ShowWelcomeWizard()
        {
            StartupTrace.Write("MainWindow.ShowWelcomeWizard: start");
            _shellControl?.Dispose();
            _shellControl = null;
            _isShellFrozen = false;
            _isWelcomeVisible = true;
            _welcomeWizardViewModel.Completed -= OnWelcomeWizardCompleted;
            _welcomeWizardViewModel.Completed += OnWelcomeWizardCompleted;
            ShellHost.Content = new WelcomeWizardControl(_welcomeWizardViewModel);
            StartupTrace.Write("MainWindow.ShowWelcomeWizard: completed");
        }

        private void OnWelcomeWizardCompleted(object? sender, EventArgs e)
        {
            StartupTrace.Write("MainWindow.OnWelcomeWizardCompleted: start");
            _welcomeWizardViewModel.Completed -= OnWelcomeWizardCompleted;
            CreateShell(resetNavigation: true);
            WelcomeCompleted?.Invoke(this, EventArgs.Empty);
        }

        private async Task FreezeShellAsync()
        {
            if (_isShellFrozen || _isShellTransitioning)
            {
                return;
            }

            if (_isWelcomeVisible)
            {
                return;
            }

            if (PortSettingsWindow.IsAnyOpen)
            {
                _pendingFreezeDueToSecondaryWindow = true;
                return;
            }

            if (_shellControl is null)
            {
                _isShellFrozen = true;
                return;
            }

            _isShellTransitioning = true;
            try
            {
                _pendingFreezeDueToSecondaryWindow = false;
                _shellControl.PrepareForFreeze();
                await Task.Yield();

                MainShellControl shell = _shellControl;
                _shellControl = null;
                ShellHost.Content = null;
                shell.Dispose();

                _homeOverviewSamplerService.FlushState();
                SuspendBackdrop();
                _isShellFrozen = true;
                PageMemoryTrimHelper.RequestTrim("shell freeze");
            }
            finally
            {
                _isShellTransitioning = false;
            }
        }

        private async Task ThawShellAsync()
        {
            if (!_isShellFrozen || _isShellTransitioning)
            {
                return;
            }

            if (_isWelcomeVisible)
            {
                return;
            }

            _isShellTransitioning = true;
            try
            {
                CreateShell(resetNavigation: false);
                ResumeBackdrop();
                await Task.Yield();
            }
            finally
            {
                _isShellTransitioning = false;
            }
        }

        private void SuspendBackdrop()
        {
            if (_isBackdropSuspended)
            {
                return;
            }

            SystemBackdrop = null;
            _isBackdropSuspended = true;
        }

        private void ResumeBackdrop()
        {
            if (!_isBackdropSuspended)
            {
                return;
            }

            _themeService.ApplyBackdrop(_themeService.CurrentBackdrop);
            _isBackdropSuspended = false;
        }

        private async void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            UpdateMinimumTrackSize();
            await SyncMinimizedStateAsync();
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
                _isHiddenToTray = true;
                await FreezeShellAsync();
                WindowExtensions.Hide(this);
                _trayService.Show();
                return;
            }

            args.Cancel = true;
            await app.RequestExitAsync();
        }

        private async void OnPortSettingsWindowsChanged(object? sender, EventArgs e)
        {
            if (!_pendingFreezeDueToSecondaryWindow || PortSettingsWindow.IsAnyOpen)
            {
                return;
            }

            if (_isHiddenToTray || IsWindowMinimized())
            {
                await FreezeShellAsync();
            }
        }

        private async Task SyncMinimizedStateAsync()
        {
            bool isMinimized = IsWindowMinimized();
            if (isMinimized == _isWindowMinimized)
            {
                return;
            }

            _isWindowMinimized = isMinimized;
            if (isMinimized)
            {
                await FreezeShellAsync();
                return;
            }

            if (!_isHiddenToTray)
            {
                await ThawShellAsync();
            }
        }

        private void SyncMinimizedState()
        {
            _isWindowMinimized = IsWindowMinimized();
        }

        private bool IsWindowMinimized()
        {
            return _windowHandle != IntPtr.Zero && IsIconic(_windowHandle);
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
            AppWindow.Changed -= OnAppWindowChanged;
            AppWindow.Closing -= OnAppWindowClosing;
            Closed -= OnWindowClosed;
            PortSettingsWindow.OpenWindowsChanged -= OnPortSettingsWindowsChanged;
            _themeService.UnregisterWindow(this);
            _welcomeWizardViewModel.Completed -= OnWelcomeWizardCompleted;

            _shellControl?.Dispose();
            _shellControl = null;
            ShellHost.Content = null;

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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

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

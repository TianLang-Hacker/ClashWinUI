using ClashWinUI.Common;
using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace ClashWinUI.Views
{
    public sealed partial class PortSettingsWindow : Window
    {
        private const int MinWindowWidth = 800;
        private const int MinWindowHeight = 600;
        private const int GwlWndProc = -4;
        private const uint WmGetMinMaxInfo = 0x0024;

        private readonly SettingsViewModel _settingsViewModel;
        private readonly WndProcDelegate _windowProcDelegate;
        private bool _titleBarConfigured;
        private IntPtr _windowHandle;
        private IntPtr _previousWindowProc;

        public PortSettingsDraft Draft { get; }

        public PortSettingsWindow(SettingsViewModel settingsViewModel, PortSettingsDraft draft)
        {
            _settingsViewModel = settingsViewModel;
            _windowProcDelegate = WindowProc;
            Draft = draft;

            InitializeComponent();
            ConfigureExtendedTitleBar();

            if (Application.Current.Resources["L"] is LocalizedStrings localizedStrings)
            {
                AppWindow.Title = localizedStrings["PortSettingsWindowTitle"];
            }

            InitializeMinimumWindowSize();
            AppWindow.Resize(new SizeInt32(800,600));
            AppWindow.SetIcon("Assets/ClashWinUI.ico");
            Activated += OnActivated;
            Closed += OnWindowClosed;
        }

        public void PositionNear(Window referenceWindow)
        {
            PointInt32 origin = referenceWindow.AppWindow.Position;
            AppWindow.Move(new PointInt32(origin.X + 96, origin.Y + 96));
        }

        private void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (_titleBarConfigured)
            {
                return;
            }

            _titleBarConfigured = true;
            TryConfigureTallTitleBar();
        }

        private void ConfigureExtendedTitleBar()
        {
            try
            {
                ExtendsContentIntoTitleBar = true;
                SetTitleBar(TitleBarDragRegion);
            }
            catch (COMException)
            {
                // Secondary windows can occasionally reject extended title bar setup during initialization.
                // Fall back to the system title bar and keep the window usable.
            }
        }

        private void TryConfigureTallTitleBar()
        {
            try
            {
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            }
            catch (COMException)
            {
                // Some secondary windows are not ready for title bar height customization at first activation.
                // Fall back to the default title bar instead of surfacing an unhandled exception.
            }
        }

        private void InitializeMinimumWindowSize()
        {
            _windowHandle = WindowNative.GetWindowHandle(this);
            if (_windowHandle == IntPtr.Zero)
            {
                return;
            }

            IntPtr newWindowProc = Marshal.GetFunctionPointerForDelegate(_windowProcDelegate);
            _previousWindowProc = SetWindowLongPtr(_windowHandle, GwlWndProc, newWindowProc);
        }

        private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WmGetMinMaxInfo && lParam != IntPtr.Zero)
            {
                MINMAXINFO minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                minMaxInfo.ptMinTrackSize.X = MinWindowWidth;
                minMaxInfo.ptMinTrackSize.Y = MinWindowHeight;
                Marshal.StructureToPtr(minMaxInfo, lParam, true);
            }

            return _previousWindowProc != IntPtr.Zero
                ? CallWindowProc(_previousWindowProc, hWnd, message, wParam, lParam)
                : DefWindowProc(hWnd, message, wParam, lParam);
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            Activated -= OnActivated;
            Closed -= OnWindowClosed;

            if (_windowHandle != IntPtr.Zero && _previousWindowProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_windowHandle, GwlWndProc, _previousWindowProc);
                _previousWindowProc = IntPtr.Zero;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (Draft.IsBusy)
            {
                return;
            }

            Draft.IsBusy = true;
            Draft.StatusMessage = string.Empty;
            CancelButton.IsEnabled = false;
            ConfirmButton.IsEnabled = false;

            try
            {
                (bool success, string message) = await _settingsViewModel.ApplyPortSettingsDraftAsync(Draft);
                if (success)
                {
                    Close();
                    return;
                }

                Draft.StatusMessage = message;
            }
            finally
            {
                Draft.IsBusy = false;
                CancelButton.IsEnabled = true;
                ConfirmButton.IsEnabled = true;
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

using ClashWinUI.Services.Interfaces;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace ClashWinUI.Views
{
    internal sealed class TrayMenuHostWindow : Window
    {
        private const int HostSize = 1;
        private const int ScreenMargin = 8;
        private const int MenuGap = 8;
        private const int GwlExStyle = -20;
        private const int SwHide = 0;
        private const int SwShowNoActivate = 4;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExAppWindow = 0x00040000L;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;

        private static readonly IntPtr HwndTopmost = new(-1);

        private readonly IThemeService _themeService;
        private readonly Grid _anchor;
        private readonly IntPtr _windowHandle;
        private bool _isClosed;

        public TrayMenuHostWindow(IThemeService themeService)
        {
            _themeService = themeService;
            _anchor = new Grid
            {
                Width = HostSize,
                Height = HostSize,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };

            Content = _anchor;
            _windowHandle = WindowNative.GetWindowHandle(this);
            ConfigureWindow();
            ApplyTheme();
        }

        public void ShowMenu(MenuFlyout menu, IntPtr trayWindowHandle, Guid trayIconId)
        {
            if (_isClosed)
            {
                return;
            }

            ApplyTheme();

            RectInt32 anchorRect = ResolveAnchorRect(trayWindowHandle, trayIconId);
            DisplayArea displayArea = DisplayArea.GetFromPoint(GetRectCenter(anchorRect), DisplayAreaFallback.Nearest);
            TaskbarEdge edge = DetectTaskbarEdge(anchorRect, displayArea);
            PointInt32 hostPoint = CalculateHostPoint(anchorRect, displayArea.WorkArea, edge);

            MoveHost(hostPoint);
            menu.Placement = edge switch
            {
                TaskbarEdge.Top => FlyoutPlacementMode.Bottom,
                TaskbarEdge.Left => FlyoutPlacementMode.Right,
                TaskbarEdge.Right => FlyoutPlacementMode.Left,
                _ => FlyoutPlacementMode.Top,
            };
            menu.Closed += OnMenuClosed;
            menu.ShowAt(_anchor);
        }

        public void HideHost()
        {
            if (_windowHandle == IntPtr.Zero || _isClosed)
            {
                return;
            }

            ShowWindow(_windowHandle, SwHide);
        }

        public void CloseHost()
        {
            if (_isClosed)
            {
                return;
            }

            ShowWindow(_windowHandle, SwHide);
            _isClosed = true;
            Close();
        }

        private void ConfigureWindow()
        {
            AppWindow.Title = string.Empty;
            AppWindow.IsShownInSwitchers = false;
            ExtendsContentIntoTitleBar = true;

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            AppWindow.MoveAndResize(new RectInt32(0, 0, HostSize, HostSize));

            if (_windowHandle == IntPtr.Zero)
            {
                return;
            }

            long extendedStyle = GetWindowLongPtr(_windowHandle, GwlExStyle).ToInt64();
            extendedStyle |= WsExToolWindow;
            extendedStyle &= ~WsExAppWindow;
            SetWindowLongPtr(_windowHandle, GwlExStyle, new IntPtr(extendedStyle));
            ShowWindow(_windowHandle, SwHide);
        }

        private void ApplyTheme()
        {
            _anchor.RequestedTheme = _themeService.CurrentAppTheme switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        private void MoveHost(PointInt32 point)
        {
            AppWindow.MoveAndResize(new RectInt32(point.X, point.Y, HostSize, HostSize));
            SetWindowPos(
                _windowHandle,
                HwndTopmost,
                point.X,
                point.Y,
                HostSize,
                HostSize,
                SwpShowWindow | SwpNoActivate);
            ShowWindow(_windowHandle, SwShowNoActivate);
        }

        private void OnMenuClosed(object? sender, object e)
        {
            if (sender is MenuFlyout menu)
            {
                menu.Closed -= OnMenuClosed;
            }

            HideHost();
        }

        private static PointInt32 CalculateHostPoint(RectInt32 anchorRect, RectInt32 workArea, TaskbarEdge edge)
        {
            PointInt32 center = GetRectCenter(anchorRect);
            int anchorRight = anchorRect.X + anchorRect.Width;
            int anchorBottom = anchorRect.Y + anchorRect.Height;

            PointInt32 point = edge switch
            {
                TaskbarEdge.Top => new PointInt32(center.X, anchorBottom + MenuGap),
                TaskbarEdge.Left => new PointInt32(anchorRight + MenuGap, center.Y),
                TaskbarEdge.Right => new PointInt32(anchorRect.X - MenuGap, center.Y),
                _ => new PointInt32(center.X, anchorRect.Y - MenuGap),
            };

            return ClampToWorkArea(point, workArea);
        }

        private RectInt32 ResolveAnchorRect(IntPtr trayWindowHandle, Guid trayIconId)
        {
            if (TryGetTrayIconRect(trayWindowHandle, trayIconId, out RectInt32 iconRect))
            {
                return iconRect;
            }

            PointInt32 cursor = GetCursorPosition();
            return new RectInt32(cursor.X, cursor.Y, HostSize, HostSize);
        }

        private static PointInt32 ClampToWorkArea(PointInt32 point, RectInt32 workArea)
        {
            int maxX = workArea.X + workArea.Width - ScreenMargin;
            int maxY = workArea.Y + workArea.Height - ScreenMargin;
            return new PointInt32(
                Math.Clamp(point.X, workArea.X + ScreenMargin, Math.Max(workArea.X + ScreenMargin, maxX)),
                Math.Clamp(point.Y, workArea.Y + ScreenMargin, Math.Max(workArea.Y + ScreenMargin, maxY)));
        }

        private static TaskbarEdge DetectTaskbarEdge(RectInt32 anchorRect, DisplayArea displayArea)
        {
            RectInt32 outer = displayArea.OuterBounds;
            RectInt32 work = displayArea.WorkArea;
            PointInt32 center = GetRectCenter(anchorRect);
            int outerRight = outer.X + outer.Width;
            int outerBottom = outer.Y + outer.Height;
            int workRight = work.X + work.Width;
            int workBottom = work.Y + work.Height;

            if (work.Y > outer.Y && center.Y < work.Y)
            {
                return TaskbarEdge.Top;
            }

            if (workBottom < outerBottom && center.Y > workBottom)
            {
                return TaskbarEdge.Bottom;
            }

            if (work.X > outer.X && center.X < work.X)
            {
                return TaskbarEdge.Left;
            }

            if (workRight < outerRight && center.X > workRight)
            {
                return TaskbarEdge.Right;
            }

            int distanceTop = Math.Abs(center.Y - outer.Y);
            int distanceBottom = Math.Abs(center.Y - outerBottom);
            int distanceLeft = Math.Abs(center.X - outer.X);
            int distanceRight = Math.Abs(center.X - outerRight);
            int min = Math.Min(Math.Min(distanceTop, distanceBottom), Math.Min(distanceLeft, distanceRight));

            if (min == distanceTop)
            {
                return TaskbarEdge.Top;
            }

            if (min == distanceLeft)
            {
                return TaskbarEdge.Left;
            }

            return min == distanceRight ? TaskbarEdge.Right : TaskbarEdge.Bottom;
        }

        private static bool TryGetTrayIconRect(IntPtr trayWindowHandle, Guid trayIconId, out RectInt32 iconRect)
        {
            iconRect = default;
            if (trayWindowHandle == IntPtr.Zero || trayIconId == Guid.Empty)
            {
                return false;
            }

            var identifier = new NotifyIconIdentifier
            {
                CbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
                HWnd = trayWindowHandle,
                GuidItem = trayIconId,
            };

            int result = Shell_NotifyIconGetRect(ref identifier, out NativeRect nativeRect);
            if (result != 0)
            {
                return false;
            }

            int width = nativeRect.Right - nativeRect.Left;
            int height = nativeRect.Bottom - nativeRect.Top;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            iconRect = new RectInt32(nativeRect.Left, nativeRect.Top, width, height);
            return true;
        }

        private static PointInt32 GetRectCenter(RectInt32 rect)
        {
            return new PointInt32(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        }

        private static PointInt32 GetCursorPosition()
        {
            return GetCursorPos(out NativePoint point)
                ? new PointInt32(point.X, point.Y)
                : new PointInt32(0, 0);
        }

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect iconLocation);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out NativePoint lpPoint);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private enum TaskbarEdge
        {
            Top,
            Bottom,
            Left,
            Right,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NotifyIconIdentifier
        {
            public uint CbSize;
            public IntPtr HWnd;
            public uint UId;
            public Guid GuidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private struct NativePoint
        {
            public int X;
            public int Y;
        }
    }
}

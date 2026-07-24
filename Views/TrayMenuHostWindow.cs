using ClashWinUI.Models;
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
        private const int ScreenMargin = 8;
        private const int AnchorSize = 8;
        private const int HostSize = 8;
        private const int GwlExStyle = -20;
        private const int GwlStyle = -16;
        private const int SwHide = 0;
        private const int SwShowNoActivate = 4;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExAppWindow = 0x00040000L;
        private const long WsExNoActivate = 0x08000000L;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const long WsPopupStyle = 0x80000000L;
        private const int WhMouseLl = 14;
        private const int GaRoot = 2;
        private const uint WmLButtonDown = 0x0201;
        private const uint WmRButtonDown = 0x0204;
        private const uint WmMButtonDown = 0x0207;
        private const uint WmXButtonDown = 0x020B;
        private const uint WmNclButtonDown = 0x00A1;
        private const uint WmNcrButtonDown = 0x00A4;

        private static readonly IntPtr HwndTopmost = new(-1);

        private readonly IThemeService _themeService;
        private readonly Grid _root;
        private readonly Border _anchor;
        private readonly IntPtr _windowHandle;
        private readonly LowLevelMouseProc _mouseProc;
        private bool _isClosed;
        private bool _isOutsideClickHookActive;
        private IntPtr _mouseHookHandle;
        private MenuFlyout? _openMenu;

        public TrayMenuHostWindow(IThemeService themeService)
        {
            _themeService = themeService;
            _mouseProc = OnLowLevelMouseProc;
            _anchor = new Border
            {
                Width = AnchorSize,
                Height = AnchorSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                IsHitTestVisible = false,
            };

            _root = new Grid
            {
                // Tiny XAML host only anchors the MenuFlyout. Outside-click dismiss uses a
                // low-level mouse hook so we never cover the desktop with a WinUI surface.
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Children = { _anchor },
            };

            Content = _root;
            _windowHandle = WindowNative.GetWindowHandle(this);
            ConfigureWindow();
            ApplyTheme();
            _themeService.ThemeChanged += OnThemeChanged;
        }

        public void ShowMenu(MenuFlyout menu, IntPtr trayWindowHandle, Guid trayIconId)
        {
            if (_isClosed)
            {
                return;
            }

            DismissOpenMenu();
            ApplyTheme();

            RectInt32 iconRect = ResolveAnchorRect(trayWindowHandle, trayIconId);
            DisplayArea displayArea = DisplayArea.GetFromPoint(GetRectCenter(iconRect), DisplayAreaFallback.Nearest);
            TaskbarEdge edge = DetectTaskbarEdge(iconRect, displayArea);
            RectInt32 hostBounds = CreateHostBounds(iconRect, displayArea);

            MoveAndShowHost(hostBounds);

            menu.ShouldConstrainToRootBounds = false;
            menu.AreOpenCloseAnimationsEnabled = true;
            menu.Placement = edge switch
            {
                TaskbarEdge.Top => FlyoutPlacementMode.Bottom,
                TaskbarEdge.Left => FlyoutPlacementMode.Right,
                TaskbarEdge.Right => FlyoutPlacementMode.Left,
                _ => FlyoutPlacementMode.Top,
            };

            _openMenu = menu;
            menu.Closed += OnMenuClosed;
            menu.ShowAt(_anchor, new FlyoutShowOptions
            {
                Placement = menu.Placement,
                ShowMode = FlyoutShowMode.Standard,
            });

            // Install after ShowAt so the opening tray right-click does not immediately dismiss.
            InstallOutsideClickHook();
        }

        public void HideHost()
        {
            if (_isClosed)
            {
                return;
            }

            UninstallOutsideClickHook();

            if (_windowHandle == IntPtr.Zero)
            {
                return;
            }

            AppWindow.MoveAndResize(new RectInt32(0, 0, 1, 1));
            ShowWindow(_windowHandle, SwHide);
        }

        public void CloseHost()
        {
            if (_isClosed)
            {
                return;
            }

            DismissOpenMenu();
            UninstallOutsideClickHook();
            _themeService.ThemeChanged -= OnThemeChanged;
            ShowWindow(_windowHandle, SwHide);
            _isClosed = true;
            Close();
        }

        private void ConfigureWindow()
        {
            AppWindow.Title = string.Empty;
            AppWindow.IsShownInSwitchers = false;
            ExtendsContentIntoTitleBar = true;
            SystemBackdrop = null;

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(false, false);
            }

            AppWindow.MoveAndResize(new RectInt32(0, 0, 1, 1));

            if (_windowHandle == IntPtr.Zero)
            {
                return;
            }

            ApplyHostWindowStyles();
            ShowWindow(_windowHandle, SwHide);
        }

        private void ApplyHostWindowStyles()
        {
            long extendedStyle = GetWindowLongPtr(_windowHandle, GwlExStyle).ToInt64();
            extendedStyle |= WsExToolWindow;
            extendedStyle &= ~WsExAppWindow;
            extendedStyle &= ~WsExNoActivate;
            SetWindowLongPtr(_windowHandle, GwlExStyle, new IntPtr(extendedStyle));
        }

        private void ApplyTheme()
        {
            ElementTheme targetTheme = _themeService.CurrentAppTheme switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            if (_root.RequestedTheme != targetTheme)
            {
                _root.RequestedTheme = targetTheme;
            }
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            if (!_isClosed)
            {
                ApplyTheme();
            }
        }

        private void MoveAndShowHost(RectInt32 bounds)
        {
            ApplyHostWindowStyles();

            AppWindow.MoveAndResize(bounds);
            SetWindowPos(
                _windowHandle,
                HwndTopmost,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                SwpShowWindow | SwpNoActivate);
            ShowWindow(_windowHandle, SwShowNoActivate);
        }

        private void InstallOutsideClickHook()
        {
            if (_isOutsideClickHookActive || _mouseHookHandle != IntPtr.Zero)
            {
                return;
            }

            _mouseHookHandle = SetWindowsHookExW(
                WhMouseLl,
                _mouseProc,
                GetModuleHandleW(null),
                0);

            _isOutsideClickHookActive = _mouseHookHandle != IntPtr.Zero;
        }

        private void UninstallOutsideClickHook()
        {
            if (_mouseHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }

            _isOutsideClickHookActive = false;
        }

        private IntPtr OnLowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _openMenu is not null && IsMouseButtonDownMessage((uint)wParam.ToInt32()))
            {
                MsllHookStruct info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                IntPtr windowUnderCursor = WindowFromPoint(info.Pt);
                if (!IsWindowOwnedByMenuHost(windowUnderCursor))
                {
                    // Do not block the click; just dismiss on the UI thread.
                    DispatcherQueue.TryEnqueue(DismissOpenMenu);
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        private static bool IsMouseButtonDownMessage(uint message)
        {
            return message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown
                or WmNclButtonDown or WmNcrButtonDown;
        }

        private bool IsWindowOwnedByMenuHost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _windowHandle == IntPtr.Zero)
            {
                return false;
            }

            IntPtr current = hwnd;
            for (int depth = 0; depth < 16 && current != IntPtr.Zero; depth++)
            {
                if (current == _windowHandle)
                {
                    return true;
                }

                IntPtr root = GetAncestor(current, GaRoot);
                if (root == _windowHandle)
                {
                    return true;
                }

                IntPtr owner = GetWindow(current, 4 /* GW_OWNER */);
                if (owner == _windowHandle)
                {
                    return true;
                }

                if (owner != IntPtr.Zero && owner != current)
                {
                    current = owner;
                    continue;
                }

                if (root != IntPtr.Zero && root != current)
                {
                    current = root;
                    continue;
                }

                break;
            }

            GetWindowThreadProcessId(hwnd, out uint windowProcessId);
            GetWindowThreadProcessId(_windowHandle, out uint hostProcessId);
            if (windowProcessId == 0 || windowProcessId != hostProcessId)
            {
                return false;
            }

            // WinUI MenuFlyout popups are typically same-process tool/popup windows.
            // Do not treat ordinary app windows (e.g. MainWindow) as part of the menu.
            long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            bool isToolWindow = (exStyle & WsExToolWindow) != 0;
            bool isAppWindow = (exStyle & WsExAppWindow) != 0;
            bool isPopup = (style & WsPopupStyle) != 0;
            bool isNoActivate = (exStyle & WsExNoActivate) != 0;

            return (isToolWindow && !isAppWindow) || (isPopup && isNoActivate);
        }

        private static RectInt32 CreateHostBounds(RectInt32 iconRect, DisplayArea displayArea)
        {
            PointInt32 center = GetRectCenter(iconRect);
            int width = HostSize;
            int height = HostSize;
            int x = center.X - (width / 2);
            int y = center.Y - (height / 2);

            RectInt32 work = displayArea.WorkArea;
            RectInt32 outer = displayArea.OuterBounds;
            int minX = outer.X + ScreenMargin;
            int minY = outer.Y + ScreenMargin;
            int maxX = outer.X + outer.Width - width - ScreenMargin;
            int maxY = outer.Y + outer.Height - height - ScreenMargin;
            if (maxX < minX)
            {
                minX = work.X;
                maxX = Math.Max(work.X, work.X + work.Width - width);
            }

            if (maxY < minY)
            {
                minY = work.Y;
                maxY = Math.Max(work.Y, work.Y + work.Height - height);
            }

            x = Math.Clamp(x, minX, Math.Max(minX, maxX));
            y = Math.Clamp(y, minY, Math.Max(minY, maxY));
            return new RectInt32(x, y, width, height);
        }

        private void OnMenuClosed(object? sender, object e)
        {
            if (sender is MenuFlyout menu)
            {
                menu.Closed -= OnMenuClosed;
            }

            if (ReferenceEquals(_openMenu, sender))
            {
                _openMenu = null;
            }

            HideHost();
        }

        private void DismissOpenMenu()
        {
            MenuFlyout? menu = _openMenu;
            if (menu is null)
            {
                HideHost();
                return;
            }

            try
            {
                menu.Hide();
            }
            catch
            {
                _openMenu = null;
                HideHost();
            }
        }

        private RectInt32 ResolveAnchorRect(IntPtr trayWindowHandle, Guid trayIconId)
        {
            if (TryGetTrayIconRect(trayWindowHandle, trayIconId, out RectInt32 iconRect))
            {
                return iconRect;
            }

            PointInt32 cursor = GetCursorPosition();
            return new RectInt32(cursor.X, cursor.Y, AnchorSize, AnchorSize);
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

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, int gaFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

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

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MsllHookStruct
        {
            public NativePoint Pt;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr DwExtraInfo;
        }
    }
}

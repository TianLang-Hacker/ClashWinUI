using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;

namespace ClashWinUI.Services.Implementations
{
    public class ThemeService : IThemeService
    {
        private readonly Dictionary<Window, FrameworkElement> _registeredWindows = new();
        private readonly IAppLogService _logService;
        private readonly IAppSettingsService _appSettingsService;

        private Window? _primaryWindow;
        private bool _isApplyingTheme;

        public AppThemeMode CurrentAppTheme { get; private set; }
        public BackdropMode CurrentBackdrop { get; private set; }
        public event EventHandler? ThemeChanged;

        public ThemeService(IAppLogService logService, IAppSettingsService appSettingsService)
        {
            _logService = logService;
            _appSettingsService = appSettingsService;
            CurrentAppTheme = _appSettingsService.AppThemeMode;
            CurrentBackdrop = _appSettingsService.BackdropMode;
        }

        public void Initialize(Window window)
        {
            _primaryWindow = window;
            RegisterWindow(window);
            ApplyBackdrop(CurrentBackdrop);
        }

        public void RegisterWindow(Window window)
        {
            if (window.Content is not FrameworkElement root)
            {
                return;
            }

            if (_registeredWindows.TryGetValue(window, out FrameworkElement? existingRoot))
            {
                if (!ReferenceEquals(existingRoot, root))
                {
                    existingRoot.ActualThemeChanged -= OnRegisteredWindowActualThemeChanged;
                }
                else
                {
                    ApplyThemeToWindow(window, root);
                    return;
                }
            }

            _registeredWindows[window] = root;
            root.ActualThemeChanged += OnRegisteredWindowActualThemeChanged;
            ApplyThemeToWindow(window, root);
        }

        public void UnregisterWindow(Window window)
        {
            if (_registeredWindows.Remove(window, out FrameworkElement? root))
            {
                root.ActualThemeChanged -= OnRegisteredWindowActualThemeChanged;
            }

            if (ReferenceEquals(_primaryWindow, window))
            {
                _primaryWindow = null;
            }
        }

        public void ApplyAppTheme(AppThemeMode mode)
        {
            if (CurrentAppTheme == mode && AreRegisteredWindowsAlreadyUsing(mode))
            {
                LogThemeOperation($"ApplyAppTheme skip (same-value): {mode}");
                return;
            }

            CurrentAppTheme = mode;
            _isApplyingTheme = true;
            try
            {
                foreach ((Window window, FrameworkElement root) in _registeredWindows)
                {
                    ApplyThemeToWindow(window, root);
                }
            }
            finally
            {
                _isApplyingTheme = false;
            }

            _appSettingsService.AppThemeMode = CurrentAppTheme;
            LogThemeOperation($"ApplyAppTheme applied: {mode}");
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool ApplyBackdrop(BackdropMode mode)
        {
            if (_primaryWindow is null)
            {
                return false;
            }

            if (HasMatchingBackdrop(_primaryWindow, mode))
            {
                CurrentBackdrop = mode;
                LogThemeOperation($"ApplyBackdrop skip (same-value): {mode}");
                return true;
            }

            if (mode == BackdropMode.Acrylic)
            {
                if (!DesktopAcrylicController.IsSupported())
                {
                    return false;
                }

                _primaryWindow.SystemBackdrop = new DesktopAcrylicBackdrop();
                CurrentBackdrop = mode;
                _appSettingsService.BackdropMode = CurrentBackdrop;
                LogThemeOperation($"ApplyBackdrop applied: {mode}");
                return true;
            }

            if (!MicaController.IsSupported())
            {
                return false;
            }

            _primaryWindow.SystemBackdrop = new MicaBackdrop
            {
                Kind = mode == BackdropMode.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base,
            };
            CurrentBackdrop = mode;
            _appSettingsService.BackdropMode = CurrentBackdrop;
            LogThemeOperation($"ApplyBackdrop applied: {mode}");
            return true;
        }

        private void ApplyThemeToWindow(Window window, FrameworkElement root)
        {
            ElementTheme targetTheme = CurrentAppTheme switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            if (root.RequestedTheme != targetTheme)
            {
                root.RequestedTheme = targetTheme;
            }

            ApplyTitleBarColors(window, ResolveEffectiveTheme(root));
        }

        private bool AreRegisteredWindowsAlreadyUsing(AppThemeMode mode)
        {
            ElementTheme targetTheme = mode switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            foreach (FrameworkElement root in _registeredWindows.Values)
            {
                if (root.RequestedTheme != targetTheme)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasMatchingBackdrop(Window window, BackdropMode mode)
        {
            return mode switch
            {
                BackdropMode.Acrylic => window.SystemBackdrop is DesktopAcrylicBackdrop,
                BackdropMode.Mica => window.SystemBackdrop is MicaBackdrop micaBackdrop && micaBackdrop.Kind == MicaKind.Base,
                BackdropMode.MicaAlt => window.SystemBackdrop is MicaBackdrop micaAltBackdrop && micaAltBackdrop.Kind == MicaKind.BaseAlt,
                _ => false,
            };
        }

        private ElementTheme ResolveEffectiveTheme(FrameworkElement root)
        {
            return CurrentAppTheme switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark => ElementTheme.Dark,
                _ => root.ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark,
            };
        }

        private void OnRegisteredWindowActualThemeChanged(FrameworkElement sender, object args)
        {
            if (_isApplyingTheme || CurrentAppTheme != AppThemeMode.System)
            {
                return;
            }

            foreach ((Window window, FrameworkElement root) in _registeredWindows)
            {
                if (!ReferenceEquals(root, sender))
                {
                    continue;
                }

                ApplyTitleBarColors(window, ResolveEffectiveTheme(root));
                ThemeChanged?.Invoke(this, EventArgs.Empty);
                break;
            }
        }

        private void LogThemeOperation(string message)
        {
#if DEBUG
            _logService.Add($"[Theme] {message}", LogLevel.Debug);
#endif
        }

        private static void ApplyTitleBarColors(Window window, ElementTheme effectiveTheme)
        {
            AppWindowTitleBar titleBar = window.AppWindow.TitleBar;

            if (effectiveTheme == ElementTheme.Light)
            {
                titleBar.ButtonForegroundColor = Colors.Black;
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 110, 110, 110);
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonHoverForegroundColor = Colors.Black;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(26, 0, 0, 0);
                titleBar.ButtonPressedForegroundColor = Colors.Black;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(40, 0, 0, 0);
            }
            else
            {
                titleBar.ButtonForegroundColor = Colors.White;
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 170, 170, 170);
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(28, 255, 255, 255);
                titleBar.ButtonPressedForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(44, 255, 255, 255);
            }
        }
    }
}

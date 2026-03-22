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

        private Window? _primaryWindow;
        private bool _isApplyingTheme;

        public AppThemeMode CurrentAppTheme { get; private set; } = AppThemeMode.System;
        public BackdropMode CurrentBackdrop { get; private set; } = BackdropMode.Mica;
        public event EventHandler? ThemeChanged;

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

            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool ApplyBackdrop(BackdropMode mode)
        {
            if (_primaryWindow is null)
            {
                return false;
            }

            if (mode == BackdropMode.Acrylic)
            {
                if (!DesktopAcrylicController.IsSupported())
                {
                    return false;
                }

                _primaryWindow.SystemBackdrop = new DesktopAcrylicBackdrop();
                CurrentBackdrop = mode;
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
            return true;
        }

        private void ApplyThemeToWindow(Window window, FrameworkElement root)
        {
            root.RequestedTheme = CurrentAppTheme switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            ApplyTitleBarColors(window, ResolveEffectiveTheme(root));
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

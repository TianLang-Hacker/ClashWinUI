using ClashWinUI.Services.Interfaces;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ClashWinUI.Services.Implementations
{
    public class ThemeService : IThemeService
    {
        private Window? _window;

        public AppThemeMode CurrentAppTheme { get; private set; } = AppThemeMode.System;
        public BackdropMode CurrentBackdrop { get; private set; } = BackdropMode.Mica;

        public void Initialize(Window window)
        {
            _window = window;
            ApplyAppTheme(CurrentAppTheme);
            ApplyBackdrop(CurrentBackdrop);
        }

        public void ApplyAppTheme(AppThemeMode mode)
        {
            CurrentAppTheme = mode;
            if (_window?.Content is not FrameworkElement root)
            {
                return;
            }

            root.RequestedTheme = mode switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        public bool ApplyBackdrop(BackdropMode mode)
        {
            if (_window is null)
            {
                return false;
            }

            if (mode == BackdropMode.Acrylic)
            {
                if (!DesktopAcrylicController.IsSupported())
                {
                    return false;
                }

                _window.SystemBackdrop = new DesktopAcrylicBackdrop();
                CurrentBackdrop = mode;
                return true;
            }

            if (!MicaController.IsSupported())
            {
                return false;
            }

            _window.SystemBackdrop = new MicaBackdrop
            {
                Kind = mode == BackdropMode.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base,
            };
            CurrentBackdrop = mode;
            return true;
        }
    }
}

using Microsoft.UI.Xaml;
using System;

namespace ClashWinUI.Services.Interfaces
{
    public enum AppThemeMode
    {
        System,
        Light,
        Dark,
    }

    public enum BackdropMode
    {
        Mica,
        MicaAlt,
        Acrylic,
    }

    public interface IThemeService
    {
        AppThemeMode CurrentAppTheme { get; }
        BackdropMode CurrentBackdrop { get; }
        event EventHandler? ThemeChanged;

        void Initialize(Window window);
        void RegisterWindow(Window window);
        void UnregisterWindow(Window window);
        bool ApplyBackdrop(BackdropMode mode);
        void ApplyAppTheme(AppThemeMode mode);
    }
}

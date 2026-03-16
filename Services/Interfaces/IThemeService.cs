using Microsoft.UI.Xaml;

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

        void Initialize(Window window);
        bool ApplyBackdrop(BackdropMode mode);
        void ApplyAppTheme(AppThemeMode mode);
    }
}

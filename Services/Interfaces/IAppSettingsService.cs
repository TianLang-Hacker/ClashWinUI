using ClashWinUI.Models;
using System;

namespace ClashWinUI.Services.Interfaces
{
    public interface IAppSettingsService
    {
        event EventHandler? SettingsChanged;

        bool WelcomeCompleted { get; set; }

        string LanguageTag { get; set; }

        AppThemeMode AppThemeMode { get; set; }

        BackdropMode BackdropMode { get; set; }

        CloseBehavior CloseBehavior { get; set; }

        bool ProxyGroupsExpandedByDefault { get; set; }
    }
}

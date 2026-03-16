using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.ComponentModel;

namespace ClashWinUI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        public const string ThemeSystem = "system";
        public const string ThemeLight = "light";
        public const string ThemeDark = "dark";

        public const string BackdropMica = "mica";
        public const string BackdropMicaAlt = "mica_alt";
        public const string BackdropAcrylic = "acrylic";

        public const string CloseBehaviorMinimizeToTray = "minimize_to_tray";
        public const string CloseBehaviorExit = "exit";

        private readonly LocalizedStrings _localizedStrings;
        private readonly IThemeService _themeService;
        private readonly IKernelPathService _kernelPathService;
        private readonly IAppSettingsService _appSettingsService;

        private bool _isUpdatingFromLocalization;
        private bool _isUpdatingFromThemeService;
        private bool _isUpdatingFromAppSettings;

        [ObservableProperty]
        public partial string Title { get; set; }

        [ObservableProperty]
        public partial string SelectedLanguageTag { get; set; }

        [ObservableProperty]
        public partial string SelectedAppThemeTag { get; set; }

        [ObservableProperty]
        public partial string SelectedBackdropTag { get; set; }

        [ObservableProperty]
        public partial string KernelPathInput { get; set; }

        [ObservableProperty]
        public partial string SelectedCloseBehaviorTag { get; set; }

        public SettingsViewModel(
            LocalizedStrings localizedStrings,
            IThemeService themeService,
            IKernelPathService kernelPathService,
            IAppSettingsService appSettingsService)
        {
            _localizedStrings = localizedStrings;
            _themeService = themeService;
            _kernelPathService = kernelPathService;
            _appSettingsService = appSettingsService;

            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;
            _appSettingsService.SettingsChanged += OnAppSettingsChanged;

            Title = _localizedStrings["PageSettings"];
            SelectedLanguageTag = _localizedStrings.CurrentLanguage;
            SelectedAppThemeTag = MapAppThemeToTag(_themeService.CurrentAppTheme);
            SelectedBackdropTag = MapBackdropToTag(_themeService.CurrentBackdrop);
            KernelPathInput = _kernelPathService.CustomKernelPath ?? _kernelPathService.DefaultKernelPath;
            SelectedCloseBehaviorTag = MapCloseBehaviorToTag(_appSettingsService.CloseBehavior);
        }

        [RelayCommand]
        private void SaveKernelPath()
        {
            _kernelPathService.SetCustomKernelPath(KernelPathInput);
            KernelPathInput = _kernelPathService.CustomKernelPath ?? _kernelPathService.DefaultKernelPath;
        }

        partial void OnSelectedLanguageTagChanged(string value)
        {
            if (_isUpdatingFromLocalization)
            {
                return;
            }

            _localizedStrings.SetLanguage(value);
        }

        partial void OnSelectedAppThemeTagChanged(string value)
        {
            if (_isUpdatingFromThemeService)
            {
                return;
            }

            if (!TryMapTagToAppTheme(value, out AppThemeMode mode))
            {
                return;
            }

            _themeService.ApplyAppTheme(mode);
            _isUpdatingFromThemeService = true;
            SelectedAppThemeTag = MapAppThemeToTag(_themeService.CurrentAppTheme);
            _isUpdatingFromThemeService = false;
        }

        partial void OnSelectedBackdropTagChanged(string value)
        {
            if (_isUpdatingFromThemeService)
            {
                return;
            }

            if (!TryMapTagToBackdrop(value, out BackdropMode mode))
            {
                return;
            }

            bool applied = _themeService.ApplyBackdrop(mode);
            if (!applied)
            {
                _isUpdatingFromThemeService = true;
                SelectedBackdropTag = MapBackdropToTag(_themeService.CurrentBackdrop);
                _isUpdatingFromThemeService = false;
            }
        }

        partial void OnSelectedCloseBehaviorTagChanged(string value)
        {
            if (_isUpdatingFromAppSettings)
            {
                return;
            }

            if (!TryMapTagToCloseBehavior(value, out CloseBehavior closeBehavior))
            {
                return;
            }

            _appSettingsService.CloseBehavior = closeBehavior;
            _isUpdatingFromAppSettings = true;
            SelectedCloseBehaviorTag = MapCloseBehaviorToTag(_appSettingsService.CloseBehavior);
            _isUpdatingFromAppSettings = false;
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(LocalizedStrings.CurrentLanguage) && e.PropertyName != "Item[]")
            {
                return;
            }

            Title = _localizedStrings["PageSettings"];
            if (!string.Equals(SelectedLanguageTag, _localizedStrings.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                _isUpdatingFromLocalization = true;
                SelectedLanguageTag = _localizedStrings.CurrentLanguage;
                _isUpdatingFromLocalization = false;
            }
        }

        private void OnAppSettingsChanged(object? sender, EventArgs e)
        {
            string behaviorTag = MapCloseBehaviorToTag(_appSettingsService.CloseBehavior);
            if (string.Equals(SelectedCloseBehaviorTag, behaviorTag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _isUpdatingFromAppSettings = true;
            SelectedCloseBehaviorTag = behaviorTag;
            _isUpdatingFromAppSettings = false;
        }

        private static bool TryMapTagToAppTheme(string tag, out AppThemeMode mode)
        {
            mode = tag switch
            {
                ThemeLight => AppThemeMode.Light,
                ThemeDark => AppThemeMode.Dark,
                _ => AppThemeMode.System,
            };

            return true;
        }

        private static string MapAppThemeToTag(AppThemeMode mode)
        {
            return mode switch
            {
                AppThemeMode.Light => ThemeLight,
                AppThemeMode.Dark => ThemeDark,
                _ => ThemeSystem,
            };
        }

        private static bool TryMapTagToBackdrop(string tag, out BackdropMode mode)
        {
            mode = tag switch
            {
                BackdropMicaAlt => BackdropMode.MicaAlt,
                BackdropAcrylic => BackdropMode.Acrylic,
                _ => BackdropMode.Mica,
            };

            return true;
        }

        private static string MapBackdropToTag(BackdropMode mode)
        {
            return mode switch
            {
                BackdropMode.MicaAlt => BackdropMicaAlt,
                BackdropMode.Acrylic => BackdropAcrylic,
                _ => BackdropMica,
            };
        }

        private static bool TryMapTagToCloseBehavior(string tag, out CloseBehavior closeBehavior)
        {
            closeBehavior = tag switch
            {
                CloseBehaviorExit => CloseBehavior.Exit,
                _ => CloseBehavior.MinimizeToTray,
            };

            return true;
        }

        private static string MapCloseBehaviorToTag(CloseBehavior closeBehavior)
        {
            return closeBehavior switch
            {
                CloseBehavior.Exit => CloseBehaviorExit,
                _ => CloseBehaviorMinimizeToTray,
            };
        }
    }
}

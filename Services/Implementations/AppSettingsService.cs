using ClashWinUI.Models;
using ClashWinUI.Serialization;
using ClashWinUI.Services.Interfaces;
using System;
using System.IO;
using System.Text.Json;

namespace ClashWinUI.Services.Implementations
{
    public class AppSettingsService : IAppSettingsService
    {
        private const string SettingsFileName = "appsettings.json";

        private readonly string _settingsFilePath;
        private readonly object _gate = new();
        private AppSettingsState _settings;

        public AppSettingsService()
        {
            string appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appSettingsDir = Path.Combine(appDataRoot, "ClashWinUI");
            _settingsFilePath = Path.Combine(appSettingsDir, SettingsFileName);

            _settings = LoadSettings();
        }

        public event EventHandler? SettingsChanged;

        public bool WelcomeCompleted
        {
            get
            {
                lock (_gate)
                {
                    return _settings.WelcomeCompleted;
                }
            }
            set
            {
                bool changed;
                lock (_gate)
                {
                    if (_settings.WelcomeCompleted == value)
                    {
                        return;
                    }

                    _settings.WelcomeCompleted = value;
                    SaveSettingsUnsafe();
                    changed = true;
                }

                if (changed)
                {
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string LanguageTag
        {
            get
            {
                lock (_gate)
                {
                    return _settings.LanguageTag;
                }
            }
            set
            {
                string normalized = NormalizeLanguageTag(value);
                bool changed;
                lock (_gate)
                {
                    if (string.Equals(_settings.LanguageTag, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    _settings.LanguageTag = normalized;
                    SaveSettingsUnsafe();
                    changed = true;
                }

                if (changed)
                {
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public AppThemeMode AppThemeMode
        {
            get
            {
                lock (_gate)
                {
                    return _settings.AppThemeMode;
                }
            }
            set
            {
                bool changed;
                lock (_gate)
                {
                    if (_settings.AppThemeMode == value)
                    {
                        return;
                    }

                    _settings.AppThemeMode = value;
                    SaveSettingsUnsafe();
                    changed = true;
                }

                if (changed)
                {
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public BackdropMode BackdropMode
        {
            get
            {
                lock (_gate)
                {
                    return _settings.BackdropMode;
                }
            }
            set
            {
                bool changed;
                lock (_gate)
                {
                    if (_settings.BackdropMode == value)
                    {
                        return;
                    }

                    _settings.BackdropMode = value;
                    SaveSettingsUnsafe();
                    changed = true;
                }

                if (changed)
                {
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public CloseBehavior CloseBehavior
        {
            get
            {
                lock (_gate)
                {
                    return _settings.CloseBehavior;
                }
            }
            set
            {
                bool changed;
                lock (_gate)
                {
                    if (_settings.CloseBehavior == value)
                    {
                        return;
                    }

                    _settings.CloseBehavior = value;
                    SaveSettingsUnsafe();
                    changed = true;
                }

                if (changed)
                {
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool ProxyGroupsExpandedByDefault
        {
            get
            {
                lock (_gate)
                {
                    return _settings.ProxyGroupsExpandedByDefault;
                }
            }
            set
            {
                bool changed;
                lock (_gate)
                {
                    if (_settings.ProxyGroupsExpandedByDefault == value)
                    {
                        return;
                    }

                    _settings.ProxyGroupsExpandedByDefault = value;
                    SaveSettingsUnsafe();
                    changed = true;
                }

                if (changed)
                {
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private AppSettingsState LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return new AppSettingsState();
                }

                string content = File.ReadAllText(_settingsFilePath);
                AppSettingsState? loaded = JsonSerializer.Deserialize(content, ClashJsonContext.Default.AppSettingsState);
                if (loaded is null)
                {
                    return new AppSettingsState();
                }

                return loaded;
            }
            catch
            {
                return new AppSettingsState();
            }
        }

        private void SaveSettingsUnsafe()
        {
            string? settingsDirectory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }

            string content = JsonSerializer.Serialize(_settings, ClashJsonContext.Default.AppSettingsState);

            File.WriteAllText(_settingsFilePath, content);
        }

        private static string NormalizeLanguageTag(string? languageTag)
        {
            return string.IsNullOrWhiteSpace(languageTag)
                ? string.Empty
                : languageTag.Trim();
        }
    }
}

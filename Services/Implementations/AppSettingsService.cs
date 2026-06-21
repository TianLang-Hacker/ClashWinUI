using ClashWinUI.Models;
using ClashWinUI.Serialization;
using ClashWinUI.Services.Interfaces;
using ClashWinUI.Common;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace ClashWinUI.Services.Implementations
{
    public class AppSettingsService : IAppSettingsService, IDisposable
    {
        private const string SettingsFileName = "appsettings.json";

        private readonly string _settingsFilePath;
        private readonly object _gate = new();
        private readonly SynchronizationContext? _synchronizationContext;
        private readonly FileSystemWatcher? _settingsWatcher;
        private AppSettingsState _settings;
        private int _reloadQueued;

        public AppSettingsService()
        {
            string appSettingsDir = AppDataPaths.RootDirectory;
            _settingsFilePath = Path.Combine(appSettingsDir, SettingsFileName);
            _synchronizationContext = SynchronizationContext.Current;

            _settings = LoadSettings();
            _settingsWatcher = CreateSettingsWatcher();
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
                    RaiseSettingsChanged();
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
                    RaiseSettingsChanged();
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
                    RaiseSettingsChanged();
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
                    RaiseSettingsChanged();
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
                    RaiseSettingsChanged();
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
                    RaiseSettingsChanged();
                }
            }
        }

        public void Dispose()
        {
            if (_settingsWatcher is null)
            {
                return;
            }

            _settingsWatcher.EnableRaisingEvents = false;
            _settingsWatcher.Dispose();
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

        private FileSystemWatcher? CreateSettingsWatcher()
        {
            try
            {
                string? directory = Path.GetDirectoryName(_settingsFilePath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return null;
                }

                Directory.CreateDirectory(directory);
                var watcher = new FileSystemWatcher(directory, Path.GetFileName(_settingsFilePath))
                {
                    NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += OnSettingsFileChanged;
                watcher.Created += OnSettingsFileChanged;
                watcher.Renamed += OnSettingsFileChanged;
                return watcher;
            }
            catch
            {
                return null;
            }
        }

        private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
        {
            if (Interlocked.Exchange(ref _reloadQueued, 1) == 1)
            {
                return;
            }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(150).ConfigureAwait(false);
                    ReloadSettingsFromDisk();
                }
                finally
                {
                    Interlocked.Exchange(ref _reloadQueued, 0);
                }
            });
        }

        private void ReloadSettingsFromDisk()
        {
            AppSettingsState reloaded = LoadSettings();
            lock (_gate)
            {
                string currentPayload = JsonSerializer.Serialize(_settings, ClashJsonContext.Default.AppSettingsState);
                string nextPayload = JsonSerializer.Serialize(reloaded, ClashJsonContext.Default.AppSettingsState);
                if (string.Equals(currentPayload, nextPayload, StringComparison.Ordinal))
                {
                    return;
                }

                _settings = reloaded;
            }

            RaiseSettingsChanged();
        }

        private void RaiseSettingsChanged()
        {
            if (_synchronizationContext is not null)
            {
                _synchronizationContext.Post(_ => SettingsChanged?.Invoke(this, EventArgs.Empty), null);
                return;
            }

            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        private static string NormalizeLanguageTag(string? languageTag)
        {
            return string.IsNullOrWhiteSpace(languageTag)
                ? string.Empty
                : languageTag.Trim();
        }
    }
}

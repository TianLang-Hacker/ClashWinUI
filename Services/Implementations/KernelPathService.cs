using ClashWinUI.Serialization;
using ClashWinUI.Services.Interfaces;
using System;
using System.IO;
using System.Text.Json;

namespace ClashWinUI.Services.Implementations
{
    public class KernelPathService : IKernelPathService
    {
        private const string SettingsFileName = "settings.json";
        private const string KernelExecutableName = "mihomo.exe";

        private readonly string _settingsFilePath;
        private KernelPathSettingsState _settings;

        public KernelPathService()
        {
            string appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appSettingsDir = Path.Combine(appDataRoot, "ClashWinUI");
            _settingsFilePath = Path.Combine(appSettingsDir, SettingsFileName);
            _settings = LoadSettings();
        }

        public string DefaultKernelPath
        {
            get
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(userProfile, "ClashWinUI", "Kernel", KernelExecutableName);
            }
        }

        public string? CustomKernelPath => _settings.CustomKernelPath;

        public string ResolveKernelPath()
        {
            if (!string.IsNullOrWhiteSpace(_settings.CustomKernelPath) && File.Exists(_settings.CustomKernelPath))
            {
                return _settings.CustomKernelPath;
            }

            return DefaultKernelPath;
        }

        public void SetCustomKernelPath(string? path)
        {
            _settings.CustomKernelPath = NormalizePath(path);
            SaveSettings();
        }

        private KernelPathSettingsState LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return new KernelPathSettingsState();
                }

                string content = File.ReadAllText(_settingsFilePath);
                KernelPathSettingsState? loaded = JsonSerializer.Deserialize(content, ClashJsonContext.Default.KernelPathSettingsState);
                if (loaded is null)
                {
                    return new KernelPathSettingsState();
                }

                loaded.CustomKernelPath = NormalizePath(loaded.CustomKernelPath);
                return loaded;
            }
            catch
            {
                return new KernelPathSettingsState();
            }
        }

        private void SaveSettings()
        {
            string? settingsDirectory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }

            string content = JsonSerializer.Serialize(_settings, ClashJsonContext.Default.KernelPathSettingsState);

            File.WriteAllText(_settingsFilePath, content);
        }

        private static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string trimmed = path.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            string fullPath = Path.GetFullPath(trimmed);
            if (fullPath.EndsWith(Path.DirectorySeparatorChar) || fullPath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                return Path.Combine(fullPath, KernelExecutableName);
            }

            return fullPath;
        }
    }
}

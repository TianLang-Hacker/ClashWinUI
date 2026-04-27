using ClashWinUI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace ClashWinUI.Helpers
{
    public class LocalizedStrings : INotifyPropertyChanged
    {
        private const string DefaultLanguage = "en-US";
        private const string SimplifiedChinese = "zh-Hans";
        private const string TraditionalChinese = "zh-Hant";
        private static readonly string[] SupportedLanguages = ["en-US", SimplifiedChinese, TraditionalChinese];

        private static readonly object SharedGate = new();
        private static readonly Dictionary<string, Dictionary<string, string>> SharedResources = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<WeakReference<LocalizedStrings>> Instances = new();
        private static bool resourcesLoaded;
        private static string sharedCurrentLanguage = DefaultLanguage;

        private IAppSettingsService? _appSettingsService;
        private string _currentLanguage = DefaultLanguage;

        public LocalizedStrings()
        {
            StartupTrace.Write("LocalizedStrings ctor: start");
            EnsureResourcesLoaded();

            string preferredLanguage = NormalizeLanguage(CultureInfo.CurrentUICulture.Name);
            lock (SharedGate)
            {
                Instances.Add(new WeakReference<LocalizedStrings>(this));
                if (string.Equals(sharedCurrentLanguage, DefaultLanguage, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(preferredLanguage, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    sharedCurrentLanguage = preferredLanguage;
                }

                _currentLanguage = sharedCurrentLanguage;
            }

            ApplyCulture(_currentLanguage);
            StartupTrace.Write($"LocalizedStrings ctor: completed language={_currentLanguage}");
        }

        public string CurrentLanguage
        {
            get => _currentLanguage;
            private set
            {
                if (string.Equals(_currentLanguage, value, StringComparison.Ordinal))
                {
                    return;
                }

                _currentLanguage = value;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(CurrentLanguage)));
            }
        }

        public string this[string key] => GetString(key);

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Initialize(IAppSettingsService appSettingsService)
        {
            _appSettingsService = appSettingsService;
            if (!string.IsNullOrWhiteSpace(appSettingsService.LanguageTag))
            {
                SetLanguage(appSettingsService.LanguageTag);
                return;
            }

            appSettingsService.LanguageTag = CurrentLanguage;
        }

        public void SetLanguage(string languageTag)
        {
            string normalized = NormalizeLanguage(languageTag);
            StartupTrace.Write($"LocalizedStrings.SetLanguage: requested={languageTag}, normalized={normalized}");

            bool changed;
            lock (SharedGate)
            {
                changed = !string.Equals(sharedCurrentLanguage, normalized, StringComparison.OrdinalIgnoreCase);
                sharedCurrentLanguage = normalized;
            }

            ApplyCulture(normalized);
            NotifyLanguageChangedForAllInstances(normalized);

            if (_appSettingsService is not null
                && !string.Equals(_appSettingsService.LanguageTag, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _appSettingsService.LanguageTag = normalized;
                StartupTrace.Write("LocalizedStrings.SetLanguage: persisted");
            }

            if (!changed)
            {
                StartupTrace.Write("LocalizedStrings.SetLanguage: unchanged shared language");
            }
        }

        private string GetString(string key)
        {
            EnsureResourcesLoaded();
            string currentLanguage = CurrentLanguage;
            lock (SharedGate)
            {
                if (SharedResources.TryGetValue(currentLanguage, out Dictionary<string, string>? current)
                    && current.TryGetValue(key, out string? value)
                    && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                if (SharedResources.TryGetValue(DefaultLanguage, out Dictionary<string, string>? fallback)
                    && fallback.TryGetValue(key, out string? fallbackValue)
                    && !string.IsNullOrWhiteSpace(fallbackValue))
                {
                    return fallbackValue;
                }
            }

            return key;
        }

        private static void EnsureResourcesLoaded()
        {
            lock (SharedGate)
            {
                if (resourcesLoaded)
                {
                    return;
                }

                foreach (string language in SupportedLanguages)
                {
                    StartupTrace.Write($"LocalizedStrings: loading {language}");
                    SharedResources[language] = LoadResourceDictionary(language);
                    StartupTrace.Write($"LocalizedStrings: loaded {language}");
                }

                resourcesLoaded = true;
            }
        }

        private static void NotifyLanguageChangedForAllInstances(string languageTag)
        {
            List<WeakReference<LocalizedStrings>> deadReferences = [];
            lock (SharedGate)
            {
                foreach (WeakReference<LocalizedStrings> reference in Instances)
                {
                    if (reference.TryGetTarget(out LocalizedStrings? instance))
                    {
                        instance.ApplyLanguage(languageTag);
                    }
                    else
                    {
                        deadReferences.Add(reference);
                    }
                }

                foreach (WeakReference<LocalizedStrings> reference in deadReferences)
                {
                    Instances.Remove(reference);
                }
            }
        }

        private void ApplyLanguage(string languageTag)
        {
            CurrentLanguage = languageTag;
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        }

        private static void ApplyCulture(string languageTag)
        {
            CultureInfo.CurrentUICulture = new CultureInfo(languageTag);
            CultureInfo.CurrentCulture = new CultureInfo(languageTag);
        }

        private void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            PropertyChanged?.Invoke(this, e);
        }

        private static string NormalizeLanguage(string? languageTag)
        {
            if (string.IsNullOrWhiteSpace(languageTag))
            {
                return DefaultLanguage;
            }

            foreach (string supportedLanguage in SupportedLanguages)
            {
                if (string.Equals(supportedLanguage, languageTag, StringComparison.OrdinalIgnoreCase))
                {
                    return supportedLanguage;
                }
            }

            if (languageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                if (languageTag.Contains("hant", StringComparison.OrdinalIgnoreCase)
                    || languageTag.Contains("tw", StringComparison.OrdinalIgnoreCase)
                    || languageTag.Contains("hk", StringComparison.OrdinalIgnoreCase)
                    || languageTag.Contains("mo", StringComparison.OrdinalIgnoreCase))
                {
                    return TraditionalChinese;
                }

                return SimplifiedChinese;
            }

            return DefaultLanguage;
        }

        private static Dictionary<string, string> LoadResourceDictionary(string languageTag)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(AppContext.BaseDirectory, "i18n", languageTag, "Resources.resw");
            StartupTrace.Write($"LocalizedStrings.LoadResourceDictionary: path={path}");

            if (!File.Exists(path))
            {
                StartupTrace.Write("LocalizedStrings.LoadResourceDictionary: file missing");
                return values;
            }

            XDocument document = XDocument.Load(path);
            foreach (XElement data in document.Descendants("data"))
            {
                XAttribute? nameAttribute = data.Attribute("name");
                XElement? valueElement = data.Element("value");

                if (nameAttribute is null || valueElement is null)
                {
                    continue;
                }

                values[nameAttribute.Value] = valueElement.Value;
            }

            StartupTrace.Write($"LocalizedStrings.LoadResourceDictionary: values={values.Count}");
            return values;
        }
    }
}

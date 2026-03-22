using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace ClashWinUI.Helpers
{
    public class LocalizedStrings : ObservableObject
    {
        private const string DefaultLanguage = "en-US";
        private const string SimplifiedChinese = "zh-Hans";
        private const string TraditionalChinese = "zh-Hant";
        private static readonly string[] SupportedLanguages = ["en-US", SimplifiedChinese, TraditionalChinese];

        private readonly Dictionary<string, Dictionary<string, string>> _resources = new(StringComparer.OrdinalIgnoreCase);
        private string _currentLanguage = DefaultLanguage;

        public LocalizedStrings()
        {
            foreach (string language in SupportedLanguages)
            {
                _resources[language] = LoadResourceDictionary(language);
            }

            string preferredLanguage = NormalizeLanguage(CultureInfo.CurrentUICulture.Name);

            SetLanguage(preferredLanguage);
        }

        public string CurrentLanguage
        {
            get => _currentLanguage;
            private set => SetProperty(ref _currentLanguage, value);
        }

        public string this[string key] => GetString(key);

        public void SetLanguage(string languageTag)
        {
            string normalized = NormalizeLanguage(languageTag);
            if (string.Equals(CurrentLanguage, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CurrentLanguage = normalized;
            CultureInfo.CurrentUICulture = new CultureInfo(CurrentLanguage);
            CultureInfo.CurrentCulture = new CultureInfo(CurrentLanguage);

            // Notify all indexer bindings to refresh.
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        }

        private string GetString(string key)
        {
            if (_resources.TryGetValue(CurrentLanguage, out Dictionary<string, string>? current)
                && current.TryGetValue(key, out string? value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (_resources.TryGetValue(DefaultLanguage, out Dictionary<string, string>? fallback)
                && fallback.TryGetValue(key, out string? fallbackValue)
                && !string.IsNullOrWhiteSpace(fallbackValue))
            {
                return fallbackValue;
            }

            return key;
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

            if (!File.Exists(path))
            {
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

            return values;
        }
    }
}

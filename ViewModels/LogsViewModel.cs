using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.UI;

namespace ClashWinUI.ViewModels
{
    public partial class LogsViewModel : ObservableObject
    {
        public const string LevelAllTag = "all";
        public const string LevelTraceTag = "trace";
        public const string LevelDebugTag = "debug";
        public const string LevelInfoTag = "info";
        public const string LevelWarningTag = "warning";
        public const string LevelErrorTag = "error";

        private const int MaxLines = 2000;

        private readonly LocalizedStrings _localizedStrings;
        private readonly IAppLogService _logService;
        private readonly IThemeService _themeService;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly List<LogEntry> _allEntries = new();

        [ObservableProperty]
        public partial string Title { get; set; }

        [ObservableProperty]
        public partial string LogsText { get; set; }

        [ObservableProperty]
        public partial string SelectedLevelFilterTag { get; set; }

        [ObservableProperty]
        public partial string SearchKeyword { get; set; }

        [ObservableProperty]
        public partial bool IsAutoScrollEnabled { get; set; }

        public ObservableCollection<LogsListItem> FilteredLogEntries { get; } = new();

        public LogsViewModel(LocalizedStrings localizedStrings, IAppLogService logService, IThemeService themeService)
        {
            _localizedStrings = localizedStrings;
            _logService = logService;
            _themeService = themeService;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            _localizedStrings.PropertyChanged += OnLocalizedStringsPropertyChanged;
            _logService.LogAdded += OnLogAdded;
            _themeService.ThemeChanged += OnThemeChanged;

            Title = _localizedStrings["PageLogs"];
            LogsText = string.Empty;
            SelectedLevelFilterTag = LevelAllTag;
            SearchKeyword = string.Empty;
            IsAutoScrollEnabled = true;

            foreach (LogEntry entry in _logService.GetLogs())
            {
                _allEntries.Add(entry);
            }

            RefreshLogsText();
            ApplyFilters();
        }

        partial void OnSelectedLevelFilterTagChanged(string value)
        {
            ApplyFilters();
        }

        partial void OnSearchKeywordChanged(string value)
        {
            ApplyFilters();
        }

        private void OnLogAdded(object? sender, LogEntry entry)
        {
            if (_dispatcherQueue.HasThreadAccess)
            {
                AppendEntry(entry);
                return;
            }

            _dispatcherQueue.TryEnqueue(() => AppendEntry(entry));
        }

        private void AppendEntry(LogEntry entry)
        {
            _allEntries.Add(entry);
            LogEntry? removedEntry = null;
            if (_allEntries.Count > MaxLines)
            {
                removedEntry = _allEntries[0];
                _allEntries.RemoveAt(0);
            }

            if (removedEntry is not null)
            {
                RemoveFirstFilteredEntry(removedEntry);
            }

            RefreshLogsText();
            if (MatchesCurrentFilters(entry))
            {
                FilteredLogEntries.Add(new LogsListItem(entry.Level, FormatEntry(entry), CreateForegroundBrush(entry.Level)));
            }
        }

        private void RefreshLogsText()
        {
            LogsText = string.Join(Environment.NewLine, _allEntries.ConvertAll(FormatEntry));
        }

        private void ApplyFilters()
        {
            FilteredLogEntries.Clear();
            foreach (LogEntry entry in _allEntries)
            {
                if (!MatchesCurrentFilters(entry))
                {
                    continue;
                }

                FilteredLogEntries.Add(new LogsListItem(entry.Level, FormatEntry(entry), CreateForegroundBrush(entry.Level)));
            }
        }

        private bool MatchesLevelFilter(LogLevel level)
        {
            return SelectedLevelFilterTag switch
            {
                LevelTraceTag => level == LogLevel.Trace,
                LevelDebugTag => level == LogLevel.Debug,
                LevelInfoTag => level == LogLevel.Info,
                LevelWarningTag => level == LogLevel.Warning,
                LevelErrorTag => level == LogLevel.Error,
                _ => true,
            };
        }

        [RelayCommand]
        private void ClearLogs()
        {
            _logService.Clear();
            _allEntries.Clear();
            FilteredLogEntries.Clear();
            LogsText = string.Empty;
        }

        private bool MatchesCurrentFilters(LogEntry entry)
        {
            if (!MatchesLevelFilter(entry.Level))
            {
                return false;
            }

            string keyword = SearchKeyword?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return true;
            }

            return entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        private void RemoveFirstFilteredEntry(LogEntry entry)
        {
            if (FilteredLogEntries.Count == 0)
            {
                return;
            }

            string targetText = FormatEntry(entry);
            for (int i = 0; i < FilteredLogEntries.Count; i++)
            {
                LogsListItem item = FilteredLogEntries[i];
                if (item.Level == entry.Level && string.Equals(item.DisplayText, targetText, StringComparison.Ordinal))
                {
                    FilteredLogEntries.RemoveAt(i);
                    break;
                }
            }
        }

        private static string FormatEntry(LogEntry entry)
        {
            return $"[{entry.Timestamp:HH:mm:ss}] [{ToLevelText(entry.Level)}] {entry.Message}";
        }

        private static string ToLevelText(LogLevel level)
        {
            return level switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Warning => "WARNING",
                LogLevel.Error => "ERROR",
                _ => "INFO",
            };
        }

        private void OnLocalizedStringsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocalizedStrings.CurrentLanguage) || e.PropertyName == "Item[]")
            {
                Title = _localizedStrings["PageLogs"];
            }
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            if (_dispatcherQueue.HasThreadAccess)
            {
                RefreshLogItemBrushes();
                return;
            }

            _dispatcherQueue.TryEnqueue(RefreshLogItemBrushes);
        }

        private void RefreshLogItemBrushes()
        {
            foreach (LogsListItem item in FilteredLogEntries)
            {
                item.ForegroundBrush = CreateForegroundBrush(item.Level);
            }
        }

        private Brush CreateForegroundBrush(LogLevel level)
        {
            bool isLightTheme = GetCurrentActualTheme() == ElementTheme.Light;

            Color color = level switch
            {
                LogLevel.Trace => isLightTheme
                    ? Color.FromArgb(255, 104, 104, 104)
                    : Color.FromArgb(255, 176, 176, 176),
                LogLevel.Debug => isLightTheme
                    ? Color.FromArgb(255, 0, 95, 184)
                    : Color.FromArgb(255, 120, 200, 255),
                LogLevel.Warning => isLightTheme
                    ? Color.FromArgb(255, 151, 99, 0)
                    : Color.FromArgb(255, 255, 196, 84),
                LogLevel.Error => isLightTheme
                    ? Color.FromArgb(255, 183, 28, 28)
                    : Color.FromArgb(255, 255, 120, 120),
                _ => isLightTheme
                    ? Color.FromArgb(255, 17, 17, 17)
                    : Color.FromArgb(255, 255, 255, 255),
            };

            return new SolidColorBrush(color);
        }

        private static ElementTheme GetCurrentActualTheme()
        {
            if (Application.Current is App app && app.ActiveWindow?.Content is FrameworkElement root)
            {
                return root.ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
            }

            return ElementTheme.Dark;
        }
    }

    public sealed partial class LogsListItem : ObservableObject
    {
        public LogsListItem(LogLevel level, string displayText, Brush foregroundBrush)
        {
            Level = level;
            DisplayText = displayText;
            ForegroundBrush = foregroundBrush;
        }

        public LogLevel Level { get; }
        public string DisplayText { get; }

        [ObservableProperty]
        public partial Brush ForegroundBrush { get; set; }
    }
}

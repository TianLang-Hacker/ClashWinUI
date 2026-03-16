using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace ClashWinUI.Services.Implementations
{
    public class AppLogService : IAppLogService
    {
        private const int MaxLines = 2000;
        private readonly object _gate = new();
        private readonly List<LogEntry> _entries = new();

        public event EventHandler<LogEntry>? LogAdded;

        public IReadOnlyList<LogEntry> GetLogs()
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }

        public void Add(string message, LogLevel level = LogLevel.Info)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string normalizedMessage = message.Trim();
            LogLevel effectiveLevel = level;
            if (level == LogLevel.Info)
            {
                (effectiveLevel, normalizedMessage) = InferLevelAndNormalizeMessage(normalizedMessage);
            }

            var entry = new LogEntry(DateTime.Now, effectiveLevel, normalizedMessage);

            lock (_gate)
            {
                _entries.Add(entry);
                if (_entries.Count > MaxLines)
                {
                    _entries.RemoveAt(0);
                }
            }

            LogAdded?.Invoke(this, entry);
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }

        private static (LogLevel Level, string Message) InferLevelAndNormalizeMessage(string message)
        {
            (string Prefix, LogLevel Level)[] mappings =
            [
                ("ERROR:", LogLevel.Error),
                ("[ERROR]", LogLevel.Error),
                ("ERR:", LogLevel.Error),
                ("WARNING:", LogLevel.Warning),
                ("WARN:", LogLevel.Warning),
                ("[WARNING]", LogLevel.Warning),
                ("[WARN]", LogLevel.Warning),
                ("DEBUG:", LogLevel.Debug),
                ("[DEBUG]", LogLevel.Debug),
                ("TRACE:", LogLevel.Trace),
                ("[TRACE]", LogLevel.Trace),
                ("INFO:", LogLevel.Info),
                ("[INFO]", LogLevel.Info),
            ];

            foreach ((string prefix, LogLevel mappedLevel) in mappings)
            {
                if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string stripped = message[prefix.Length..].TrimStart();
                return (mappedLevel, string.IsNullOrWhiteSpace(stripped) ? message : stripped);
            }

            return (LogLevel.Info, message);
        }
    }
}

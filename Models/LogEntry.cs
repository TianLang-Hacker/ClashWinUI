using System;

namespace ClashWinUI.Models
{
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warning = 3,
        Error = 4
    }

    public sealed class LogEntry
    {
        public DateTime Timestamp { get; }
        public LogLevel Level { get; }
        public string Message { get; }

        public LogEntry(DateTime timestamp, LogLevel level, string message)
        {
            Timestamp = timestamp;
            Level = level;
            Message = message;
        }
    }
}

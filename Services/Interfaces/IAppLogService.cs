using ClashWinUI.Models;
using System;
using System.Collections.Generic;

namespace ClashWinUI.Services.Interfaces
{
    public interface IAppLogService
    {
        event EventHandler<LogEntry>? LogAdded;

        IReadOnlyList<LogEntry> GetLogs();
        void Add(string message, LogLevel level = LogLevel.Info);
        void Clear();
    }
}

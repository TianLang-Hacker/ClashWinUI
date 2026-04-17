using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;

namespace ClashWinUI.Helpers
{
    public static class PerformanceTraceHelper
    {
        public static void LogElapsed(string operationName, TimeSpan elapsed, TimeSpan slowThreshold)
        {
#if DEBUG
            bool shouldLog = true;
#else
            bool shouldLog = elapsed >= slowThreshold;
#endif
            if (!shouldLog)
            {
                return;
            }

            LogLevel level = elapsed >= slowThreshold
                ? LogLevel.Warning
                : LogLevel.Debug;
            string message = $"[Perf] {operationName}: {elapsed.TotalMilliseconds:F0} ms";

            try
            {
                if (Application.Current is App app)
                {
                    app.GetRequiredService<IAppLogService>().Add(message, level);
                    return;
                }
            }
            catch
            {
                // Fall back to Debug.WriteLine when DI is not ready.
            }

            Debug.WriteLine(message);
        }
    }
}

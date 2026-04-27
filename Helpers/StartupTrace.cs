using System;
using System.Diagnostics;
using System.IO;

namespace ClashWinUI.Helpers
{
    internal static class StartupTrace
    {
        private static readonly object Gate = new();
        private static readonly string TraceFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClashWinUI",
            "Logs",
            "startup-trace.log");

        public static void Reset(string message)
        {
#if DEBUG
            try
            {
                lock (Gate)
                {
                    string? directory = Path.GetDirectoryName(TraceFilePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(
                        TraceFilePath,
                        $"===== Startup trace reset {DateTime.Now:O} | {message} ====={Environment.NewLine}");
                }
            }
            catch
            {
                // Startup tracing must never affect application startup.
            }
#endif
        }

        public static void Write(string message)
        {
#if DEBUG
            try
            {
                lock (Gate)
                {
                    string line = $"[{DateTime.Now:HH:mm:ss.fff}] [PID {Environment.ProcessId}] {message}{Environment.NewLine}";
                    File.AppendAllText(TraceFilePath, line);
                    Debug.WriteLine(line);
                }
            }
            catch
            {
                // Startup tracing must never affect application startup.
            }
#endif
        }

        public static void WriteException(string message, Exception exception)
        {
            Write($"{message}: {exception}");
        }
    }
}

using ClashWinUI.Common;
using System;
using System.IO;

namespace ClashWinUI.Helpers
{
    internal static class PendingElevatedStartStore
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

        public static void Save()
        {
            try
            {
                AppDataPaths.EnsureStateDirectory();
                File.WriteAllText(AppDataPaths.PendingElevatedStartFilePath, DateTimeOffset.UtcNow.ToString("O"));
            }
            catch
            {
                // Best-effort only.
            }
        }

        public static bool IsPending()
        {
            try
            {
                if (!File.Exists(AppDataPaths.PendingElevatedStartFilePath))
                {
                    return false;
                }

                string raw = File.ReadAllText(AppDataPaths.PendingElevatedStartFilePath).Trim();
                if (DateTimeOffset.TryParse(raw, out DateTimeOffset createdAt))
                {
                    if (DateTimeOffset.UtcNow - createdAt > MaxAge)
                    {
                        Clear();
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(AppDataPaths.PendingElevatedStartFilePath))
                {
                    File.Delete(AppDataPaths.PendingElevatedStartFilePath);
                }
            }
            catch
            {
                // Best-effort only.
            }
        }
    }
}

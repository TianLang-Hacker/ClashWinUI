using ClashWinUI.Common;
using ClashWinUI.Models;
using ClashWinUI.Serialization;
using System;
using System.IO;
using System.Text.Json;

namespace ClashWinUI.Helpers
{
    internal static class PendingLaunchStore
    {
        public static void Save(string routeKey)
        {
            try
            {
                AppDataPaths.EnsureStateDirectory();
                var command = new PendingLaunchCommand
                {
                    RouteKey = routeKey ?? string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow,
                };

                string payload = JsonSerializer.Serialize(command, ClashJsonContext.Default.PendingLaunchCommand);
                File.WriteAllText(AppDataPaths.PendingLaunchFilePath, payload);
            }
            catch
            {
                // Best-effort only.
            }
        }

        public static PendingLaunchCommand? TryConsume()
        {
            try
            {
                if (!File.Exists(AppDataPaths.PendingLaunchFilePath))
                {
                    return null;
                }

                string payload = File.ReadAllText(AppDataPaths.PendingLaunchFilePath);
                File.Delete(AppDataPaths.PendingLaunchFilePath);
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return null;
                }

                return JsonSerializer.Deserialize(payload, ClashJsonContext.Default.PendingLaunchCommand);
            }
            catch
            {
                return null;
            }
        }
    }
}

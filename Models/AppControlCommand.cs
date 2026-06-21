using System;

namespace ClashWinUI.Models
{
    internal enum AppControlCommandType
    {
        ShowRoute = 0,
        ShutdownUi = 1,
        ShutdownApp = 2,
    }

    internal sealed class AppControlCommand
    {
        public AppControlCommandType CommandType { get; set; } = AppControlCommandType.ShowRoute;

        public string RouteKey { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public static AppControlCommand CreateShowRoute(string routeKey)
        {
            return new AppControlCommand
            {
                CommandType = AppControlCommandType.ShowRoute,
                RouteKey = routeKey ?? string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
    }
}

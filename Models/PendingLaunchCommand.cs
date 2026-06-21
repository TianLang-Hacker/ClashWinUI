using System;

namespace ClashWinUI.Models
{
    internal sealed class PendingLaunchCommand
    {
        public string RouteKey { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}

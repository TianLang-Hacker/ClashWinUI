using System;

namespace ClashWinUI.Models
{
    public sealed class RuntimeStateSnapshot
    {
        public bool IsRunning { get; set; }

        public int ProcessId { get; set; }

        public string ControllerHost { get; set; } = "127.0.0.1";

        public int ControllerPort { get; set; } = 9090;

        public string CurrentConfigPath { get; set; } = string.Empty;

        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}

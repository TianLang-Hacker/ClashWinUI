using System;
using System.Collections.Generic;

namespace ClashWinUI.Models
{
    public enum ProxyGroupLoadSource
    {
        RuntimeFile = 0,
        MihomoController = 1,
    }

    public sealed class ProxyGroupLoadResult
    {
        public required IReadOnlyList<ProxyGroup> Groups { get; init; }
        public required ProxyGroupLoadSource Source { get; init; }
    }
}

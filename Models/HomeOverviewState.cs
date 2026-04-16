using System;

namespace ClashWinUI.Models
{
    public sealed class HomeOverviewState
    {
        public required int ConnectionsCount { get; init; }

        public required long? MemoryUsageBytes { get; init; }

        public required long DownloadTotalBytes { get; init; }

        public required long DownloadSpeedBytes { get; init; }

        public required long UploadTotalBytes { get; init; }

        public required long UploadSpeedBytes { get; init; }

        public required int RuntimeEventsCount { get; init; }

        public required SystemProxyState SystemProxyState { get; init; }

        public required string MixinPortsText { get; init; }

        public required string RulesCountText { get; init; }

        public required string KernelVersionText { get; init; }

        public required TunRuntimeStatus TunRuntimeStatus { get; init; }

        public required double[] DownloadValues { get; init; }

        public required double[] UploadValues { get; init; }

        public required double[] MemoryValues { get; init; }

        public required double TrafficAxisMax { get; init; }

        public required double MemoryAxisMax { get; init; }

        public required DateTimeOffset UpdatedAt { get; init; }
    }
}
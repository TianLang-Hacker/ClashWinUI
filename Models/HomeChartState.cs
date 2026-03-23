using System;

namespace ClashWinUI.Models
{
    public sealed class HomeChartState
    {
        public required double[] DownloadValues { get; init; }

        public required double[] UploadValues { get; init; }

        public required double[] MemoryValues { get; init; }

        public required double TrafficAxisMax { get; init; }

        public required double MemoryAxisMax { get; init; }

        public required DateTimeOffset UpdatedAt { get; init; }
    }
}

using System;

namespace ClashWinUI.Models
{
    public sealed class HomeChartSample
    {
        public required DateTimeOffset Timestamp { get; init; }

        public required double PrimaryValue { get; init; }

        public double SecondaryValue { get; init; }
    }
}

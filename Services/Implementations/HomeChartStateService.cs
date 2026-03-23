using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using System;
using System.Linq;

namespace ClashWinUI.Services.Implementations
{
    public sealed class HomeChartStateService : IHomeChartStateService
    {
        private const int ChartCapacity = 60;
        private static readonly double DefaultMemoryAxisMax = 10d * 1024d * 1024d;

        private HomeChartState _state = CreateDefaultState();

        public HomeChartState GetState()
        {
            return Clone(_state);
        }

        public void Save(HomeChartState state)
        {
            _state = Clone(state);
        }

        private static HomeChartState CreateDefaultState()
        {
            double[] zeros = Enumerable.Repeat(0d, ChartCapacity).ToArray();
            return new HomeChartState
            {
                DownloadValues = zeros.ToArray(),
                UploadValues = zeros.ToArray(),
                MemoryValues = zeros.ToArray(),
                TrafficAxisMax = 1d,
                MemoryAxisMax = DefaultMemoryAxisMax,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        private static HomeChartState Clone(HomeChartState state)
        {
            return new HomeChartState
            {
                DownloadValues = state.DownloadValues.ToArray(),
                UploadValues = state.UploadValues.ToArray(),
                MemoryValues = state.MemoryValues.ToArray(),
                TrafficAxisMax = state.TrafficAxisMax,
                MemoryAxisMax = state.MemoryAxisMax,
                UpdatedAt = state.UpdatedAt,
            };
        }
    }
}

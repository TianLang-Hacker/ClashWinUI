using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using System.Threading;

namespace ClashWinUI.Helpers
{
    internal static class LiveChartsBootstrapper
    {
        private static int _initialized;

        public static void EnsureInitialized()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 1)
            {
                return;
            }

            LiveCharts.Configure(settings =>
            {
                settings
                    .AddDefaultMappers()
                    .AddSkiaSharp();
            });
        }
    }
}

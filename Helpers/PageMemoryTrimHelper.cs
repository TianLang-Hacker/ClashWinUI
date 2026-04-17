using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Helpers
{
    public static class PageMemoryTrimHelper
    {
        private static int _trimScheduled;

        public static void RequestTrim(string reason = "general")
        {
            if (Interlocked.Exchange(ref _trimScheduled, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    // Let navigation settle first, then trim the large page graph.
                    await Task.Delay(250).ConfigureAwait(false);

                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                    using Process process = Process.GetCurrentProcess();
                    _ = EmptyWorkingSet(process.Handle);
                }
                catch
                {
                    // Best-effort only.
                }
                finally
                {
                    stopwatch.Stop();
                    PerformanceTraceHelper.LogElapsed(
                        $"memory trim ({reason})",
                        stopwatch.Elapsed,
                        TimeSpan.FromMilliseconds(120));
                    Interlocked.Exchange(ref _trimScheduled, 0);
                }
            });
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);
    }
}

using ClashWinUI.Common;
using ClashWinUI.Helpers;
using ClashWinUI.Models;
using Microsoft.UI.Xaml;
using System;
using System.Threading;

namespace ClashWinUI
{
    public static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            AppProcessBootstrapResult bootstrapResult = AppProcessBootstrapper.TryInitialize();
            if (bootstrapResult.ShouldExit)
            {
                return;
            }

            Application.Start(_ =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
    }
}

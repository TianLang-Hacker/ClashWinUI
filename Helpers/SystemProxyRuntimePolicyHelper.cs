using ClashWinUI.Common;
using ClashWinUI.Services.Interfaces;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Helpers
{
    public static class SystemProxyRuntimePolicyHelper
    {
        public static async Task ApplyForRuntimeAsync(
            ISystemProxyService systemProxyService,
            IProcessService processService,
            ITunService tunService,
            string runtimePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(runtimePath))
            {
                return;
            }

            if (tunService.IsTunEnabled(runtimePath))
            {
                await systemProxyService.DisableAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            int proxyPort = processService.ResolveProxyPort(runtimePath);
            await systemProxyService.EnableAsync("127.0.0.1", proxyPort, AppConstants.SystemProxyBypassList, cancellationToken).ConfigureAwait(false);
        }
    }
}

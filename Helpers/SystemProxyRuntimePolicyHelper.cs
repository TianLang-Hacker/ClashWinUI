using ClashWinUI.Common;
using ClashWinUI.Services.Interfaces;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Helpers
{
    public static class SystemProxyRuntimePolicyHelper
    {
        public static Task ApplyForRuntimeAsync(
            ISystemProxyService systemProxyService,
            IProcessService processService,
            ITunService tunService,
            string runtimePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(runtimePath))
            {
                return Task.CompletedTask;
            }

            if (tunService.IsTunEnabled(runtimePath))
            {
                return systemProxyService.DisableAsync(cancellationToken);
            }

            int proxyPort = processService.ResolveProxyPort(runtimePath);
            return systemProxyService.EnableAsync("127.0.0.1", proxyPort, AppConstants.SystemProxyBypassList, cancellationToken);
        }
    }
}

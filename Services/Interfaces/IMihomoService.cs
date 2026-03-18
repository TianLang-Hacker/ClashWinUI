
using ClashWinUI.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface IMihomoService
    {
        event EventHandler<string>? ConfigApplied;
        Task<bool> ApplyConfigAsync(string configPath, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ProxyNode>> GetProxiesAsync(CancellationToken cancellationToken = default);
        Task<int?> TestProxyDelayAsync(string proxyName, string testUrl, int timeoutMilliseconds = 5000, CancellationToken cancellationToken = default);
    }
}

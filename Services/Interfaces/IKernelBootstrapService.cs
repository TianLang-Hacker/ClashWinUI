using ClashWinUI.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface IKernelBootstrapService
    {
        void Start();
        Task<bool> EnsureKernelReadyAsync(CancellationToken cancellationToken = default);
        Task<bool> EnsureKernelReadyAsync(IProgress<DownloadProgressReport>? progress, CancellationToken cancellationToken = default);
    }
}

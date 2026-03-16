using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface IKernelBootstrapService
    {
        void Start();
        Task<bool> EnsureKernelReadyAsync(CancellationToken cancellationToken = default);
    }
}

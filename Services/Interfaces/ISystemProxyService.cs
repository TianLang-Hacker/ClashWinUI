using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface ISystemProxyService
    {
        Task EnableAsync(string host, int port, string bypassList, CancellationToken cancellationToken = default);
        Task DisableAsync(CancellationToken cancellationToken = default);
    }
}

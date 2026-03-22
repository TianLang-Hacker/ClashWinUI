using ClashWinUI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface INetworkInfoService
    {
        Task<PublicNetworkInfo?> GetPublicNetworkInfoAsync(CancellationToken cancellationToken = default);
    }
}

using ClashWinUI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface IGeoDataService
    {
        GeoDataOperationResult LastResult { get; }

        Task<GeoDataOperationResult> EnsureGeoDataReadyAsync(CancellationToken cancellationToken = default);

        Task<GeoDataOperationResult> UpdateGeoDataAsync(CancellationToken cancellationToken = default);
    }
}

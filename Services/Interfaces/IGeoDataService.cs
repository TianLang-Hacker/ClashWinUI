using ClashWinUI.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface IGeoDataService
    {
        string GeoDataDirectory { get; }

        GeoDataOperationResult LastResult { get; }

        Task<GeoDataOperationResult> EnsureGeoDataReadyAsync(CancellationToken cancellationToken = default);

        Task<GeoDataOperationResult> EnsureGeoDataReadyAsync(IProgress<DownloadProgressReport>? progress, CancellationToken cancellationToken = default);

        Task<GeoDataOperationResult> UpdateGeoDataAsync(CancellationToken cancellationToken = default);

        Task<GeoDataOperationResult> UpdateGeoDataAsync(IProgress<DownloadProgressReport>? progress, CancellationToken cancellationToken = default);
    }
}

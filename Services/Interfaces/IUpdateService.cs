
using ClashWinUI.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface IUpdateService
    {
        event EventHandler? StateChanged;

        UpdateState CurrentState { get; }

        Task CheckForUpdatesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

        Task<bool> DownloadAndInstallLatestAsync(CancellationToken cancellationToken = default);
    }
}

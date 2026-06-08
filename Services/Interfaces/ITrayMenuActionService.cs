using ClashWinUI.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface ITrayMenuActionService
    {
        event EventHandler<TrayMenuSnapshot>? SnapshotChanged;

        TrayMenuSnapshot GetCachedSnapshot();
        Task<TrayMenuSnapshot> RefreshSnapshotAsync(CancellationToken cancellationToken = default);
        Task<bool> ApplyModeAsync(string mode, CancellationToken cancellationToken = default);
        Task<bool> ActivateProfileAsync(string profileId, CancellationToken cancellationToken = default);
        Task<bool> SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken = default);
        Task<bool> SetTunEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
        Task<bool> RestartMihomoCoreAsync(CancellationToken cancellationToken = default);
        void OpenProfilesDirectory();
    }
}

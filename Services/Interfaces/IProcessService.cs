using ClashWinUI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface IProcessService
    {
        bool IsRunning { get; }
        int ControllerPort { get; }
        string ControllerHost { get; }
        MihomoFailureDiagnostic LastFailureDiagnostic { get; }

        string EnsureStartupConfigPath(string? preferredConfigPath = null);
        int ResolveProxyPort(string configPath);
        Task<bool> EnsureStartedAsync(string configPath, CancellationToken cancellationToken = default);
        Task<bool> RestartAsync(string configPath, CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
        void ResetFailureDiagnostic();
    }
}

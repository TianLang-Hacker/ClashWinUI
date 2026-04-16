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
        string? CurrentConfigPath { get; }
        MihomoFailureDiagnostic LastFailureDiagnostic { get; }
        long? GetMihomoMemoryUsageBytes();

        string EnsureStartupConfigPath(string? preferredConfigPath = null);
        int ResolveProxyPort(string configPath);
        Task<bool> EnsureStartedAsync(string configPath, CancellationToken cancellationToken = default);
        Task<bool> RestartAsync(string configPath, CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
        void ResetFailureDiagnostic();
        void UpdateFailureDiagnostic(MihomoFailureKind kind, string message);
    }
}
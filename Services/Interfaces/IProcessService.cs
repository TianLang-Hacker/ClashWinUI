using ClashWinUI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Interfaces
{
    public interface IProcessService
    {
        event System.EventHandler? RuntimeStateChanged;

        bool IsRunning { get; }
        int ControllerPort { get; }
        string ControllerHost { get; }
        string? CurrentConfigPath { get; }
        MihomoFailureDiagnostic LastFailureDiagnostic { get; }
        long? GetMihomoMemoryUsageBytes();

        RuntimeStateSnapshot? GetPersistedRuntimeState();
        string EnsureStartupConfigPath(string? preferredConfigPath = null);
        int ResolveProxyPort(string configPath);
        bool TryAttachToPersistedRuntime();
        Task<bool> EnsureStartedAsync(string configPath, CancellationToken cancellationToken = default);
        Task<bool> RestartAsync(string configPath, CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
        void ResetFailureDiagnostic();
        void UpdateFailureDiagnostic(MihomoFailureKind kind, string message);
    }
}

using ClashWinUI.Models;

namespace ClashWinUI.Services.Interfaces
{
    public interface ITunService
    {
        bool IsTunEnabled(string configPath);
        TunRuntimeStatus GetRuntimeStatus(string configPath, string kernelExecutablePath);
        TunPreparationResult ValidateEnvironment(string kernelExecutablePath);
        TunPreparationResult EnsureWintunReady(string kernelExecutablePath);
    }
}

using ClashWinUI.Services.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Implementations
{
    public class KernelBootstrapService : IKernelBootstrapService
    {
        private const string DefaultFallbackTag = "v1.19.21";

        private readonly IAppLogService _logService;
        private readonly IKernelPathService _kernelPathService;
        private readonly SemaphoreSlim _sync = new(1, 1);

        public KernelBootstrapService(IAppLogService logService, IKernelPathService kernelPathService)
        {
            _logService = logService;
            _kernelPathService = kernelPathService;
        }

        public void Start()
        {
            _ = EnsureKernelReadyAsync();
        }

        public async Task<bool> EnsureKernelReadyAsync(CancellationToken cancellationToken = default)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string scriptPath = Path.Combine(AppContext.BaseDirectory, "Build", "DownloadKernel.ps1");
                string? configuredCustomPath = _kernelPathService.CustomKernelPath;
                string kernelExecutablePath = _kernelPathService.ResolveKernelPath();
                string kernelDir = Path.GetDirectoryName(kernelExecutablePath)
                    ?? Path.GetDirectoryName(_kernelPathService.DefaultKernelPath)
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                _logService.Add($"Resolved kernel path: {kernelExecutablePath}");
                if (!string.IsNullOrWhiteSpace(configuredCustomPath))
                {
                    _logService.Add($"Configured custom kernel path: {configuredCustomPath}");
                    if (!File.Exists(configuredCustomPath))
                    {
                        _logService.Add($"Custom kernel path not found. Fallback to default path: {_kernelPathService.DefaultKernelPath}");
                    }
                }

                if (File.Exists(kernelExecutablePath))
                {
                    _logService.Add($"Kernel exists: {kernelExecutablePath}. Skip download.");
                    return true;
                }

                if (!File.Exists(scriptPath))
                {
                    _logService.Add($"Kernel script missing: {scriptPath}", Models.LogLevel.Error);
                    return false;
                }

                _logService.Add("Kernel not found. Start download script...");
                bool success = await RunDownloadScriptAsync(scriptPath, kernelDir, cancellationToken).ConfigureAwait(false);

                if (success && File.Exists(kernelExecutablePath))
                {
                    _logService.Add("Kernel script completed successfully.");
                    return true;
                }

                _logService.Add($"Kernel not ready after script run: {kernelExecutablePath}", Models.LogLevel.Error);
                return false;
            }
            finally
            {
                _sync.Release();
            }
        }

        private async Task<bool> RunDownloadScriptAsync(string scriptPath, string kernelDir, CancellationToken cancellationToken)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -KernelDir \"{kernelDir}\" -FallbackTag \"{DefaultFallbackTag}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = AppContext.BaseDirectory,
                    },
                    EnableRaisingEvents = true,
                };

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logService.Add(e.Data);
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logService.Add($"ERROR: {e.Data}", Models.LogLevel.Error);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode == 0)
                {
                    return true;
                }

                _logService.Add($"Kernel script failed, exit code: {process.ExitCode}", Models.LogLevel.Error);
                return false;
            }
            catch (OperationCanceledException)
            {
                _logService.Add("Kernel download canceled.", Models.LogLevel.Warning);
                return false;
            }
            catch (Exception ex)
            {
                _logService.Add($"Kernel script launch failed: {ex.Message}", Models.LogLevel.Error);
                return false;
            }
        }
    }
}

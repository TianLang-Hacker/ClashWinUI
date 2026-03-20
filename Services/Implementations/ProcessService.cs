using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using ClashWinUI.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Implementations
{
    public class ProcessService : IProcessService
    {
        private const string DefaultStartupFileName = "default-startup.yaml";
        private const int DefaultProxyPort = 7890;

        private static readonly Regex MixedPortRegex = new(@"^\uFEFF?mixed-port\s*:\s*(?<value>\d+)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PortRegex = new(@"^\uFEFF?port\s*:\s*(?<value>\d+)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IKernelPathService _kernelPathService;
        private readonly IAppLogService _logService;
        private readonly HttpClient _controllerProbeClient = new();
        private readonly SemaphoreSlim _sync = new(1, 1);

        private Process? _mihomoProcess;
        private MihomoFailureDiagnostic _lastFailureDiagnostic = MihomoFailureDiagnostic.None;

        public ProcessService(IKernelPathService kernelPathService, IAppLogService logService)
        {
            _kernelPathService = kernelPathService;
            _logService = logService;
        }

        public bool IsRunning => _mihomoProcess is { HasExited: false };

        public int ControllerPort => 9090;

        public string ControllerHost => "127.0.0.1";

        public MihomoFailureDiagnostic LastFailureDiagnostic => _lastFailureDiagnostic;

        public string EnsureStartupConfigPath(string? preferredConfigPath = null)
        {
            if (!string.IsNullOrWhiteSpace(preferredConfigPath))
            {
                string candidate = Path.GetFullPath(preferredConfigPath.Trim().Trim('"'));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string defaultConfigPath = Path.Combine(userProfile, "ClashWinUI", "Profiles", DefaultStartupFileName);
            string? directory = Path.GetDirectoryName(defaultConfigPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(defaultConfigPath))
            {
                File.WriteAllText(defaultConfigPath, BuildDefaultStartupConfig(), Encoding.UTF8);
                _logService.Add($"Generated default startup profile: {defaultConfigPath}");
            }

            return defaultConfigPath;
        }

        public int ResolveProxyPort(string configPath)
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                return DefaultProxyPort;
            }

            int? mixedPort = null;
            int? legacyPort = null;

            foreach (string line in File.ReadLines(configPath))
            {
                Match mixedPortMatch = MixedPortRegex.Match(line);
                if (mixedPortMatch.Success && int.TryParse(mixedPortMatch.Groups["value"].Value, out int parsedMixedPort))
                {
                    mixedPort = parsedMixedPort;
                    break;
                }

                Match portMatch = PortRegex.Match(line);
                if (portMatch.Success && int.TryParse(portMatch.Groups["value"].Value, out int parsedPort))
                {
                    legacyPort = parsedPort;
                }
            }

            return mixedPort ?? legacyPort ?? DefaultProxyPort;
        }

        public async Task<bool> EnsureStartedAsync(string configPath, CancellationToken cancellationToken = default)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string effectiveConfigPath = EnsureStartupConfigPath(configPath);
                NormalizeConfigFileIfNeeded(effectiveConfigPath);

                string kernelPath = _kernelPathService.ResolveKernelPath();
                if (!File.Exists(kernelPath))
                {
                    _logService.Add($"Cannot start Mihomo, kernel missing: {kernelPath}", LogLevel.Error);
                    return false;
                }

                if (IsRunning)
                {
                    ResetFailureDiagnostic();
                    _logService.Add("Mihomo process already running. Reuse existing instance.");
                    return true;
                }

                List<Process> existingOwnedProcesses = FindOwnedMihomoProcesses(kernelPath);
                if (existingOwnedProcesses.Count > 0)
                {
                    bool controllerReachable = await IsControllerReachableAsync(cancellationToken).ConfigureAwait(false);
                    if (controllerReachable)
                    {
                        Process keepProcess = SelectProcessToKeep(existingOwnedProcesses);
                        _mihomoProcess = keepProcess;
                        ResetFailureDiagnostic();
                        _logService.Add($"Detected existing Mihomo process (PID={keepProcess.Id}). Reuse existing instance.");

                        if (existingOwnedProcesses.Count > 1)
                        {
                            foreach (Process duplicate in existingOwnedProcesses)
                            {
                                if (duplicate.Id == keepProcess.Id)
                                {
                                    continue;
                                }

                                TryTerminateProcess(duplicate, "duplicate Mihomo process");
                            }
                        }

                        return true;
                    }

                    _logService.Add("Detected stale Mihomo process without reachable controller. Terminating stale process(es).", LogLevel.Warning);
                    foreach (Process staleProcess in existingOwnedProcesses)
                    {
                        TryTerminateProcess(staleProcess, "stale Mihomo process");
                    }
                }

                string controller = $"{ControllerHost}:{ControllerPort}";
                string arguments = $"-f \"{effectiveConfigPath}\" -ext-ctl {controller}";
                string workingDirectory = Path.GetDirectoryName(kernelPath) ?? AppContext.BaseDirectory;
                int bindConflictDetected = 0;
                ResetFailureDiagnostic();

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = kernelPath,
                        Arguments = arguments,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                    },
                    EnableRaisingEvents = true,
                };

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logService.Add($"[MIHOMO] {e.Data}");
                        if (LooksLikePortBindConflict(e.Data))
                        {
                            Interlocked.Exchange(ref bindConflictDetected, 1);
                            UpdateFailureDiagnostic(MihomoFailureKind.PortBindConflict, e.Data);
                        }
                        else if (LooksLikeGeoDataIssue(e.Data))
                        {
                            UpdateFailureDiagnostic(MihomoFailureKind.GeoData, e.Data);
                        }
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logService.Add($"[MIHOMO] {e.Data}", LogLevel.Warning);
                        if (LooksLikePortBindConflict(e.Data))
                        {
                            Interlocked.Exchange(ref bindConflictDetected, 1);
                            UpdateFailureDiagnostic(MihomoFailureKind.PortBindConflict, e.Data);
                        }
                        else if (LooksLikeGeoDataIssue(e.Data))
                        {
                            UpdateFailureDiagnostic(MihomoFailureKind.GeoData, e.Data);
                        }
                    }
                };

                process.Exited += (_, _) =>
                {
                    _logService.Add("Mihomo process exited.", LogLevel.Warning);
                };

                bool started;
                try
                {
                    started = process.Start();
                }
                catch (Exception ex)
                {
                    process.Dispose();
                    _logService.Add($"Mihomo start failed: {ex.Message}", LogLevel.Error);
                    return false;
                }

                if (!started)
                {
                    process.Dispose();
                    _logService.Add("Mihomo start failed: process.Start returned false.", LogLevel.Error);
                    return false;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    if (process.HasExited)
                    {
                        int exitCode = process.ExitCode;
                        process.Dispose();
                        _logService.Add($"Mihomo exited immediately after start. ExitCode={exitCode}", LogLevel.Error);
                        return false;
                    }

                    if (Interlocked.CompareExchange(ref bindConflictDetected, 0, 0) == 1)
                    {
                        TryTerminateProcess(process, "port bind conflict during startup");
                        _logService.Add("Mihomo startup failed due to port bind conflict.", LogLevel.Error);
                        return false;
                    }

                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }

                if (Interlocked.CompareExchange(ref bindConflictDetected, 0, 0) == 1)
                {
                    TryTerminateProcess(process, "port bind conflict during startup");
                    _logService.Add("Mihomo startup failed due to port bind conflict.", LogLevel.Error);
                    return false;
                }

                _mihomoProcess = process;
                _logService.Add($"Mihomo started with config: {effectiveConfigPath}");

                return true;
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task<bool> RestartAsync(string configPath, CancellationToken cancellationToken = default)
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);
            return await EnsureStartedAsync(configPath, cancellationToken).ConfigureAwait(false);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string kernelPath = _kernelPathService.ResolveKernelPath();
                Process? trackedProcess = _mihomoProcess;
                _mihomoProcess = null;

                if (trackedProcess is not null)
                {
                    try
                    {
                        if (!trackedProcess.HasExited)
                        {
                            trackedProcess.Kill(entireProcessTree: true);
                            await trackedProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        }

                        _logService.Add("Mihomo process stopped.");
                    }
                    catch (OperationCanceledException)
                    {
                        _logService.Add("Stop Mihomo canceled.", LogLevel.Warning);
                    }
                    catch (Exception ex)
                    {
                        _logService.Add($"Stop Mihomo failed: {ex.Message}", LogLevel.Warning);
                    }
                    finally
                    {
                        trackedProcess.Dispose();
                    }
                }

                List<Process> orphanedOwnedProcesses = FindOwnedMihomoProcesses(kernelPath);
                foreach (Process orphan in orphanedOwnedProcesses)
                {
                    if (trackedProcess is not null && orphan.Id == trackedProcess.Id)
                    {
                        continue;
                    }

                    TryTerminateProcess(orphan, "orphan Mihomo process during shutdown");
                }
            }
            finally
            {
                _sync.Release();
            }
        }

        public void ResetFailureDiagnostic()
        {
            _lastFailureDiagnostic = MihomoFailureDiagnostic.None;
        }

        private List<Process> FindOwnedMihomoProcesses(string kernelPath)
        {
            var processes = new List<Process>();
            string normalizedKernelPath = NormalizeExecutablePath(kernelPath);

            foreach (Process process in Process.GetProcessesByName("mihomo"))
            {
                try
                {
                    if (process.HasExited)
                    {
                        process.Dispose();
                        continue;
                    }

                    string? processPath = TryGetProcessPath(process);
                    if (string.IsNullOrWhiteSpace(processPath))
                    {
                        process.Dispose();
                        continue;
                    }

                    if (string.Equals(NormalizeExecutablePath(processPath), normalizedKernelPath, StringComparison.OrdinalIgnoreCase))
                    {
                        processes.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch
                {
                    process.Dispose();
                }
            }

            return processes;
        }

        private static string NormalizeExecutablePath(string path)
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }

        private static string? TryGetProcessPath(Process process)
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        private static Process SelectProcessToKeep(List<Process> processes)
        {
            Process selected = processes[0];
            DateTime selectedStartTime = GetSafeStartTime(selected);

            for (int i = 1; i < processes.Count; i++)
            {
                Process current = processes[i];
                DateTime currentStartTime = GetSafeStartTime(current);
                if (currentStartTime < selectedStartTime)
                {
                    selected = current;
                    selectedStartTime = currentStartTime;
                }
            }

            return selected;
        }

        private static DateTime GetSafeStartTime(Process process)
        {
            try
            {
                return process.StartTime;
            }
            catch
            {
                return DateTime.MaxValue;
            }
        }

        private void TryTerminateProcess(Process process, string reason)
        {
            int pid = -1;
            try
            {
                pid = process.Id;
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }

                _logService.Add($"Terminated {reason}. PID={pid}", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                _logService.Add($"Failed to terminate {reason} (PID={pid}): {ex.Message}", LogLevel.Warning);
            }
            finally
            {
                process.Dispose();
            }
        }

        private async Task<bool> IsControllerReachableAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(1));
                using HttpResponseMessage response = await _controllerProbeClient.GetAsync(
                    $"http://{ControllerHost}:{ControllerPort}/version",
                    cts.Token).ConfigureAwait(false);

                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static bool LooksLikePortBindConflict(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            return line.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase)
                || (line.Contains("listen tcp", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("bind:", StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeGeoDataIssue(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            return line.Contains("MMDB invalid", StringComparison.OrdinalIgnoreCase)
                || line.Contains("can't initial GeoIP", StringComparison.OrdinalIgnoreCase)
                || line.Contains("can't download MMDB", StringComparison.OrdinalIgnoreCase)
                || (line.Contains("Parse config error", StringComparison.OrdinalIgnoreCase)
                    && (line.Contains("GEOIP", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("GEOSITE", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("MMDB", StringComparison.OrdinalIgnoreCase)));
        }

        private void UpdateFailureDiagnostic(MihomoFailureKind kind, string message)
        {
            _lastFailureDiagnostic = new MihomoFailureDiagnostic
            {
                Kind = kind,
                Message = message,
                OccurredAt = DateTimeOffset.UtcNow,
            };
        }

        private static string BuildDefaultStartupConfig()
        {
            return """
mixed-port: 7890
allow-lan: false
mode: Rule
log-level: info
external-controller: 127.0.0.1:9090

proxies: []

proxy-groups:
  - name: Default
    type: select
    proxies:
      - DIRECT

rules:
  - MATCH,Default
""";
        }

        private void NormalizeConfigFileIfNeeded(string configPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                {
                    return;
                }

                byte[] rawContent = File.ReadAllBytes(configPath);
                SubscriptionContentNormalizationResult normalization = SubscriptionContentNormalizer.Normalize(rawContent);
                if (normalization.Status == SubscriptionContentNormalizationStatus.DecodedFromBase64)
                {
                    File.WriteAllBytes(configPath, normalization.Content);
                    _logService.Add($"Config normalized from Base64 to YAML before startup: {configPath}", LogLevel.Warning);
                }
                else if (normalization.Status == SubscriptionContentNormalizationStatus.Base64DecodedButNotYaml)
                {
                    if (ShareLinkSubscriptionConverter.TryConvertToMihomoYaml(normalization.Content, out byte[] convertedYaml, out int convertedCount))
                    {
                        File.WriteAllBytes(configPath, convertedYaml);
                        _logService.Add($"Config converted from share links to Mihomo YAML before startup. Nodes={convertedCount}: {configPath}", LogLevel.Warning);
                    }
                    else
                    {
                        _logService.Add(
                            $"Config is Base64 but decoded content is not Mihomo YAML. Skip using as startup config: {configPath}",
                            LogLevel.Warning);
                    }
                }
                else if (normalization.Status == SubscriptionContentNormalizationStatus.Base64DecodeFailed)
                {
                    _logService.Add($"Config appears to be Base64 but decode failed: {configPath}", LogLevel.Warning);
                }
                else if (normalization.Status == SubscriptionContentNormalizationStatus.Unrecognized)
                {
                    if (ShareLinkSubscriptionConverter.TryConvertToMihomoYaml(normalization.Content, out byte[] convertedYaml, out int convertedCount))
                    {
                        File.WriteAllBytes(configPath, convertedYaml);
                        _logService.Add($"Config converted from share links to Mihomo YAML before startup. Nodes={convertedCount}: {configPath}", LogLevel.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Add($"Config normalization before startup failed: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}

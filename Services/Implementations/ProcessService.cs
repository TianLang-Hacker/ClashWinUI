using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using ClashWinUI.Helpers;
using ClashWinUI.Common;
using ClashWinUI.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
namespace ClashWinUI.Services.Implementations
{
    public class ProcessService : IProcessService, IDisposable
    {
        private const string DefaultStartupFileName = "default-startup.yaml";
        private const int DefaultProxyPort = 7890;

        private static readonly Regex MixedPortRegex = new(@"^\uFEFF?mixed-port\s*:\s*(?<value>\d+)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PortRegex = new(@"^\uFEFF?port\s*:\s*(?<value>\d+)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IKernelPathService _kernelPathService;
        private readonly IKernelBootstrapService _kernelBootstrapService;
        private readonly ITunService _tunService;
        private readonly IAppLogService _logService;
        private readonly IGeoDataService _geoDataService;
        private readonly HttpClient _controllerProbeClient = new();
        private readonly SemaphoreSlim _sync = new(1, 1);
        private readonly string _runtimeStateFilePath;
        private readonly SynchronizationContext? _synchronizationContext;
        private readonly FileSystemWatcher? _runtimeStateWatcher;

        private Process? _mihomoProcess;
        private string? _currentConfigPath;
        private MihomoFailureDiagnostic _lastFailureDiagnostic = MihomoFailureDiagnostic.None;
        private MihomoFailureDiagnostic _lastTunKernelFailureDiagnostic = MihomoFailureDiagnostic.None;
        private int _runtimeStateReloadQueued;

        public event EventHandler? RuntimeStateChanged;

        public ProcessService(
            IKernelPathService kernelPathService,
            IKernelBootstrapService kernelBootstrapService,
            ITunService tunService,
            IAppLogService logService,
            IGeoDataService geoDataService)
        {
            _kernelPathService = kernelPathService;
            _kernelBootstrapService = kernelBootstrapService;
            _tunService = tunService;
            _logService = logService;
            _geoDataService = geoDataService;
            _synchronizationContext = SynchronizationContext.Current;
            _runtimeStateFilePath = AppDataPaths.RuntimeStateFilePath;
            AppDataPaths.EnsureStateDirectory();
            _runtimeStateWatcher = CreateRuntimeStateWatcher();
        }

        public bool IsRunning => _mihomoProcess is { HasExited: false };

        public int ControllerPort => 9090;

        public string ControllerHost => "127.0.0.1";

        public string? CurrentConfigPath => _currentConfigPath;

        public MihomoFailureDiagnostic LastFailureDiagnostic => _lastFailureDiagnostic;

        public RuntimeStateSnapshot? GetPersistedRuntimeState()
        {
            try
            {
                if (!File.Exists(_runtimeStateFilePath))
                {
                    return null;
                }

                string payload = File.ReadAllText(_runtimeStateFilePath);
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return null;
                }

                return JsonSerializer.Deserialize(payload, ClashJsonContext.Default.RuntimeStateSnapshot);
            }
            catch
            {
                return null;
            }
        }

        public bool TryAttachToPersistedRuntime()
        {
            RuntimeStateSnapshot? snapshot = GetPersistedRuntimeState();
            if (!ApplyRuntimeSnapshot(snapshot, raiseEvent: false))
            {
                return false;
            }

            bool reachable = IsControllerReachableAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (!reachable)
            {
                ClearAttachedProcess(disposeCurrent: true);
                _currentConfigPath = null;
            }

            return reachable;
        }

        public long? GetMihomoMemoryUsageBytes()
        {
            try
            {
                if (_mihomoProcess is { HasExited: false })
                {
                    return _mihomoProcess.WorkingSet64;
                }

                string kernelPath = _kernelPathService.ResolveKernelPath();
                List<Process> ownedProcesses = FindOwnedMihomoProcesses(kernelPath);
                if (ownedProcesses.Count == 0)
                {
                    return null;
                }

                try
                {
                    Process selected = SelectProcessToKeep(ownedProcesses);
                    return selected.HasExited ? null : selected.WorkingSet64;
                }
                finally
                {
                    foreach (Process process in ownedProcesses)
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

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
                    bool kernelReady = await _kernelBootstrapService.EnsureKernelReadyAsync(cancellationToken).ConfigureAwait(false);
                    kernelPath = _kernelPathService.ResolveKernelPath();
                    if (!kernelReady || !File.Exists(kernelPath))
                    {
                        _currentConfigPath = null;
                        _logService.Add($"Cannot start Mihomo, kernel missing: {kernelPath}", LogLevel.Error);
                        return false;
                    }
                }

                if (_tunService.IsTunEnabled(effectiveConfigPath))
                {
                    TunPreparationResult tunPreparation = _tunService.ValidateEnvironment(kernelPath);
                    if (!tunPreparation.Success)
                    {
                        _currentConfigPath = null;
                        UpdateFailureDiagnostic(tunPreparation.FailureKind, tunPreparation.Message);
                        _logService.Add($"TUN preparation failed before Mihomo startup: {tunPreparation.Message}", LogLevel.Error);
                        return false;
                    }
                }

                if (IsRunning)
                {
                    _currentConfigPath = effectiveConfigPath;
                    ResetFailureDiagnostic();
                    PersistRuntimeState();
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
                        AttachProcess(keepProcess, effectiveConfigPath, persistState: true, raiseEvent: true);
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

                using IDisposable? startGate = MihomoPortConflictResolver.TryEnterStartGate(TimeSpan.FromSeconds(8));
                if (startGate is null)
                {
                    _logService.Add("Timed out waiting for exclusive Mihomo start gate. Continuing cautiously.", LogLevel.Warning);
                }

                int freedBeforeStart = MihomoPortConflictResolver.FreeConflictingPorts(
                    effectiveConfigPath,
                    kernelPath,
                    ControllerPort,
                    DefaultProxyPort,
                    _logService);
                if (freedBeforeStart > 0)
                {
                    _logService.Add($"Freed {freedBeforeStart} conflicting process(es) before Mihomo startup.", LogLevel.Warning);
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }

                string controller = $"{ControllerHost}:{ControllerPort}";
                string geoDataDirectory = _geoDataService.GeoDataDirectory;
                Directory.CreateDirectory(geoDataDirectory);
                string arguments = $"-d \"{geoDataDirectory}\" -f \"{effectiveConfigPath}\" -ext-ctl {controller}";
                string workingDirectory = Path.GetDirectoryName(kernelPath) ?? AppContext.BaseDirectory;

                const int maxStartAttempts = 2;
                for (int attempt = 1; attempt <= maxStartAttempts; attempt++)
                {
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
                            if (TryClassifyMihomoDiagnostic(e.Data, out MihomoFailureKind diagnosticKind))
                            {
                                if (diagnosticKind == MihomoFailureKind.PortBindConflict)
                                {
                                    Interlocked.Exchange(ref bindConflictDetected, 1);
                                }

                                TrackTunKernelFailureDiagnostic(diagnosticKind, e.Data);
                                UpdateFailureDiagnostic(diagnosticKind, e.Data);
                            }
                        }
                    };

                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            _logService.Add($"[MIHOMO] {e.Data}", LogLevel.Warning);
                            if (TryClassifyMihomoDiagnostic(e.Data, out MihomoFailureKind diagnosticKind))
                            {
                                if (diagnosticKind == MihomoFailureKind.PortBindConflict)
                                {
                                    Interlocked.Exchange(ref bindConflictDetected, 1);
                                }

                                TrackTunKernelFailureDiagnostic(diagnosticKind, e.Data);
                                UpdateFailureDiagnostic(diagnosticKind, e.Data);
                            }
                        }
                    };

                    process.Exited += (_, _) =>
                    {
                        Process? previousProcess = _mihomoProcess;
                        if (previousProcess is not null && previousProcess.Id != process.Id)
                        {
                            return;
                        }

                        ClearAttachedProcess(disposeCurrent: false);
                        _currentConfigPath = null;
                        PersistRuntimeState();
                        RaiseRuntimeStateChanged();
                        _logService.Add("Mihomo process exited.", LogLevel.Warning);
                    };

                    bool started;
                    try
                    {
                        started = process.Start();
                    }
                    catch (Exception ex)
                    {
                        _currentConfigPath = null;
                        process.Dispose();
                        _logService.Add($"Mihomo start failed: {ex.Message}", LogLevel.Error);
                        return false;
                    }

                    if (!started)
                    {
                        _currentConfigPath = null;
                        process.Dispose();
                        _logService.Add("Mihomo start failed: process.Start returned false.", LogLevel.Error);
                        return false;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
                    bool exitedImmediately = false;
                    int exitCode = 0;
                    while (DateTimeOffset.UtcNow < deadline)
                    {
                        if (process.HasExited)
                        {
                            exitCode = process.ExitCode;
                            exitedImmediately = true;
                            break;
                        }

                        if (Interlocked.CompareExchange(ref bindConflictDetected, 0, 0) == 1)
                        {
                            break;
                        }

                        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                    }

                    bool hasBindConflict = Interlocked.CompareExchange(ref bindConflictDetected, 0, 0) == 1;
                    if (exitedImmediately && !hasBindConflict)
                    {
                        _currentConfigPath = null;
                        process.Dispose();
                        _logService.Add($"Mihomo exited immediately after start. ExitCode={exitCode}", LogLevel.Error);
                        return false;
                    }

                    if (hasBindConflict)
                    {
                        _currentConfigPath = null;
                        TryTerminateProcess(process, "port bind conflict during startup");
                        if (attempt < maxStartAttempts)
                        {
                            int freedOnRetry = MihomoPortConflictResolver.FreeConflictingPorts(
                                effectiveConfigPath,
                                kernelPath,
                                ControllerPort,
                                DefaultProxyPort,
                                _logService);
                            _logService.Add(
                                $"Mihomo port bind conflict on attempt {attempt}/{maxStartAttempts}. Freed={freedOnRetry}. Retrying startup.",
                                LogLevel.Warning);
                            if (freedOnRetry > 0)
                            {
                                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                            }

                            continue;
                        }

                        _logService.Add("Mihomo startup failed due to port bind conflict.", LogLevel.Error);
                        return false;
                    }

                    AttachProcess(process, effectiveConfigPath, persistState: true, raiseEvent: true);
                    _logService.Add($"Mihomo started with config: {effectiveConfigPath}, GeoDataDir={geoDataDirectory}");
                    return true;
                }

                _currentConfigPath = null;
                _logService.Add("Mihomo startup failed after retries.", LogLevel.Error);
                return false;
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
                _currentConfigPath = null;

                if (trackedProcess is not null)
                {
                    try
                    {
                        if (!trackedProcess.HasExited)
                        {
                            trackedProcess.Kill(entireProcessTree: true);
                            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            waitCts.CancelAfter(TimeSpan.FromSeconds(5));
                            try
                            {
                                await trackedProcess.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                _logService.Add("Stop Mihomo timed out after kill; continuing restart.", LogLevel.Warning);
                            }
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

                PersistRuntimeState();
                RaiseRuntimeStateChanged();
            }
            finally
            {
                _sync.Release();
            }
        }

        public void ResetFailureDiagnostic()
        {
            _lastFailureDiagnostic = MihomoFailureDiagnostic.None;
            _lastTunKernelFailureDiagnostic = MihomoFailureDiagnostic.None;
        }

        public void Dispose()
        {
            _runtimeStateWatcher?.Dispose();
            _sync.Dispose();
            _controllerProbeClient.Dispose();
        }

        internal bool TryGetRecentTunFailureDiagnostic(TimeSpan freshnessWindow, out MihomoFailureDiagnostic diagnostic)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - freshnessWindow;

            if (MihomoFailureKindHelper.IsTunFailure(_lastTunKernelFailureDiagnostic.Kind)
                && _lastTunKernelFailureDiagnostic.OccurredAt >= cutoff
                && !string.IsNullOrWhiteSpace(_lastTunKernelFailureDiagnostic.Message))
            {
                diagnostic = _lastTunKernelFailureDiagnostic;
                return true;
            }

            if (MihomoFailureKindHelper.IsTunFailure(_lastFailureDiagnostic.Kind)
                && _lastFailureDiagnostic.OccurredAt >= cutoff
                && !string.IsNullOrWhiteSpace(_lastFailureDiagnostic.Message))
            {
                diagnostic = _lastFailureDiagnostic;
                return true;
            }

            diagnostic = MihomoFailureDiagnostic.None;
            return false;
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

        private FileSystemWatcher? CreateRuntimeStateWatcher()
        {
            try
            {
                string? directory = Path.GetDirectoryName(_runtimeStateFilePath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return null;
                }

                Directory.CreateDirectory(directory);
                var watcher = new FileSystemWatcher(directory, Path.GetFileName(_runtimeStateFilePath))
                {
                    NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += OnRuntimeStateFileChanged;
                watcher.Created += OnRuntimeStateFileChanged;
                watcher.Deleted += OnRuntimeStateFileChanged;
                watcher.Renamed += OnRuntimeStateFileChanged;
                return watcher;
            }
            catch
            {
                return null;
            }
        }

        private void OnRuntimeStateFileChanged(object sender, FileSystemEventArgs e)
        {
            if (Interlocked.Exchange(ref _runtimeStateReloadQueued, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150).ConfigureAwait(false);
                    ApplyRuntimeSnapshot(GetPersistedRuntimeState(), raiseEvent: true);
                }
                finally
                {
                    Interlocked.Exchange(ref _runtimeStateReloadQueued, 0);
                }
            });
        }

        private bool ApplyRuntimeSnapshot(RuntimeStateSnapshot? snapshot, bool raiseEvent)
        {
            bool changed = false;

            if (snapshot is null || !snapshot.IsRunning || snapshot.ProcessId <= 0)
            {
                changed = ClearAttachedProcess(disposeCurrent: true) || !string.IsNullOrWhiteSpace(_currentConfigPath);
                _currentConfigPath = null;
            }
            else
            {
                Process? nextProcess = TryGetProcessById(snapshot.ProcessId);
                if (nextProcess is null)
                {
                    changed = ClearAttachedProcess(disposeCurrent: true) || !string.IsNullOrWhiteSpace(_currentConfigPath);
                    _currentConfigPath = null;
                }
                else
                {
                    string expectedKernelPath = _kernelPathService.ResolveKernelPath();
                    string? processPath = TryGetProcessPath(nextProcess);
                    if (string.IsNullOrWhiteSpace(processPath)
                        || !string.Equals(
                            NormalizeExecutablePath(processPath),
                            NormalizeExecutablePath(expectedKernelPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        nextProcess.Dispose();
                        changed = ClearAttachedProcess(disposeCurrent: true) || !string.IsNullOrWhiteSpace(_currentConfigPath);
                        _currentConfigPath = null;
                    }
                    else
                    {
                        changed = AttachProcess(nextProcess, snapshot.CurrentConfigPath, persistState: false, raiseEvent: false) || changed;
                    }
                }
            }

            if (changed && raiseEvent)
            {
                RaiseRuntimeStateChanged();
            }

            return changed;
        }

        private bool AttachProcess(Process process, string? configPath, bool persistState, bool raiseEvent)
        {
            bool changed = false;
            if (_mihomoProcess is null || _mihomoProcess.Id != process.Id)
            {
                Process? previousProcess = _mihomoProcess;
                _mihomoProcess = process;
                if (previousProcess is not null && previousProcess.Id != process.Id)
                {
                    previousProcess.Dispose();
                }

                changed = true;
            }

            string normalizedConfigPath = string.IsNullOrWhiteSpace(configPath)
                ? string.Empty
                : Path.GetFullPath(configPath.Trim().Trim('"'));
            if (!string.Equals(_currentConfigPath ?? string.Empty, normalizedConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                _currentConfigPath = normalizedConfigPath;
                changed = true;
            }

            if (persistState)
            {
                PersistRuntimeState();
            }

            if (changed && raiseEvent)
            {
                RaiseRuntimeStateChanged();
            }

            return changed;
        }

        private bool ClearAttachedProcess(bool disposeCurrent)
        {
            bool changed = _mihomoProcess is not null;
            if (_mihomoProcess is not null && disposeCurrent)
            {
                _mihomoProcess.Dispose();
            }

            _mihomoProcess = null;
            return changed;
        }

        private void PersistRuntimeState()
        {
            try
            {
                AppDataPaths.EnsureStateDirectory();
                RuntimeStateSnapshot snapshot;
                if (_mihomoProcess is { HasExited: false } process)
                {
                    snapshot = new RuntimeStateSnapshot
                    {
                        IsRunning = true,
                        ProcessId = process.Id,
                        ControllerHost = ControllerHost,
                        ControllerPort = ControllerPort,
                        CurrentConfigPath = _currentConfigPath ?? string.Empty,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                }
                else
                {
                    snapshot = new RuntimeStateSnapshot
                    {
                        IsRunning = false,
                        ProcessId = 0,
                        ControllerHost = ControllerHost,
                        ControllerPort = ControllerPort,
                        CurrentConfigPath = string.Empty,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                }

                string payload = JsonSerializer.Serialize(snapshot, ClashJsonContext.Default.RuntimeStateSnapshot);
                File.WriteAllText(_runtimeStateFilePath, payload);
            }
            catch
            {
                // Runtime state persistence is best-effort.
            }
        }

        private void RaiseRuntimeStateChanged()
        {
            if (_synchronizationContext is not null)
            {
                _synchronizationContext.Post(_ => RuntimeStateChanged?.Invoke(this, EventArgs.Empty), null);
                return;
            }

            RuntimeStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private static Process? TryGetProcessById(int processId)
        {
            try
            {
                Process process = Process.GetProcessById(processId);
                return process.HasExited ? null : process;
            }
            catch
            {
                return null;
            }
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
            return MihomoPortConflictResolver.LooksLikePortBindConflict(line);
        }

        private static bool TryClassifyMihomoDiagnostic(string line, out MihomoFailureKind kind)
        {
            kind = MihomoFailureKind.None;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            if (LooksLikePortBindConflict(line))
            {
                kind = MihomoFailureKind.PortBindConflict;
                return true;
            }

            if (LooksLikeTunPermissionIssue(line))
            {
                kind = MihomoFailureKind.TunPermission;
                return true;
            }

            if (LooksLikeTunDependencyIssue(line))
            {
                kind = MihomoFailureKind.TunDependency;
                return true;
            }

            if (LooksLikeTunRouteIssue(line))
            {
                kind = MihomoFailureKind.TunRouteMissing;
                return true;
            }

            if (LooksLikeGeoDataIssue(line))
            {
                kind = MihomoFailureKind.GeoData;
                return true;
            }

            return false;
        }

        private static bool LooksLikeTunPermissionIssue(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || !LooksLikeTunContext(line))
            {
                return false;
            }

            return line.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
                || line.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                || line.Contains("requires elevation", StringComparison.OrdinalIgnoreCase)
                || line.Contains("required privilege is not held", StringComparison.OrdinalIgnoreCase)
                || line.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeTunDependencyIssue(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            if (line.Contains("wintun.dll", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return LooksLikeTunContext(line)
                && (line.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("not exist", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("load", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("initialize", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("driver", StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeTunRouteIssue(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            bool hasRouteOrInterfaceContext =
                LooksLikeTunContext(line)
                || line.Contains("route", StringComparison.OrdinalIgnoreCase)
                || line.Contains("interface", StringComparison.OrdinalIgnoreCase);

            if (!hasRouteOrInterfaceContext)
            {
                return false;
            }

            bool hasFailureSignal =
                line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("unable", StringComparison.OrdinalIgnoreCase)
                || line.Contains("cannot", StringComparison.OrdinalIgnoreCase)
                || line.Contains("can't", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not attached", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || line.Contains("missing", StringComparison.OrdinalIgnoreCase)
                || line.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || line.Contains("file exists", StringComparison.OrdinalIgnoreCase)
                || line.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                || line.Contains("in use", StringComparison.OrdinalIgnoreCase)
                || line.Contains("occupied", StringComparison.OrdinalIgnoreCase);

            if (!hasFailureSignal)
            {
                return false;
            }

            return line.Contains("route", StringComparison.OrdinalIgnoreCase)
                || line.Contains("interface", StringComparison.OrdinalIgnoreCase)
                || line.Contains("adapter", StringComparison.OrdinalIgnoreCase)
                || line.Contains("create adapter", StringComparison.OrdinalIgnoreCase)
                || line.Contains("createadapter", StringComparison.OrdinalIgnoreCase);
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

        private static bool LooksLikeTunContext(string line)
        {
            return line.Contains("tun", StringComparison.OrdinalIgnoreCase)
                || line.Contains("wintun", StringComparison.OrdinalIgnoreCase)
                || line.Contains("adapter", StringComparison.OrdinalIgnoreCase);
        }

        public void UpdateFailureDiagnostic(MihomoFailureKind kind, string message)
        {
            _lastFailureDiagnostic = new MihomoFailureDiagnostic
            {
                Kind = kind,
                Message = message,
                OccurredAt = DateTimeOffset.UtcNow,
            };
        }

        private void TrackTunKernelFailureDiagnostic(MihomoFailureKind kind, string message)
        {
            if (!MihomoFailureKindHelper.IsTunFailure(kind) || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            _lastTunKernelFailureDiagnostic = new MihomoFailureDiagnostic
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

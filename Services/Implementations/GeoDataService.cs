using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Implementations
{
    public sealed class GeoDataService : IGeoDataService
    {
        private static readonly string[] AssetNames =
        [
            "geoip.metadb",
            "geoip.dat",
            "geosite.dat",
        ];
        private static readonly TimeSpan GeoDataProcessTimeout = TimeSpan.FromMinutes(8);

        private readonly IAppLogService _logService;
        private readonly SemaphoreSlim _sync = new(1, 1);

        public GeoDataService(IAppLogService logService)
        {
            _logService = logService;
        }

        public string GeoDataDirectory => GetGeoDataDirectory();

        public GeoDataOperationResult LastResult { get; private set; } = GeoDataOperationResult.None;

        public Task<GeoDataOperationResult> EnsureGeoDataReadyAsync(CancellationToken cancellationToken = default)
        {
            return EnsureGeoDataReadyAsync(progress: null, cancellationToken);
        }

        public async Task<GeoDataOperationResult> EnsureGeoDataReadyAsync(IProgress<DownloadProgressReport>? progress, CancellationToken cancellationToken = default)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IReadOnlyList<GeoDataAssetStatus> existingAssets = InspectAssets();
                if (existingAssets.All(asset => asset.Exists && asset.Length > 0))
                {
                    GeoDataOperationResult readyResult = new()
                    {
                        HasRun = true,
                        Success = true,
                        OperationKind = GeoDataOperationKind.Ensure,
                        FailureKind = GeoDataFailureKind.None,
                        Details = "GeoData already ready.",
                        Assets = existingAssets,
                    };

                    LastResult = readyResult;
                    _logService.Add($"GeoData ready: {BuildAssetsSummary(existingAssets)}", LogLevel.Info);
                    return readyResult;
                }

                _logService.Add($"GeoData missing or empty. Start ensure download: {BuildAssetsSummary(existingAssets)}", LogLevel.Warning);
                return await DownloadGeoDataInternalAsync(forceRefresh: false, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sync.Release();
            }
        }

        public Task<GeoDataOperationResult> UpdateGeoDataAsync(CancellationToken cancellationToken = default)
        {
            return UpdateGeoDataAsync(progress: null, cancellationToken);
        }

        public async Task<GeoDataOperationResult> UpdateGeoDataAsync(IProgress<DownloadProgressReport>? progress, CancellationToken cancellationToken = default)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _logService.Add("Start GeoData force refresh.", LogLevel.Info);
                return await DownloadGeoDataInternalAsync(forceRefresh: true, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sync.Release();
            }
        }

        private async Task<GeoDataOperationResult> DownloadGeoDataInternalAsync(
            bool forceRefresh,
            IProgress<DownloadProgressReport>? progress,
            CancellationToken cancellationToken)
        {
            string scriptPath = Path.Combine(AppContext.BaseDirectory, "Build", "DownloadGeoData.ps1");
            string geoDataDirectory = GeoDataDirectory;

            if (!File.Exists(scriptPath))
            {
                GeoDataOperationResult missingScriptResult = CreateFailureResult(
                    forceRefresh ? GeoDataOperationKind.Update : GeoDataOperationKind.Ensure,
                    GeoDataFailureKind.ScriptMissing,
                    $"GeoData script missing: {scriptPath}");
                LastResult = missingScriptResult;
                _logService.Add(missingScriptResult.Details, LogLevel.Error);
                return missingScriptResult;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = BuildScriptArguments(scriptPath, geoDataDirectory, forceRefresh),
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

                var standardOutputClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var standardErrorClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null)
                    {
                        standardOutputClosed.TrySetResult(true);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        if (DownloadProgressReport.TryParseScriptLine(e.Data, out DownloadProgressReport report))
                        {
                            progress?.Report(report);
                            return;
                        }

                        _logService.Add(e.Data);
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null)
                    {
                        standardErrorClosed.TrySetResult(true);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logService.Add($"ERROR: {e.Data}", LogLevel.Error);
                    }
                };

                if (!process.Start())
                {
                    GeoDataOperationResult startFailure = CreateFailureResult(
                        forceRefresh ? GeoDataOperationKind.Update : GeoDataOperationKind.Ensure,
                        GeoDataFailureKind.ScriptLaunchFailed,
                        "GeoData script failed: process.Start returned false.");
                    LastResult = startFailure;
                    _logService.Add(startFailure.Details, LogLevel.Error);
                    return startFailure;
                }

                _logService.Add(
                    $"GeoData download script started. Mode={(forceRefresh ? "update" : "ensure")}, Timeout={GeoDataProcessTimeout.TotalMinutes:F0}m, Script={scriptPath}",
                    LogLevel.Info);

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(GeoDataProcessTimeout);

                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // Ignore kill failures and surface timeout below.
                    }

                    GeoDataOperationResult timeoutResult = CreateFailureResult(
                        forceRefresh ? GeoDataOperationKind.Update : GeoDataOperationKind.Ensure,
                        GeoDataFailureKind.DownloadFailed,
                        $"GeoData download timed out after {GeoDataProcessTimeout.TotalMinutes:F0} minutes.");
                    LastResult = timeoutResult;
                    _logService.Add(timeoutResult.Details, LogLevel.Error);
                    return timeoutResult;
                }

                try
                {
                    await Task.WhenAll(standardOutputClosed.Task, standardErrorClosed.Task)
                        .WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Do not fail GeoData handling because stream completion notifications arrived late.
                }

                IReadOnlyList<GeoDataAssetStatus> assets = InspectAssets();
                bool filesReady = assets.All(asset => asset.Exists && asset.Length > 0);
                if (process.ExitCode == 0 && filesReady)
                {
                    GeoDataOperationResult successResult = new()
                    {
                        HasRun = true,
                        Success = true,
                        OperationKind = forceRefresh ? GeoDataOperationKind.Update : GeoDataOperationKind.Ensure,
                        FailureKind = GeoDataFailureKind.None,
                        Details = forceRefresh
                            ? "GeoData force refresh completed."
                            : "GeoData prepared successfully.",
                        Assets = assets,
                    };

                    LastResult = successResult;
                    _logService.Add($"{successResult.Details} {BuildAssetsSummary(assets)}", LogLevel.Info);
                    return successResult;
                }

                string failureDetails = process.ExitCode != 0
                    ? $"GeoData download script failed. ExitCode={process.ExitCode}"
                    : $"GeoData verification failed after download: {BuildAssetsSummary(assets)}";

                GeoDataOperationResult failedResult = CreateFailureResult(
                    forceRefresh ? GeoDataOperationKind.Update : GeoDataOperationKind.Ensure,
                    GeoDataFailureKind.DownloadFailed,
                    failureDetails,
                    assets);
                LastResult = failedResult;
                _logService.Add(failedResult.Details, LogLevel.Error);
                return failedResult;
            }
            catch (OperationCanceledException)
            {
                GeoDataOperationResult canceledResult = CreateFailureResult(
                    forceRefresh ? GeoDataOperationKind.Update : GeoDataOperationKind.Ensure,
                    GeoDataFailureKind.Unknown,
                    "GeoData download canceled.");
                LastResult = canceledResult;
                _logService.Add(canceledResult.Details, LogLevel.Warning);
                return canceledResult;
            }
            catch (Exception ex)
            {
                GeoDataOperationResult failedResult = CreateFailureResult(
                    forceRefresh ? GeoDataOperationKind.Update : GeoDataOperationKind.Ensure,
                    GeoDataFailureKind.ScriptLaunchFailed,
                    $"GeoData script launch failed: {ex.Message}");
                LastResult = failedResult;
                _logService.Add(failedResult.Details, LogLevel.Error);
                return failedResult;
            }
        }

        private static string BuildScriptArguments(string scriptPath, string geoDataDirectory, bool forceRefresh)
        {
            string forceRefreshArgument = forceRefresh ? " -ForceRefresh" : string.Empty;
            return $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -GeoDataDir \"{geoDataDirectory}\"{forceRefreshArgument}";
        }

        private static GeoDataOperationResult CreateFailureResult(
            GeoDataOperationKind operationKind,
            GeoDataFailureKind failureKind,
            string details,
            IReadOnlyList<GeoDataAssetStatus>? assets = null)
        {
            return new GeoDataOperationResult
            {
                HasRun = true,
                Success = false,
                OperationKind = operationKind,
                FailureKind = failureKind,
                Details = details,
                Assets = assets ?? Array.Empty<GeoDataAssetStatus>(),
            };
        }

        private IReadOnlyList<GeoDataAssetStatus> InspectAssets()
        {
            string geoDataDirectory = GeoDataDirectory;
            Directory.CreateDirectory(geoDataDirectory);
            TryCopyLegacyGeoDataAssets(geoDataDirectory);

            return AssetNames
                .Select(assetName =>
                {
                    string path = Path.Combine(geoDataDirectory, assetName);
                    if (!File.Exists(path))
                    {
                        return new GeoDataAssetStatus
                        {
                            Name = assetName,
                            Exists = false,
                            Length = 0,
                        };
                    }

                    var fileInfo = new FileInfo(path);
                    return new GeoDataAssetStatus
                    {
                        Name = assetName,
                        Exists = true,
                        Length = fileInfo.Length,
                    };
                })
                .ToList();
        }

        private void TryCopyLegacyGeoDataAssets(string geoDataDirectory)
        {
            string legacyGeoDataDirectory = GetLegacyGeoDataDirectory();
            if (string.Equals(
                Path.GetFullPath(legacyGeoDataDirectory),
                Path.GetFullPath(geoDataDirectory),
                StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(legacyGeoDataDirectory))
            {
                return;
            }

            foreach (string assetName in AssetNames)
            {
                try
                {
                    string sourcePath = Path.Combine(legacyGeoDataDirectory, assetName);
                    string targetPath = Path.Combine(geoDataDirectory, assetName);
                    if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                    {
                        continue;
                    }

                    if (!File.Exists(sourcePath))
                    {
                        continue;
                    }

                    var sourceInfo = new FileInfo(sourcePath);
                    if (sourceInfo.Length <= 0)
                    {
                        continue;
                    }

                    File.Copy(sourcePath, targetPath, overwrite: false);
                    _logService.Add($"Copied legacy GeoData asset to new directory: {assetName} -> {targetPath}", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    _logService.Add($"Copy legacy GeoData asset failed: {assetName}, {ex.Message}", LogLevel.Warning);
                }
            }
        }

        private static string GetGeoDataDirectory()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, "ClashWinUI", "Geodata");
        }

        private static string GetLegacyGeoDataDirectory()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".config", "mihomo");
        }

        private static string BuildAssetsSummary(IReadOnlyList<GeoDataAssetStatus> assets)
        {
            return string.Join(", ", assets.Select(asset => $"{asset.Name}:{(asset.Exists ? asset.Length : 0)}"));
        }
    }
}

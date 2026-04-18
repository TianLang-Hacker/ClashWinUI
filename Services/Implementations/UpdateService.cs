
using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Management.Deployment;

namespace ClashWinUI.Services.Implementations
{
    public sealed class UpdateService : IUpdateService
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/TianLang-Hacker/ClashWinUI/releases/latest";
        private const int ErrorCancelledHResult = unchecked((int)0x800704C7);
        private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

        private readonly IAppLogService _logService;
        private readonly INetworkInfoService _networkInfoService;
        private readonly HttpClient _metadataClient;
        private readonly HttpClient _downloadClient;
        private readonly SemaphoreSlim _sync = new(1, 1);
        private readonly AppPackageIdentityInfo _packageIdentity = AppPackageInfoHelper.Current;

        private GitHubReleaseInfo? _latestRelease;
        private UpdateState _currentState;

        public UpdateService(IAppLogService logService, INetworkInfoService networkInfoService)
        {
            _logService = logService;
            _networkInfoService = networkInfoService;

            _metadataClient = CreateHttpClient(MetadataTimeout);
            _downloadClient = CreateHttpClient(DownloadTimeout);

            _currentState = new UpdateState
            {
                Status = UpdateStatus.Unknown,
                CurrentVersion = _packageIdentity.Version.ToString(4),
            };
        }

        public event EventHandler? StateChanged;

        public UpdateState CurrentState => _currentState;

        public async Task CheckForUpdatesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SetState(new UpdateState
                {
                    Status = UpdateStatus.Checking,
                    CurrentVersion = _packageIdentity.Version.ToString(4),
                    LatestVersion = _currentState.LatestVersion,
                    ReleasePageUrl = _currentState.ReleasePageUrl,
                    SelectedAssetName = _currentState.SelectedAssetName,
                    DownloadProgressPercentage = 0,
                    IsDownloadProgressIndeterminate = false,
                    IsBusy = true,
                });

                GitHubReleaseInfo? release = await FetchLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
                if (release is null || release.Version is null)
                {
                    _latestRelease = null;
                    SetState(new UpdateState
                    {
                        Status = UpdateStatus.Unavailable,
                        CurrentVersion = _packageIdentity.Version.ToString(4),
                        DownloadProgressPercentage = 0,
                        IsDownloadProgressIndeterminate = false,
                        IsBusy = false,
                    });
                    return;
                }

                _latestRelease = release;
                int comparison = _packageIdentity.Version.CompareTo(release.Version);
                UpdateStatus status = comparison < 0 ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate;

                SetState(new UpdateState
                {
                    Status = status,
                    CurrentVersion = _packageIdentity.Version.ToString(4),
                    LatestVersion = release.Version.ToString(4),
                    ReleasePageUrl = release.ReleasePageUrl,
                    SelectedAssetName = string.Empty,
                    DownloadProgressPercentage = 0,
                    IsDownloadProgressIndeterminate = false,
                    IsBusy = false,
                });
            }
            catch (Exception ex)
            {
                _latestRelease = null;
                _logService.Add($"Update metadata check error: {ex.Message}", LogLevel.Warning);
                SetState(new UpdateState
                {
                    Status = UpdateStatus.Unavailable,
                    CurrentVersion = _packageIdentity.Version.ToString(4),
                    DownloadProgressPercentage = 0,
                    IsDownloadProgressIndeterminate = false,
                    IsBusy = false,
                });
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task<bool> DownloadAndInstallLatestAsync(CancellationToken cancellationToken = default)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            bool failureLogged = false;
            try
            {
                if (_latestRelease is null || _latestRelease.Version is null || _packageIdentity.Version.CompareTo(_latestRelease.Version) >= 0)
                {
                    return false;
                }

                GitHubReleaseAsset? asset = SelectBestAsset(_latestRelease);
                if (asset is null)
                {
                    _logService.Add("Update download skipped: no compatible .msix asset found in latest release.", LogLevel.Warning);
                    SetState(new UpdateState
                    {
                        Status = UpdateStatus.Unavailable,
                        CurrentVersion = _packageIdentity.Version.ToString(4),
                        LatestVersion = _latestRelease.Version.ToString(4),
                        ReleasePageUrl = _latestRelease.ReleasePageUrl,
                        DownloadProgressPercentage = 0,
                        IsDownloadProgressIndeterminate = false,
                        IsBusy = false,
                    });
                    return false;
                }

                SetState(new UpdateState
                {
                    Status = UpdateStatus.Downloading,
                    CurrentVersion = _packageIdentity.Version.ToString(4),
                    LatestVersion = _latestRelease.Version.ToString(4),
                    ReleasePageUrl = _latestRelease.ReleasePageUrl,
                    SelectedAssetName = asset.Name,
                    DownloadProgressPercentage = 0,
                    IsDownloadProgressIndeterminate = true,
                    IsBusy = true,
                });

                string downloadDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "ClashWinUI",
                    "DownloadUpdate");
                Directory.CreateDirectory(downloadDirectory);

                string msixPath = Path.Combine(downloadDirectory, asset.Name);
                try
                {
                    await DownloadWithFallbackAsync(asset, msixPath, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    failureLogged = true;
                    throw;
                }

                SetState(new UpdateState
                {
                    Status = UpdateStatus.LaunchingInstaller,
                    CurrentVersion = _packageIdentity.Version.ToString(4),
                    LatestVersion = _latestRelease.Version.ToString(4),
                    ReleasePageUrl = _latestRelease.ReleasePageUrl,
                    SelectedAssetName = asset.Name,
                    DownloadProgressPercentage = 100,
                    IsDownloadProgressIndeterminate = false,
                    IsBusy = true,
                });

                try
                {
                    TryRegisterApplicationRestart();
                    await RequestInstallPackageAsync(msixPath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (TryHandleInstallCancellation(ex))
                {
                    return false;
                }
                catch (Exception ex) when (TryHandleInstallFailure(ex))
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                if (!failureLogged)
                {
                    _logService.Add($"Update download/install error: {ex.Message}", LogLevel.Warning);
                }

                SetState(new UpdateState
                {
                    Status = UpdateStatus.DownloadFailed,
                    CurrentVersion = _packageIdentity.Version.ToString(4),
                    LatestVersion = _latestRelease?.Version?.ToString(4) ?? string.Empty,
                    ReleasePageUrl = _latestRelease?.ReleasePageUrl ?? string.Empty,
                    SelectedAssetName = _currentState.SelectedAssetName,
                    DownloadProgressPercentage = _currentState.DownloadProgressPercentage,
                    IsDownloadProgressIndeterminate = false,
                    IsBusy = false,
                });
                return false;
            }
            finally
            {
                _sync.Release();
            }
        }

        private async Task<GitHubReleaseInfo?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using HttpResponseMessage response = await _metadataClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logService.Add("Update metadata unavailable: latest GitHub release returned 404.", LogLevel.Warning);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logService.Add($"Update metadata request failed: {(int)response.StatusCode} {response.ReasonPhrase}", LogLevel.Warning);
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string tagName = GetString(root, "tag_name");
            if (!TryParseVersion(tagName, out Version? version))
            {
                _logService.Add($"Update metadata parse failed: unsupported release tag '{tagName}'.", LogLevel.Warning);
                return null;
            }

            var assets = new List<GitHubReleaseAsset>();
            if (root.TryGetProperty("assets", out JsonElement assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement assetElement in assetsElement.EnumerateArray())
                {
                    string name = GetString(assetElement, "name");
                    string url = GetString(assetElement, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    assets.Add(new GitHubReleaseAsset(
                        name,
                        url,
                        ResolveAssetArchitecture(name)));
                }
            }

            return new GitHubReleaseInfo(
                tagName,
                version,
                GetString(root, "html_url"),
                assets);
        }

        private async Task DownloadWithFallbackAsync(GitHubReleaseAsset asset, string destinationPath, CancellationToken cancellationToken)
        {
            bool useCnProxy = await ShouldUseChinaProxyAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> candidates = BuildCandidateUrls(asset.BrowserDownloadUrl, useCnProxy);

            string tempPath = destinationPath + ".download";
            Exception? lastException = null;
            UpdateDownloadFailureStage lastFailureStage = UpdateDownloadFailureStage.None;
            string lastCandidate = string.Empty;

            foreach (string candidate in candidates)
            {
                try
                {
                    using HttpResponseMessage response = await _downloadClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        lastException = new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                        lastFailureStage = UpdateDownloadFailureStage.Download;
                        lastCandidate = candidate;
                        continue;
                    }

                    {
                        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        await using FileStream target = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        await CopyToFileWithProgressAsync(source, target, response.Content.Headers.ContentLength, asset, cancellationToken).ConfigureAwait(false);
                    }

                    try
                    {
                        File.Move(tempPath, destinationPath, overwrite: true);
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        lastFailureStage = UpdateDownloadFailureStage.Promote;
                        lastCandidate = candidate;
                        TryDeleteTempFile(tempPath);
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    lastFailureStage = UpdateDownloadFailureStage.Download;
                    lastCandidate = candidate;
                    TryDeleteTempFile(tempPath);
                }
            }

            LogDownloadFailure(lastFailureStage, tempPath, destinationPath, lastCandidate, lastException);
            throw lastException ?? new InvalidOperationException("Update download failed for all candidate URLs.");
        }

        private async Task RequestInstallPackageAsync(string msixPath, CancellationToken cancellationToken)
        {
            var packageManager = new PackageManager();
            Uri packageUri = new(Path.GetFullPath(msixPath));
            var options = DeploymentOptions.ForceApplicationShutdown;
            var operation = packageManager.RequestAddPackageAsync(
                packageUri,
                dependencyPackageUris: null,
                deploymentOptions: options,
                targetVolume: null,
                optionalPackageFamilyNames: null,
                relatedPackageUris: null);

            DeploymentResult result = await operation.AsTask(cancellationToken).ConfigureAwait(false);
            if (result.ExtendedErrorCode is { HResult: not 0 } error)
            {
                throw CreateDeploymentException(error.HResult, result.ErrorText);
            }
        }

        private async Task CopyToFileWithProgressAsync(
            Stream source,
            FileStream target,
            long? contentLength,
            GitHubReleaseAsset asset,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int lastReportedPercentage = -1;

            if (!contentLength.HasValue || contentLength.Value <= 0)
            {
                SetState(new UpdateState
                {
                    Status = UpdateStatus.Downloading,
                    CurrentVersion = _packageIdentity.Version.ToString(4),
                    LatestVersion = _latestRelease?.Version?.ToString(4) ?? string.Empty,
                    ReleasePageUrl = _latestRelease?.ReleasePageUrl ?? string.Empty,
                    SelectedAssetName = asset.Name,
                    DownloadProgressPercentage = 0,
                    IsDownloadProgressIndeterminate = true,
                    IsBusy = true,
                });
            }

            while (true)
            {
                int bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                totalRead += bytesRead;

                if (contentLength.HasValue && contentLength.Value > 0)
                {
                    int progress = (int)Math.Clamp(
                        Math.Round(totalRead * 100d / contentLength.Value, MidpointRounding.AwayFromZero),
                        0,
                        100);
                    if (progress != lastReportedPercentage)
                    {
                        lastReportedPercentage = progress;
                        SetState(new UpdateState
                        {
                            Status = UpdateStatus.Downloading,
                            CurrentVersion = _packageIdentity.Version.ToString(4),
                            LatestVersion = _latestRelease?.Version?.ToString(4) ?? string.Empty,
                            ReleasePageUrl = _latestRelease?.ReleasePageUrl ?? string.Empty,
                            SelectedAssetName = asset.Name,
                            DownloadProgressPercentage = progress,
                            IsDownloadProgressIndeterminate = false,
                            IsBusy = true,
                        });
                    }
                }
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> ShouldUseChinaProxyAsync(CancellationToken cancellationToken)
        {
            try
            {
                PublicNetworkInfo? info = await _networkInfoService.GetPublicNetworkInfoAsync(cancellationToken).ConfigureAwait(false);
                return string.Equals(info?.CountryCode, "CN", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static IReadOnlyList<string> BuildCandidateUrls(string browserDownloadUrl, bool useChinaProxy)
        {
            if (!useChinaProxy)
            {
                return [browserDownloadUrl];
            }

            return
            [
                "https://gh-proxy.com/" + browserDownloadUrl,
                "https://gh.llkk.cc/" + browserDownloadUrl,
                browserDownloadUrl,
            ];
        }

        private GitHubReleaseAsset? SelectBestAsset(GitHubReleaseInfo release)
        {
            string preferredArchitecture = _packageIdentity.Architecture;

            GitHubReleaseAsset? exactMatch = release.Assets.FirstOrDefault(static asset => asset.Name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
                && string.Equals(asset.Architecture, AppPackageInfoHelper.Current.Architecture, StringComparison.OrdinalIgnoreCase));
            if (exactMatch is not null)
            {
                return exactMatch;
            }

            GitHubReleaseAsset? suffixMatch = release.Assets.FirstOrDefault(asset =>
                asset.Name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
                && asset.Name.Contains($"_{preferredArchitecture}.msix", StringComparison.OrdinalIgnoreCase));
            if (suffixMatch is not null)
            {
                return suffixMatch;
            }

            return release.Assets.FirstOrDefault(asset => asset.Name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase));
        }

        private void SetState(UpdateState state)
        {
            _currentState = state;
            RaiseStateChanged();
        }

        private void RaiseStateChanged()
        {
            if (Application.Current is App app
                && app.ActiveWindow is Microsoft.UI.Xaml.Window window
                && window.DispatcherQueue is { } dispatcherQueue)
            {
                _ = dispatcherQueue.TryEnqueue(() => StateChanged?.Invoke(this, EventArgs.Empty));
                return;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            var client = new HttpClient
            {
                Timeout = timeout,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClashWinUI/1.0");
            return client;
        }

        private static bool TryParseVersion(string rawTag, out Version? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(rawTag))
            {
                return false;
            }

            string normalized = rawTag.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[1..];
            }

            int suffixIndex = normalized.IndexOfAny(['-', '+']);
            if (suffixIndex >= 0)
            {
                normalized = normalized[..suffixIndex];
            }

            string[] segments = normalized.Split('.', StringSplitOptions.None);
            if (segments.Length is < 2 or > 4)
            {
                return false;
            }

            int[] components = new int[4];
            for (int index = 0; index < segments.Length; index++)
            {
                if (!int.TryParse(segments[index], out components[index]))
                {
                    return false;
                }
            }

            version = new Version(components[0], components[1], components[2], components[3]);
            return true;
        }

        private static string ResolveAssetArchitecture(string assetName)
        {
            string normalized = assetName.ToLowerInvariant();
            if (normalized.Contains("_arm64"))
            {
                return "arm64";
            }

            if (normalized.Contains("_x86"))
            {
                return "x86";
            }

            return normalized.Contains("_x64") ? "x64" : string.Empty;
        }

        private static string GetString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement element))
            {
                return string.Empty;
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString()?.Trim() ?? string.Empty,
                JsonValueKind.Number => element.GetRawText(),
                _ => string.Empty,
            };
        }

        private void LogDownloadFailure(
            UpdateDownloadFailureStage stage,
            string tempPath,
            string destinationPath,
            string candidate,
            Exception? exception)
        {
            if (exception is null)
            {
                return;
            }

            string message = stage switch
            {
                UpdateDownloadFailureStage.Promote => $"Update package promote error from '{tempPath}' to '{destinationPath}': {exception.Message}",
                UpdateDownloadFailureStage.Download when !string.IsNullOrWhiteSpace(candidate) => $"Update package download/write error for '{destinationPath}' from '{candidate}': {exception.Message}",
                _ => $"Update package download/write error for '{destinationPath}': {exception.Message}",
            };

            _logService.Add(message, LogLevel.Warning);
        }

        private bool TryHandleInstallCancellation(Exception ex)
        {
            if (!IsCancelledException(ex))
            {
                return false;
            }

            _logService.Add("Update install canceled by user.", LogLevel.Warning);
            SetState(new UpdateState
            {
                Status = UpdateStatus.DownloadFailed,
                CurrentVersion = _packageIdentity.Version.ToString(4),
                LatestVersion = _latestRelease?.Version?.ToString(4) ?? string.Empty,
                ReleasePageUrl = _latestRelease?.ReleasePageUrl ?? string.Empty,
                SelectedAssetName = _currentState.SelectedAssetName,
                DownloadProgressPercentage = _currentState.DownloadProgressPercentage,
                IsDownloadProgressIndeterminate = false,
                IsBusy = false,
            });
            return true;
        }

        private bool TryHandleInstallFailure(Exception ex)
        {
            string errorText = ex.Message;
            if (ex is DeploymentException deploymentException && !string.IsNullOrWhiteSpace(deploymentException.ErrorText))
            {
                errorText = deploymentException.ErrorText;
            }

            _logService.Add($"Update install request failed: 0x{ex.HResult:X8} {errorText}", LogLevel.Warning);
            SetState(new UpdateState
            {
                Status = UpdateStatus.DownloadFailed,
                CurrentVersion = _packageIdentity.Version.ToString(4),
                LatestVersion = _latestRelease?.Version?.ToString(4) ?? string.Empty,
                ReleasePageUrl = _latestRelease?.ReleasePageUrl ?? string.Empty,
                SelectedAssetName = _currentState.SelectedAssetName,
                DownloadProgressPercentage = _currentState.DownloadProgressPercentage,
                IsDownloadProgressIndeterminate = false,
                IsBusy = false,
            });
            return true;
        }

        private static Exception CreateDeploymentException(int hresult, string errorText)
        {
            string normalizedText = string.IsNullOrWhiteSpace(errorText) ? "Package deployment failed." : errorText;
            return new DeploymentException(hresult, normalizedText);
        }

        private static bool IsCancelledException(Exception ex)
        {
            return ex.HResult == ErrorCancelledHResult
                || ex is DeploymentException { HResult: ErrorCancelledHResult }
                || ex is OperationCanceledException;
        }

        private static void TryRegisterApplicationRestart()
        {
            int hresult = RegisterApplicationRestart(null, 0);
            if (hresult != 0)
            {
                Debug.WriteLine($"RegisterApplicationRestart failed: 0x{hresult:X8}");
            }
        }

        private static void TryDeleteTempFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort.
            }
        }

        private sealed class GitHubReleaseInfo
        {
            public GitHubReleaseInfo(string tagName, Version? version, string releasePageUrl, IReadOnlyList<GitHubReleaseAsset> assets)
            {
                TagName = tagName;
                Version = version;
                ReleasePageUrl = releasePageUrl;
                Assets = assets;
            }

            public string TagName { get; }

            public Version? Version { get; }

            public string ReleasePageUrl { get; }

            public IReadOnlyList<GitHubReleaseAsset> Assets { get; }
        }

        private sealed class GitHubReleaseAsset
        {
            public GitHubReleaseAsset(string name, string browserDownloadUrl, string architecture)
            {
                Name = name;
                BrowserDownloadUrl = browserDownloadUrl;
                Architecture = architecture;
            }

            public string Name { get; }

            public string BrowserDownloadUrl { get; }

            public string Architecture { get; }
        }

        private sealed class DeploymentException : Exception
        {
            public DeploymentException(int hresult, string errorText)
                : base(errorText)
            {
                HResult = hresult;
                ErrorText = errorText;
            }

            public string ErrorText { get; }
        }

        private enum UpdateDownloadFailureStage
        {
            None,
            Download,
            Promote,
        }

        [DllImport("kernel32.dll", SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern int RegisterApplicationRestart(string? pwzCommandline, int dwFlags);
    }
}

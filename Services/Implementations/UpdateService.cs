
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
using System.Xml.Linq;

namespace ClashWinUI.Services.Implementations
{
    public sealed class UpdateService : IUpdateService
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/TianLang-Hacker/ClashWinUI/releases/latest";
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
                string appInstallerPath = Path.Combine(downloadDirectory, Path.GetFileNameWithoutExtension(asset.Name) + ".appinstaller");

                await DownloadWithFallbackAsync(asset, msixPath, cancellationToken).ConfigureAwait(false);
                WriteLocalAppInstallerFile(appInstallerPath, msixPath, asset, _latestRelease.Version);

                SetState(new UpdateState
                {
                    Status = UpdateStatus.LaunchingInstaller,
                    CurrentVersion = _packageIdentity.Version.ToString(4),
                    LatestVersion = _latestRelease.Version.ToString(4),
                    ReleasePageUrl = _latestRelease.ReleasePageUrl,
                    SelectedAssetName = asset.Name,
                    DownloadProgressPercentage = 100,
                    IsDownloadProgressIndeterminate = false,
                    IsBusy = false,
                });

                Process.Start(new ProcessStartInfo
                {
                    FileName = appInstallerPath,
                    UseShellExecute = true,
                });

                return true;
            }
            catch (Exception ex)
            {
                _logService.Add($"Update download/install error: {ex.Message}", LogLevel.Warning);
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

            foreach (string candidate in candidates)
            {
                try
                {
                    using HttpResponseMessage response = await _downloadClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        lastException = new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                        continue;
                    }

                    await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using FileStream target = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await CopyToFileWithProgressAsync(source, target, response.Content.Headers.ContentLength, asset, cancellationToken).ConfigureAwait(false);

                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }

                    File.Move(tempPath, destinationPath, overwrite: true);
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    TryDeleteTempFile(tempPath);
                }
            }

            throw lastException ?? new InvalidOperationException("Update download failed for all candidate URLs.");
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

        private void WriteLocalAppInstallerFile(string appInstallerPath, string msixPath, GitHubReleaseAsset asset, Version version)
        {
            string appInstallerUri = new Uri(appInstallerPath).AbsoluteUri;
            string msixUri = new Uri(msixPath).AbsoluteUri;
            string processorArchitecture = string.IsNullOrWhiteSpace(asset.Architecture)
                ? _packageIdentity.Architecture
                : asset.Architecture;

            XNamespace ns = "http://schemas.microsoft.com/appx/appinstaller/2017/2";
            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(
                    ns + "AppInstaller",
                    new XAttribute("Uri", appInstallerUri),
                    new XAttribute("Version", version.ToString(4)),
                    new XElement(
                        ns + "MainPackage",
                        new XAttribute("Name", _packageIdentity.Name),
                        new XAttribute("Publisher", _packageIdentity.Publisher),
                        new XAttribute("Version", version.ToString(4)),
                        new XAttribute("ProcessorArchitecture", processorArchitecture),
                        new XAttribute("Uri", msixUri)),
                    new XElement(
                        ns + "UpdateSettings",
                        new XElement(
                            ns + "OnLaunch",
                            new XAttribute("HoursBetweenUpdateChecks", "0"),
                            new XAttribute("ShowPrompt", "false"),
                            new XAttribute("UpdateBlocksActivation", "false")))));

            Directory.CreateDirectory(Path.GetDirectoryName(appInstallerPath)!);
            document.Save(appInstallerPath);
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

            return Version.TryParse(normalized, out version);
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
    }
}

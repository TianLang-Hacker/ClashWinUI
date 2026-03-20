using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Serialization;
using ClashWinUI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Implementations
{
    public class ProfileService : IProfileService
    {
        private const string IndexFileName = "profiles.json";
        private static readonly HttpClient HttpClient = CreateHttpClient();
        private static readonly SubscriptionRequestProfile[] SubscriptionRequestProfiles =
        [
            new(
                "ClashForWindows",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["User-Agent"] = "ClashforWindows",
                    ["Accept"] = "text/yaml, application/yaml, text/plain, */*",
                }),
            new(
                "ClashVerge",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["User-Agent"] = "clash-verge/v2",
                    ["Accept"] = "text/yaml, application/yaml, text/plain, */*",
                }),
            new(
                "Mihomo",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["User-Agent"] = "mihomo",
                    ["Accept"] = "text/yaml, application/yaml, text/plain, */*",
                }),
            new(
                "Default",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
        ];

        private readonly IConfigService _configService;
        private readonly IAppLogService _logService;
        private readonly string _indexFilePath;
        private ProfileStoreState _store;

        public ProfileService(IConfigService configService, IAppLogService logService)
        {
            _configService = configService;
            _logService = logService;

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ProfilesDirectory = Path.Combine(userProfile, "ClashWinUI", "Profiles");
            _indexFilePath = Path.Combine(ProfilesDirectory, IndexFileName);

            Directory.CreateDirectory(ProfilesDirectory);
            _store = LoadStore();
        }

        public string ProfilesDirectory { get; }

        public IReadOnlyList<ProfileItem> GetProfiles()
        {
            EnsureWorkspaceLayouts();
            RefreshNodeCountsIfChanged();
            return _store.Profiles
                .OrderByDescending(item => item.UpdatedAt)
                .ToArray();
        }

        public ProfileItem? GetActiveProfile()
        {
            EnsureWorkspaceLayouts();
            RefreshNodeCountsIfChanged();
            if (string.IsNullOrWhiteSpace(_store.ActiveProfileId))
            {
                return null;
            }

            ProfileItem? profile = _store.Profiles.FirstOrDefault(item => string.Equals(item.Id, _store.ActiveProfileId, StringComparison.OrdinalIgnoreCase));
            if (profile is not null && TryRefreshSubscriptionSource(profile))
            {
                SaveStore();
            }

            return profile;
        }

        public async Task<ProfileItem> AddOrUpdateFromSubscriptionAsync(string subscriptionUrl, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionUrl);

            if (!Uri.TryCreate(subscriptionUrl, UriKind.Absolute, out Uri? uri))
            {
                throw new ArgumentException("Invalid subscription URL.", nameof(subscriptionUrl));
            }

            byte[] content = await DownloadSubscriptionContentAsync(uri, cancellationToken).ConfigureAwait(false);
            if (content.Length == 0)
            {
                throw new InvalidOperationException("Subscription content is empty.");
            }

            byte[] normalizedContent = NormalizeImportedContent(content, isSubscription: true);

            ProfileItem? existing = _store.Profiles.FirstOrDefault(item =>
                string.Equals(item.SourceType, "subscription", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Source, subscriptionUrl, StringComparison.OrdinalIgnoreCase));

            ProfileItem profile = existing ?? new ProfileItem
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.Now,
            };

            profile.DisplayName = existing?.DisplayName ?? BuildDisplayName(uri.Host);
            profile.SourceType = "subscription";
            profile.Source = subscriptionUrl;
            profile.UpdatedAt = DateTimeOffset.Now;

            var workspace = _configService.GetWorkspace(profile);
            Directory.CreateDirectory(workspace.DirectoryPath);
            string sourcePath = workspace.SourcePath;
            await File.WriteAllBytesAsync(sourcePath, normalizedContent, cancellationToken).ConfigureAwait(false);

            profile.WorkspaceDirectory = workspace.DirectoryPath;
            profile.FilePath = sourcePath;
            profile.NodeCount = ProxyConfigParser.ParseFromFile(sourcePath).Count;

            if (existing is null)
            {
                _store.Profiles.Add(profile);
            }

            if (string.IsNullOrWhiteSpace(_store.ActiveProfileId))
            {
                _store.ActiveProfileId = profile.Id;
            }

            _configService.EnsureWorkspace(profile);
            _configService.BuildRuntime(profile);
            SaveStore();
            _logService.Add($"Subscription saved: {MaskSensitiveQuery(uri)}");
            return profile;
        }

        public async Task<ProfileItem> ImportLocalFileAsync(string localFilePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);
            if (!File.Exists(localFilePath))
            {
                throw new FileNotFoundException("Profile file not found.", localFilePath);
            }

            string sourceExtension = Path.GetExtension(localFilePath);
            string extension = string.IsNullOrWhiteSpace(sourceExtension) ? ".yaml" : sourceExtension;
            byte[] importedContent = await File.ReadAllBytesAsync(localFilePath, cancellationToken).ConfigureAwait(false);
            byte[] normalizedContent = NormalizeImportedContent(importedContent, isSubscription: false);

            var profile = new ProfileItem
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = Path.GetFileNameWithoutExtension(localFilePath),
                SourceType = "local",
                Source = localFilePath,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now,
            };

            var workspace = _configService.GetWorkspace(profile);
            Directory.CreateDirectory(workspace.DirectoryPath);
            string destinationPath = workspace.SourcePath;
            await File.WriteAllBytesAsync(destinationPath, normalizedContent, cancellationToken).ConfigureAwait(false);

            profile.WorkspaceDirectory = workspace.DirectoryPath;
            profile.FilePath = destinationPath;
            profile.NodeCount = ProxyConfigParser.ParseFromFile(destinationPath).Count;

            _store.Profiles.Add(profile);
            if (string.IsNullOrWhiteSpace(_store.ActiveProfileId))
            {
                _store.ActiveProfileId = profile.Id;
            }

            _configService.EnsureWorkspace(profile);
            _configService.BuildRuntime(profile);
            SaveStore();
            _logService.Add($"Local profile imported: {localFilePath}");
            return profile;
        }

        public bool SetActiveProfile(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return false;
            }

            ProfileItem? profile = _store.Profiles.FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                return false;
            }

            _ = TryRefreshSubscriptionSource(profile);
            _store.ActiveProfileId = profileId;
            _configService.EnsureWorkspace(profile);
            _configService.BuildRuntime(profile);
            SaveStore();
            _logService.Add($"Active profile switched: {profile.DisplayName}");
            return true;
        }

        public bool DeleteProfile(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return false;
            }

            ProfileItem? profile = _store.Profiles.FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                return false;
            }

            _store.Profiles.Remove(profile);
            if (!string.IsNullOrWhiteSpace(profile.WorkspaceDirectory) && Directory.Exists(profile.WorkspaceDirectory))
            {
                try
                {
                    Directory.Delete(profile.WorkspaceDirectory, recursive: true);
                }
                catch
                {
                    // Keep metadata deletion successful even if workspace cannot be deleted.
                }
            }
            else if (File.Exists(profile.FilePath))
            {
                try
                {
                    File.Delete(profile.FilePath);
                }
                catch
                {
                    // Keep metadata deletion successful even if file cannot be deleted.
                }
            }

            if (string.Equals(_store.ActiveProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                _store.ActiveProfileId = _store.Profiles.FirstOrDefault()?.Id;
            }

            SaveStore();
            _logService.Add($"Profile deleted: {profile.DisplayName}");
            return true;
        }

        private ProfileStoreState LoadStore()
        {
            try
            {
                if (!File.Exists(_indexFilePath))
                {
                    return new ProfileStoreState();
                }

                string content = File.ReadAllText(_indexFilePath);
                ProfileStoreState? store = JsonSerializer.Deserialize(content, ClashJsonContext.Default.ProfileStoreState);
                if (store is null)
                {
                    return new ProfileStoreState();
                }

                store.Profiles ??= new List<ProfileItem>();
                return store;
            }
            catch
            {
                return new ProfileStoreState();
            }
        }

        private void SaveStore()
        {
            Directory.CreateDirectory(ProfilesDirectory);
            string content = JsonSerializer.Serialize(_store, ClashJsonContext.Default.ProfileStoreState);
            File.WriteAllText(_indexFilePath, content);
        }

        private void RefreshNodeCountsIfChanged()
        {
            bool changed = false;
            foreach (ProfileItem profile in _store.Profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.FilePath) || !File.Exists(profile.FilePath))
                {
                    if (profile.NodeCount != 0)
                    {
                        profile.NodeCount = 0;
                        changed = true;
                    }

                    continue;
                }

                int parsedCount = ProxyConfigParser.ParseFromFile(profile.FilePath).Count;
                if (profile.NodeCount != parsedCount)
                {
                    profile.NodeCount = parsedCount;
                    profile.UpdatedAt = DateTimeOffset.Now;
                    changed = true;
                }
            }

            if (changed)
            {
                SaveStore();
            }
        }

        private void EnsureWorkspaceLayouts()
        {
            bool changed = false;
            foreach (ProfileItem profile in _store.Profiles)
            {
                string originalPath = profile.FilePath;
                string originalWorkspace = profile.WorkspaceDirectory;

                _configService.EnsureWorkspace(profile);

                if (!string.Equals(originalPath, profile.FilePath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(originalWorkspace, profile.WorkspaceDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                SaveStore();
            }
        }

        private static string BuildDisplayName(string raw)
        {
            string name = string.IsNullOrWhiteSpace(raw) ? "Profile" : raw.Trim();
            return name.Length > 50 ? name[..50] : name;
        }

        private static string BuildFileName(string rawName, string extension)
        {
            string sanitized = string.Concat((rawName ?? "profile").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "profile";
            }

            string suffix = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            return $"{sanitized}-{suffix}{extension}";
        }

        private static string MaskSensitiveQuery(Uri uri)
        {
            return string.IsNullOrWhiteSpace(uri.Query)
                ? uri.ToString()
                : $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        }

        private byte[] NormalizeImportedContent(byte[] rawContent, bool isSubscription)
        {
            string contentKind = isSubscription ? "Subscription" : "Profile";
            SubscriptionContentNormalizationResult normalization = SubscriptionContentNormalizer.Normalize(rawContent);
            byte[] normalizedContent = normalization.Content;

            if (normalization.Status == SubscriptionContentNormalizationStatus.DecodedFromBase64)
            {
                _logService.Add($"{contentKind} content normalized from Base64 to YAML.");
            }
            else if (normalization.Status == SubscriptionContentNormalizationStatus.Base64DecodedButNotYaml)
            {
                _logService.Add($"{contentKind} is Base64, but decoded content is not Mihomo YAML. Keep decoded content.", LogLevel.Warning);
            }
            else if (normalization.Status == SubscriptionContentNormalizationStatus.Base64DecodeFailed)
            {
                _logService.Add($"{contentKind} appears to be Base64 but decode failed. Keep raw content.", LogLevel.Warning);
            }
            else if (normalization.Status == SubscriptionContentNormalizationStatus.Unrecognized)
            {
                _logService.Add($"{contentKind} content format unrecognized. Keep raw content.", LogLevel.Warning);
            }

            if ((normalization.Status == SubscriptionContentNormalizationStatus.Base64DecodedButNotYaml
                    || normalization.Status == SubscriptionContentNormalizationStatus.Unrecognized)
                && ShareLinkSubscriptionConverter.TryConvertToMihomoYaml(normalizedContent, out byte[] convertedYaml, out int convertedCount))
            {
                normalizedContent = convertedYaml;
                _logService.Add($"{contentKind} share links converted to Mihomo YAML. Nodes={convertedCount}.");
            }

            return normalizedContent;
        }

        private async Task<byte[]> DownloadSubscriptionContentAsync(Uri uri, CancellationToken cancellationToken)
        {
            byte[]? bestContent = null;
            SubscriptionContentNormalizationStatus bestStatus = SubscriptionContentNormalizationStatus.Unrecognized;
            string? bestProfileName = null;

            foreach (SubscriptionRequestProfile requestProfile in SubscriptionRequestProfiles)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    foreach ((string headerName, string headerValue) in requestProfile.Headers)
                    {
                        request.Headers.TryAddWithoutValidation(headerName, headerValue);
                    }

                    using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (content.Length == 0)
                    {
                        continue;
                    }

                    SubscriptionContentNormalizationResult normalization = SubscriptionContentNormalizer.Normalize(content);
                    if (IsPreferredSubscriptionPayload(normalization.Status))
                    {
                        _logService.Add($"Subscription downloaded using profile '{requestProfile.Name}'.");
                        return content;
                    }

                    if (bestContent is null || GetNormalizationScore(normalization.Status) > GetNormalizationScore(bestStatus))
                    {
                        bestContent = content;
                        bestStatus = normalization.Status;
                        bestProfileName = requestProfile.Name;
                    }
                }
                catch (Exception ex)
                {
                    _logService.Add($"Subscription download profile '{requestProfile.Name}' failed: {ex.Message}", LogLevel.Warning);
                }
            }

            if (bestContent is not null)
            {
                if (!string.IsNullOrWhiteSpace(bestProfileName))
                {
                    _logService.Add($"Subscription downloaded using fallback profile '{bestProfileName}'.", LogLevel.Warning);
                }

                return bestContent;
            }

            throw new InvalidOperationException("All subscription download attempts failed.");
        }

        private bool TryRefreshSubscriptionSource(ProfileItem profile)
        {
            if (!string.Equals(profile.SourceType, "subscription", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(profile.Source)
                || !Uri.TryCreate(profile.Source, UriKind.Absolute, out Uri? uri))
            {
                return false;
            }

            ProfileConfigWorkspace workspace = _configService.GetWorkspace(profile);
            string sourcePath = workspace.SourcePath;
            if (!ShouldRepairSubscriptionSource(sourcePath))
            {
                return false;
            }

            try
            {
                byte[] downloaded = DownloadSubscriptionContentAsync(uri, CancellationToken.None).GetAwaiter().GetResult();
                byte[] normalized = NormalizeImportedContent(downloaded, isSubscription: true);

                Directory.CreateDirectory(workspace.DirectoryPath);
                File.WriteAllBytes(sourcePath, normalized);

                profile.WorkspaceDirectory = workspace.DirectoryPath;
                profile.FilePath = sourcePath;
                profile.NodeCount = ProxyConfigParser.ParseFromFile(sourcePath).Count;
                profile.UpdatedAt = DateTimeOffset.Now;
                _logService.Add($"Subscription source refreshed: {MaskSensitiveQuery(uri)}");
                return true;
            }
            catch (Exception ex)
            {
                _logService.Add($"Subscription source refresh skipped: {ex.Message}", LogLevel.Warning);
                return false;
            }
        }

        private static bool ShouldRepairSubscriptionSource(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return true;
            }

            byte[] rawContent = File.ReadAllBytes(sourcePath);
            if (rawContent.Length == 0)
            {
                return true;
            }

            SubscriptionContentNormalizationResult normalization = SubscriptionContentNormalizer.Normalize(rawContent);
            if (normalization.Status is SubscriptionContentNormalizationStatus.DecodedFromBase64
                or SubscriptionContentNormalizationStatus.Base64DecodedButNotYaml
                or SubscriptionContentNormalizationStatus.Base64DecodeFailed
                or SubscriptionContentNormalizationStatus.Unrecognized)
            {
                return true;
            }

            string text = Encoding.UTF8.GetString(rawContent);
            return LooksLikeGeneratedShareLinkYaml(text);
        }

        private static bool LooksLikeGeneratedShareLinkYaml(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            return content.Contains("proxy-groups:", StringComparison.OrdinalIgnoreCase)
                && content.Contains("name: 'Proxy'", StringComparison.OrdinalIgnoreCase)
                && content.Contains("- MATCH,Proxy", StringComparison.OrdinalIgnoreCase)
                && !content.Contains("dns:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPreferredSubscriptionPayload(SubscriptionContentNormalizationStatus status)
        {
            return status is SubscriptionContentNormalizationStatus.AlreadyYaml
                or SubscriptionContentNormalizationStatus.DecodedFromBase64;
        }

        private static int GetNormalizationScore(SubscriptionContentNormalizationStatus status)
        {
            return status switch
            {
                SubscriptionContentNormalizationStatus.AlreadyYaml => 4,
                SubscriptionContentNormalizationStatus.DecodedFromBase64 => 3,
                SubscriptionContentNormalizationStatus.Base64DecodedButNotYaml => 2,
                SubscriptionContentNormalizationStatus.Unrecognized => 1,
                _ => 0,
            };
        }

        private static HttpClient CreateHttpClient()
        {
            return new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip
                    | System.Net.DecompressionMethods.Deflate
                    | System.Net.DecompressionMethods.Brotli,
            });
        }

        private sealed class SubscriptionRequestProfile(string name, Dictionary<string, string> headers)
        {
            public string Name { get; } = name;
            public Dictionary<string, string> Headers { get; } = headers;
        }

    }
}

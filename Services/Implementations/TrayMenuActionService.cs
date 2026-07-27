using Microsoft.UI.Xaml;
using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ClashWinUI.ViewModels;

namespace ClashWinUI.Services.Implementations
{
    public sealed class TrayMenuActionService : ITrayMenuActionService, IDisposable
    {
        private const int ProxyMenuNodeLimit = ProxyGroup.VisibleMembersBatchSize;
        private const string ModeRule = "rule";
        private const string ModeGlobal = "global";
        private const string ModeDirect = "direct";

        private readonly IProfileService _profileService;
        private readonly IProfileActivationService _profileActivationService;
        private readonly IConfigService _configService;
        private readonly IMihomoService _mihomoService;
        private readonly IProcessService _processService;
        private readonly ISystemProxyService _systemProxyService;
        private readonly ITunService _tunService;
        private readonly IKernelPathService _kernelPathService;
        private readonly IAppLogService _logService;
        private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
        private readonly SemaphoreSlim _proxyRefreshSemaphore = new(1, 1);
        private readonly object _snapshotGate = new();

        private TrayMenuSnapshot _cachedSnapshot = TrayMenuSnapshot.Empty;
        private bool _isDisposed;

        public event EventHandler<TrayMenuSnapshot>? SnapshotChanged;

        public TrayMenuActionService(
            IProfileService profileService,
            IProfileActivationService profileActivationService,
            IConfigService configService,
            IMihomoService mihomoService,
            IProcessService processService,
            ISystemProxyService systemProxyService,
            ITunService tunService,
            IKernelPathService kernelPathService,
            IAppLogService logService)
        {
            _profileService = profileService;
            _profileActivationService = profileActivationService;
            _configService = configService;
            _mihomoService = mihomoService;
            _processService = processService;
            _systemProxyService = systemProxyService;
            _tunService = tunService;
            _kernelPathService = kernelPathService;
            _logService = logService;

            _profileService.ActiveProfileChanged += OnMenuDataChanged;
            _profileService.ProfilesChanged += OnMenuDataChanged;
            _configService.ConfigurationChanged += OnMenuDataChanged;
            _mihomoService.ConfigApplied += OnMihomoConfigApplied;
        }

        public TrayMenuSnapshot GetCachedSnapshot()
        {
            lock (_snapshotGate)
            {
                return _cachedSnapshot;
            }
        }

        public async Task<TrayMenuSnapshot> RefreshSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (_isDisposed)
            {
                return TrayMenuSnapshot.Empty;
            }

            if (!await _refreshSemaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return GetCachedSnapshot();
            }

            try
            {
                TrayBaseSnapshotResult baseResult = await BuildBaseSnapshotAsync(cancellationToken).ConfigureAwait(false);
                PublishSnapshot(baseResult.Snapshot);
                LogSnapshotSummary("base", baseResult.Snapshot);

                if (baseResult.ActiveProfile is not null)
                {
                    _ = RefreshProxyGroupsSnapshotAsync(baseResult.ActiveProfile, baseResult.Snapshot.ActiveProfileId);
                }

                return baseResult.Snapshot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.Add($"Tray menu snapshot refresh failed: {ex.Message}", LogLevel.Warning);
                return GetCachedSnapshot();
            }
            finally
            {
                _refreshSemaphore.Release();
            }
        }

        public async Task<bool> ApplyModeAsync(string mode, CancellationToken cancellationToken = default)
        {
            ProfileItem? activeProfile = _profileService.GetActiveProfile();
            if (activeProfile is null)
            {
                return false;
            }

            string normalizedMode = NormalizeMode(mode);
            MixinSettings settings = await Task.Run(() => _configService.LoadMixin(activeProfile), cancellationToken).ConfigureAwait(false);
            if (string.Equals(NormalizeMode(settings.Mode), normalizedMode, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            MixinSettings previousSettings = CloneMixinSettings(settings);
            settings.Mode = normalizedMode;
            bool applied = await SaveBuildApplyAndSyncAsync(activeProfile, settings, cancellationToken).ConfigureAwait(false);
            if (!applied)
            {
                await RestoreMixinSettingsAsync(activeProfile, previousSettings, cancellationToken).ConfigureAwait(false);
            }

            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return applied;
        }

        public async Task<bool> ActivateProfileAsync(string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return false;
            }

            ProfileItem? profile = _profileService.GetProfiles()
                .FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                return false;
            }

            ProfileActivationResult result = await _profileActivationService.ActivateAsync(profile, cancellationToken).ConfigureAwait(false);
            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return result.Applied;
        }

        public async Task<bool> SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken = default)
        {
            bool selected = await _mihomoService.SelectProxyAsync(groupName, proxyName, cancellationToken).ConfigureAwait(false);
            if (selected)
            {
                await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }

            return selected;
        }

        public async Task<bool> SetTunEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            ProfileItem? activeProfile = _profileService.GetActiveProfile();
            if (activeProfile is null)
            {
                return false;
            }

            MixinSettings settings = await Task.Run(() => _configService.LoadMixin(activeProfile), cancellationToken).ConfigureAwait(false);
            if (settings.TunEnabled == enabled)
            {
                return true;
            }

            MixinSettings previousSettings = CloneMixinSettings(settings);
            settings.TunEnabled = enabled;

            if (enabled && !AppElevationHelper.IsProcessElevated())
            {
                try
                {
                    await Task.Run(
                        () => _configService.SaveMixinAndBuildRuntime(activeProfile, settings),
                        cancellationToken).ConfigureAwait(false);

                    bool relaunched = false;
                    if (Application.Current is App app)
                    {
                        relaunched = await app.RequestElevatedRestartForTunAsync(MainViewModel.SettingsRouteKey)
                            .ConfigureAwait(false);
                    }

                    if (relaunched)
                    {
                        _logService.Add("Tray TUN enable requested administrator elevation. Restarting elevated process.");
                        return true;
                    }

                    await RestoreMixinSettingsAsync(activeProfile, previousSettings, cancellationToken).ConfigureAwait(false);
                    _logService.Add("Tray TUN enable cancelled or failed during elevation.", LogLevel.Warning);
                    await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }
                catch (Exception ex)
                {
                    await RestoreMixinSettingsAsync(activeProfile, previousSettings, cancellationToken).ConfigureAwait(false);
                    _logService.Add($"Tray TUN elevation failed: {ex.Message}", LogLevel.Warning);
                    await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }
            }

            if (enabled)
            {
                TunPreparationResult preparation = await Task.Run(
                    () => _tunService.ValidateEnvironment(_kernelPathService.ResolveKernelPath()),
                    cancellationToken).ConfigureAwait(false);
                if (!preparation.Success)
                {
                    _logService.Add($"Tray TUN enable failed: {preparation.Message}", LogLevel.Warning);
                    return false;
                }
            }

            bool applied = await SaveBuildApplyAndSyncAsync(activeProfile, settings, cancellationToken, forceRestartForTun: true).ConfigureAwait(false);
            if (!applied)
            {
                await RestoreMixinSettingsAsync(activeProfile, previousSettings, cancellationToken).ConfigureAwait(false);
            }

            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return applied;
        }

        public async Task<bool> RestartMihomoCoreAsync(CancellationToken cancellationToken = default)
        {
            string? configPath = _processService.CurrentConfigPath;
            if (string.IsNullOrWhiteSpace(configPath))
            {
                ProfileItem? activeProfile = _profileService.GetActiveProfile();
                configPath = activeProfile is null
                    ? _processService.EnsureStartupConfigPath()
                    : _configService.GetRuntimePath(activeProfile);
            }

            bool restarted = await _processService.RestartAsync(configPath, cancellationToken).ConfigureAwait(false);
            if (restarted)
            {
                await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                    _systemProxyService,
                    _processService,
                    _tunService,
                    configPath,
                    cancellationToken).ConfigureAwait(false);
            }

            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return restarted;
        }

        public void OpenProfilesDirectory()
        {
            Directory.CreateDirectory(_profileService.ProfilesDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _profileService.ProfilesDirectory,
                UseShellExecute = true,
            });
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _profileService.ActiveProfileChanged -= OnMenuDataChanged;
            _profileService.ProfilesChanged -= OnMenuDataChanged;
            _configService.ConfigurationChanged -= OnMenuDataChanged;
            _mihomoService.ConfigApplied -= OnMihomoConfigApplied;
            _refreshSemaphore.Dispose();
            _proxyRefreshSemaphore.Dispose();
        }

        private async Task<TrayBaseSnapshotResult> BuildBaseSnapshotAsync(CancellationToken cancellationToken)
        {
            TrayMenuSnapshot previousSnapshot = GetCachedSnapshot();
            ProfileItem? activeProfile = null;
            string activeProfileId = string.Empty;
            TrayProfileMenuItem[] profiles = Array.Empty<TrayProfileMenuItem>();

            string mode = ModeRule;
            bool tunEnabled = false;

            try
            {
                activeProfile = _profileService.GetActiveProfile();
                activeProfileId = activeProfile?.Id ?? string.Empty;

                profiles = _profileService.GetProfiles()
                    .Select(profile => new TrayProfileMenuItem
                    {
                        Id = profile.Id,
                        DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName,
                        IsActive = string.Equals(profile.Id, activeProfileId, StringComparison.OrdinalIgnoreCase),
                    })
                    .ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.Add($"Tray menu base snapshot failed: {ex.Message}", LogLevel.Warning);
                TrayMenuSnapshot fallbackSnapshot = previousSnapshot.IsLoaded
                    ? previousSnapshot
                    : new TrayMenuSnapshot
                    {
                        IsLoaded = true,
                    };
                return new TrayBaseSnapshotResult(fallbackSnapshot, null);
            }

            if (activeProfile is not null)
            {
                try
                {
                    MixinSettings settings = await Task.Run(() => _configService.LoadMixin(activeProfile), cancellationToken).ConfigureAwait(false);
                    mode = NormalizeMode(settings.Mode);
                    tunEnabled = settings.TunEnabled;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logService.Add($"Tray menu mixin snapshot failed: {ex.Message}", LogLevel.Warning);
                    if (string.Equals(previousSnapshot.ActiveProfileId, activeProfileId, StringComparison.OrdinalIgnoreCase))
                    {
                        mode = previousSnapshot.Mode;
                        tunEnabled = previousSnapshot.TunEnabled;
                    }
                }
            }

            TrayProxyGroupMenuItem[] retainedProxyGroups =
                activeProfile is not null
                && string.Equals(previousSnapshot.ActiveProfileId, activeProfileId, StringComparison.OrdinalIgnoreCase)
                    ? previousSnapshot.ProxyGroups.ToArray()
                    : Array.Empty<TrayProxyGroupMenuItem>();

            TrayMenuSnapshot snapshot = new()
            {
                IsLoaded = true,
                ActiveProfileId = activeProfileId,
                Mode = mode,
                TunEnabled = tunEnabled,
                ProxyGroupsLoading = activeProfile is not null,
                ProxyGroupsUnavailable = false,
                ProxyGroupsErrorMessage = string.Empty,
                Profiles = profiles,
                ProxyGroups = retainedProxyGroups,
            };

            return new TrayBaseSnapshotResult(snapshot, activeProfile);
        }

        private async Task RefreshProxyGroupsSnapshotAsync(ProfileItem activeProfile, string activeProfileId)
        {
            if (_isDisposed)
            {
                return;
            }

            await _proxyRefreshSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                string runtimePath = await Task.Run(() =>
                {
                    string path = _configService.GetRuntimePath(activeProfile);
                    return File.Exists(path) ? path : _configService.BuildRuntime(activeProfile);
                }).ConfigureAwait(false);

                ProxyGroupLoadResult loadResult = await _mihomoService.GetProxyGroupsAsync(runtimePath).ConfigureAwait(false);
                TrayProxyGroupMenuItem[] proxyGroups = BuildProxyGroupMenuItems(loadResult);
                UpdateProxySnapshot(
                    activeProfileId,
                    proxyGroups,
                    isLoading: false,
                    isUnavailable: false,
                    errorMessage: string.Empty);
            }
            catch (Exception ex)
            {
                _logService.Add($"Tray menu proxy snapshot failed: {ex.Message}", LogLevel.Warning);
                UpdateProxySnapshot(
                    activeProfileId,
                    proxyGroups: null,
                    isLoading: false,
                    isUnavailable: true,
                    errorMessage: ex.Message);
            }
            finally
            {
                _proxyRefreshSemaphore.Release();
            }
        }

        private void UpdateProxySnapshot(
            string activeProfileId,
            TrayProxyGroupMenuItem[]? proxyGroups,
            bool isLoading,
            bool isUnavailable,
            string errorMessage)
        {
            TrayMenuSnapshot currentSnapshot = GetCachedSnapshot();
            if (!string.Equals(currentSnapshot.ActiveProfileId, activeProfileId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TrayMenuSnapshot updatedSnapshot = new()
            {
                IsLoaded = currentSnapshot.IsLoaded,
                ActiveProfileId = currentSnapshot.ActiveProfileId,
                Mode = currentSnapshot.Mode,
                TunEnabled = currentSnapshot.TunEnabled,
                ProxyGroupsLoading = isLoading,
                ProxyGroupsUnavailable = isUnavailable,
                ProxyGroupsErrorMessage = errorMessage,
                Profiles = currentSnapshot.Profiles,
                ProxyGroups = proxyGroups ?? currentSnapshot.ProxyGroups,
            };

            PublishSnapshot(updatedSnapshot);
            LogSnapshotSummary("proxy", updatedSnapshot);
        }

        private void PublishSnapshot(TrayMenuSnapshot snapshot)
        {
            lock (_snapshotGate)
            {
                _cachedSnapshot = snapshot;
            }

            SnapshotChanged?.Invoke(this, snapshot);
        }

        private void LogSnapshotSummary(string stage, TrayMenuSnapshot snapshot)
        {
            string proxyState = snapshot.ProxyGroupsLoading
                ? "loading"
                : snapshot.ProxyGroupsUnavailable
                    ? $"unavailable:{snapshot.ProxyGroupsErrorMessage}"
                    : "ready";

            _logService.Add(
                $"Tray menu snapshot {stage}: profiles={snapshot.Profiles.Count}, active={snapshot.ActiveProfileId}, mode={snapshot.Mode}, tun={snapshot.TunEnabled}, proxyGroups={snapshot.ProxyGroups.Count}, proxyState={proxyState}");
        }

        private sealed class TrayBaseSnapshotResult
        {
            public TrayBaseSnapshotResult(TrayMenuSnapshot snapshot, ProfileItem? activeProfile)
            {
                Snapshot = snapshot;
                ActiveProfile = activeProfile;
            }

            public TrayMenuSnapshot Snapshot { get; }

            public ProfileItem? ActiveProfile { get; }
        }

        private static TrayProxyGroupMenuItem[] BuildProxyGroupMenuItems(ProxyGroupLoadResult loadResult)
        {
            return loadResult.Groups
                .Where(group => group.Members.Count > 0)
                .Select(group => new TrayProxyGroupMenuItem
                {
                    Name = group.Name,
                    ControllerName = group.ControllerName,
                    CurrentProxyName = group.CurrentProxyName,
                    HasMoreNodes = group.Members.Count > ProxyMenuNodeLimit,
                    Nodes = group.Members
                        .Take(ProxyMenuNodeLimit)
                        .Select(member => new TrayProxyNodeMenuItem
                        {
                            Name = member.Node.Name,
                            ControllerName = member.Node.ControllerName,
                            IsCurrent = member.IsCurrent,
                        })
                        .ToArray(),
                })
                .ToArray();
        }

        private async Task<bool> SaveBuildApplyAndSyncAsync(
            ProfileItem profile,
            MixinSettings settings,
            CancellationToken cancellationToken,
            bool forceRestartForTun = false)
        {
            string runtimePath = await Task.Run(() =>
            {
                _configService.SaveMixin(profile, settings);
                return _configService.BuildRuntime(profile);
            }, cancellationToken).ConfigureAwait(false);

            ConfigApplyOptions applyOptions = forceRestartForTun
                ? ConfigApplyOptions.ForceRestart
                : ConfigApplyOptions.Default;
            bool applied = await _mihomoService.ApplyConfigAsync(runtimePath, applyOptions, cancellationToken).ConfigureAwait(false);
            if (applied || PathsEqual(_processService.CurrentConfigPath, runtimePath))
            {
                await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                    _systemProxyService,
                    _processService,
                    _tunService,
                    runtimePath,
                    cancellationToken).ConfigureAwait(false);
            }

            return applied;
        }

        private Task RestoreMixinSettingsAsync(
            ProfileItem profile,
            MixinSettings settings,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                _configService.SaveMixin(profile, settings);
                _configService.BuildRuntime(profile);
            }, cancellationToken);
        }

        private void OnMenuDataChanged(object? sender, EventArgs e)
        {
            MarkSnapshotStale();
        }

        private void OnMihomoConfigApplied(object? sender, string e)
        {
            MarkSnapshotStale();
        }

        private void MarkSnapshotStale()
        {
            _ = RefreshSnapshotAsync();
        }

        private static string NormalizeMode(string? mode)
        {
            return mode?.Trim().ToLowerInvariant() switch
            {
                ModeGlobal => ModeGlobal,
                ModeDirect => ModeDirect,
                _ => ModeRule,
            };
        }

        private static MixinSettings CloneMixinSettings(MixinSettings settings)
        {
            return new MixinSettings
            {
                MixedPort = settings.MixedPort,
                HttpPort = settings.HttpPort,
                SocksPort = settings.SocksPort,
                RedirPort = settings.RedirPort,
                TProxyPort = settings.TProxyPort,
                TunEnabled = settings.TunEnabled,
                AllowLan = settings.AllowLan,
                Mode = NormalizeMode(settings.Mode),
                LogLevel = settings.LogLevel,
                Ipv6Enabled = settings.Ipv6Enabled,
            };
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(left.Trim()),
                Path.GetFullPath(right.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}

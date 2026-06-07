using ClashWinUI.Helpers;
using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Implementations
{
    public sealed class ProfileActivationService : IProfileActivationService
    {
        private readonly IProfileService _profileService;
        private readonly IConfigService _configService;
        private readonly IMihomoService _mihomoService;
        private readonly ISystemProxyService _systemProxyService;
        private readonly IProcessService _processService;
        private readonly ITunService _tunService;

        public ProfileActivationService(
            IProfileService profileService,
            IConfigService configService,
            IMihomoService mihomoService,
            ISystemProxyService systemProxyService,
            IProcessService processService,
            ITunService tunService)
        {
            _profileService = profileService;
            _configService = configService;
            _mihomoService = mihomoService;
            _systemProxyService = systemProxyService;
            _processService = processService;
            _tunService = tunService;
        }

        public async Task<ProfileActivationResult> ActivateAsync(ProfileItem profile, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(profile);

            string runtimePath = _configService.GetRuntimePath(profile);
            if (!_profileService.SetActiveProfile(profile.Id))
            {
                return new ProfileActivationResult
                {
                    Applied = false,
                    RuntimePath = runtimePath,
                };
            }

            runtimePath = _configService.GetRuntimePath(profile);
            bool applied = await ApplyRuntimeAndSyncProxyAsync(runtimePath, cancellationToken).ConfigureAwait(false);
            return new ProfileActivationResult
            {
                Applied = applied,
                RuntimePath = runtimePath,
            };
        }

        private async Task<bool> ApplyRuntimeAndSyncProxyAsync(string runtimePath, CancellationToken cancellationToken)
        {
            bool applied = await _mihomoService.ApplyConfigAsync(runtimePath, cancellationToken).ConfigureAwait(false);
            if (!applied)
            {
                if (PathsEqual(_processService.CurrentConfigPath, runtimePath))
                {
                    await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                        _systemProxyService,
                        _processService,
                        _tunService,
                        runtimePath,
                        cancellationToken).ConfigureAwait(false);
                }

                return false;
            }

            await SystemProxyRuntimePolicyHelper.ApplyForRuntimeAsync(
                _systemProxyService,
                _processService,
                _tunService,
                runtimePath,
                cancellationToken).ConfigureAwait(false);
            return true;
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

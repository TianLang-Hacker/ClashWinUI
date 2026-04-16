using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using System;

namespace ClashWinUI.Helpers
{
    public static class MihomoFailureTextHelper
    {
        public static bool TryBuildControllerFailureMessage(
            LocalizedStrings localizedStrings,
            IProcessService processService,
            IGeoDataService geoDataService,
            ITunService tunService,
            string? configPath,
            out string message)
        {
            message = string.Empty;

            MihomoFailureDiagnostic diagnostic = processService.LastFailureDiagnostic;
            GeoDataOperationResult result = geoDataService.LastResult;
            bool tunEnabled = !string.IsNullOrWhiteSpace(configPath) && tunService.IsTunEnabled(configPath);

            if (MihomoFailureKindHelper.IsTunFailure(diagnostic.Kind))
            {
                string detail = NormalizeDetail(localizedStrings, diagnostic.Message);
                message = diagnostic.Kind switch
                {
                    MihomoFailureKind.TunPermission => string.Format(localizedStrings["TunStatusPermissionFailureFormat"], detail),
                    MihomoFailureKind.TunDependency => string.Format(localizedStrings["TunStatusDependencyFailureFormat"], detail),
                    MihomoFailureKind.TunAdapterMissing => string.Format(localizedStrings["TunStatusAdapterMissingFormat"], detail),
                    MihomoFailureKind.TunRouteMissing => string.Format(localizedStrings["TunStatusRouteMissingFormat"], detail),
                    MihomoFailureKind.TunDnsUnmanaged => string.Format(localizedStrings["TunStatusDnsUnmanagedFormat"], detail),
                    MihomoFailureKind.TunFirewallBlocked => string.Format(localizedStrings["TunStatusFirewallBlockedFormat"], detail),
                    _ => string.Format(localizedStrings["TunStatusControllerFailureFormat"], detail),
                };
                return true;
            }

            if (diagnostic.Kind == MihomoFailureKind.GeoData
                || (!result.Success && result.HasRun && result.FailureKind != GeoDataFailureKind.None))
            {
                string detail = diagnostic.Kind == MihomoFailureKind.GeoData
                    ? diagnostic.Message
                    : result.Details;
                message = string.Format(
                    localizedStrings["GeoDataStatusControllerFailureFormat"],
                    NormalizeDetail(localizedStrings, detail));
                return true;
            }

            if (tunEnabled)
            {
                message = string.Format(
                    localizedStrings["TunStatusControllerFailureFormat"],
                    NormalizeDetail(localizedStrings, diagnostic.Message));
                return true;
            }

            return false;
        }

        private static string NormalizeDetail(LocalizedStrings localizedStrings, string? detail)
        {
            return string.IsNullOrWhiteSpace(detail)
                ? localizedStrings["MihomoStatusUnknownReason"]
                : detail.Trim();
        }
    }
}

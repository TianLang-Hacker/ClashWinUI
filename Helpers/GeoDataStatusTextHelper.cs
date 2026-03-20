using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using System;

namespace ClashWinUI.Helpers
{
    public static class GeoDataStatusTextHelper
    {
        public static bool TryBuildControllerFailureMessage(
            LocalizedStrings localizedStrings,
            IProcessService processService,
            IGeoDataService geoDataService,
            out string message)
        {
            message = string.Empty;

            MihomoFailureDiagnostic diagnostic = processService.LastFailureDiagnostic;
            GeoDataOperationResult result = geoDataService.LastResult;
            if (diagnostic.Kind != MihomoFailureKind.GeoData
                && (!result.HasRun || result.Success || result.FailureKind == GeoDataFailureKind.None))
            {
                return false;
            }

            string detail = diagnostic.Kind == MihomoFailureKind.GeoData
                ? diagnostic.Message
                : result.Details;
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = localizedStrings["GeoDataStatusUnknownReason"];
            }

            message = string.Format(localizedStrings["GeoDataStatusControllerFailureFormat"], detail);
            return true;
        }

        public static string BuildSettingsStatusMessage(LocalizedStrings localizedStrings, GeoDataOperationResult result)
        {
            if (!result.HasRun)
            {
                return string.Empty;
            }

            if (result.Success)
            {
                return result.OperationKind == GeoDataOperationKind.Update
                    ? localizedStrings["GeoDataStatusUpdated"]
                    : string.Empty;
            }

            string detail = string.IsNullOrWhiteSpace(result.Details)
                ? localizedStrings["GeoDataStatusUnknownReason"]
                : result.Details;
            string resourceKey = result.OperationKind == GeoDataOperationKind.Update
                ? "GeoDataStatusUpdateFailedFormat"
                : "GeoDataStatusEnsureFailedFormat";

            return string.Format(localizedStrings[resourceKey], detail);
        }
    }
}

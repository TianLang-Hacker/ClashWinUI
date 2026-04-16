using ClashWinUI.Models;
using System;

namespace ClashWinUI.Helpers
{
    public static class RuntimeNetworkStatusTextHelper
    {
        public static string BuildSystemProxyStatusText(LocalizedStrings localizedStrings, SystemProxyState state)
        {
            ArgumentNullException.ThrowIfNull(localizedStrings);
            ArgumentNullException.ThrowIfNull(state);

            if (!state.IsEnabled)
            {
                return localizedStrings["SystemProxyStatusDisabled"];
            }

            return string.IsNullOrWhiteSpace(state.ProxyServer)
                ? localizedStrings["SystemProxyStatusEnabledWithoutAddress"]
                : string.Format(localizedStrings["SystemProxyStatusEnabledFormat"], state.ProxyServer.Trim());
        }

        public static (string StatusText, string SummaryText) BuildTunPresentation(
            LocalizedStrings localizedStrings,
            TunRuntimeStatus runtimeStatus,
            SystemProxyState systemProxyState)
        {
            ArgumentNullException.ThrowIfNull(localizedStrings);
            ArgumentNullException.ThrowIfNull(runtimeStatus);
            ArgumentNullException.ThrowIfNull(systemProxyState);

            if (!runtimeStatus.IsConfigured)
            {
                return (localizedStrings["TunRuntimeStatusDisabled"], string.Empty);
            }

            string detail = NormalizeDetail(localizedStrings, runtimeStatus.Message);
            string statusText = runtimeStatus.FailureKind switch
            {
                MihomoFailureKind.TunPermission => string.Format(localizedStrings["TunStatusPermissionFailureFormat"], detail),
                MihomoFailureKind.TunDependency => string.Format(localizedStrings["TunStatusDependencyFailureFormat"], detail),
                MihomoFailureKind.TunAdapterMissing => string.Format(localizedStrings["TunStatusAdapterMissingFormat"], detail),
                MihomoFailureKind.TunRouteMissing => string.Format(localizedStrings["TunStatusRouteMissingFormat"], detail),
                MihomoFailureKind.TunDnsUnmanaged => string.Format(localizedStrings["TunStatusDnsUnmanagedFormat"], detail),
                MihomoFailureKind.TunFirewallBlocked => string.Format(localizedStrings["TunStatusFirewallBlockedFormat"], detail),
                _ when runtimeStatus.IsHealthy => localizedStrings["TunRuntimeStatusActive"],
                _ when runtimeStatus.FailureKind != MihomoFailureKind.None => string.Format(localizedStrings["TunStatusControllerFailureFormat"], detail),
                _ => localizedStrings["TunRuntimeStatusPending"],
            };

            if (runtimeStatus.IsHealthy)
            {
                return (statusText, string.Empty);
            }

            if (runtimeStatus.FailureKind == MihomoFailureKind.TunDnsUnmanaged)
            {
                return (statusText, localizedStrings["TunRuntimeSummaryDnsUnmanaged"]);
            }

            string unhealthySummaryText = systemProxyState.IsEnabled
                ? string.IsNullOrWhiteSpace(systemProxyState.ProxyServer)
                    ? localizedStrings["TunRuntimeSummaryFallbackProxyNoAddress"]
                    : string.Format(localizedStrings["TunRuntimeSummaryFallbackProxyFormat"], systemProxyState.ProxyServer.Trim())
                : localizedStrings["TunRuntimeSummaryUnprotected"];

            return (statusText, unhealthySummaryText);
        }

        private static string NormalizeDetail(LocalizedStrings localizedStrings, string? detail)
        {
            return string.IsNullOrWhiteSpace(detail)
                ? localizedStrings["MihomoStatusUnknownReason"]
                : detail.Trim();
        }
    }
}


using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using ClashWinUI.Helpers;
using ClashWinUI.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace ClashWinUI.Services.Implementations
{
    public class MihomoService : IMihomoService
    {
        private static readonly HashSet<string> GroupTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "selector",
            "urltest",
            "fallback",
            "loadbalance",
            "direct",
            "reject",
            "compatible",
        };
        private static readonly HashSet<string> AnchorGroupTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "selector",
            "urltest",
            "fallback",
            "loadbalance",
        };
        private static readonly HashSet<string> NonLeafProxyTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "selector",
            "urltest",
            "fallback",
            "loadbalance",
            "relay",
            "group",
            "direct",
            "reject",
            "pass",
            "compatible",
        };

        private readonly IAppLogService _logService;
        private readonly IGeoDataService _geoDataService;
        private readonly IKernelPathService _kernelPathService;
        private readonly IProcessService _processService;
        private readonly ITunService _tunService;
        private readonly HttpClient _httpClient;
        private readonly string _controllerBaseUrl;
        private readonly string? _controllerSecret;
        private readonly HashSet<string> _incompatibleApplyWarned = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _warnSync = new();
        private readonly Dictionary<string, DateTimeOffset> _applyFailureLoggedAt = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _applyFailureSync = new();
        private readonly object _groupCacheSync = new();
        private readonly object _connectionStatsSync = new();
        private List<ProxyGroup> _lastRuntimeGroups = new();
        private Dictionary<string, RuntimeGroupCacheEntry> _runtimeGroupCache = new(StringComparer.Ordinal);
        private Dictionary<string, ConnectionTrafficSnapshot> _connectionTrafficSnapshots = new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan ApplyFailureLogDedupeWindow = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ConfigConvergencePollDelay = TimeSpan.FromMilliseconds(250);
        private const int HotApplyConvergenceAttempts = 8;
        private const int RestartConvergenceAttempts = 24;
        private const int AnchorGroupCandidateCount = 4;
        private const double MinimumStableRuntimeCoverage = 0.80;
        private const double MinimumStableControllerCoverage = 0.35;
        private const double MinimumHotApplyAliasGroupCoverage = 0.90;
        private const double MinimumHotApplyAliasMemberCoverage = 0.90;
        private const double MinimumRestartAliasGroupCoverage = 0.95;
        private const double MinimumRestartAliasMemberCoverage = 0.98;
        private const double MinimumHotApplyAnchorStableCoverage = 0.50;
        private const double MinimumAnchorLeafRatio = 0.60;
        private const int MinimumAnchorLeafMembers = 2;

        public event EventHandler<string>? ConfigApplied;

        public MihomoService(
            IAppLogService logService,
            IGeoDataService geoDataService,
            IKernelPathService kernelPathService,
            IProcessService processService,
            ITunService tunService)
        {
            _logService = logService;
            _geoDataService = geoDataService;
            _kernelPathService = kernelPathService;
            _processService = processService;
            _tunService = tunService;
            _httpClient = new HttpClient();
            _controllerBaseUrl = Environment.GetEnvironmentVariable("MIHOMO_CONTROLLER")?.TrimEnd('/')
                ?? "http://127.0.0.1:9090";
            _controllerSecret = Environment.GetEnvironmentVariable("MIHOMO_SECRET");
        }

        public async Task<bool> ApplyConfigAsync(string configPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                _logService.Add($"Mihomo apply config skipped. File not found: {configPath}", LogLevel.Warning);
                return false;
            }

            ProfileCompatibilityStatus compatibility = EnsureProfileCompatibility(configPath);
            if (compatibility == ProfileCompatibilityStatus.Base64NotYaml)
            {
                if (TryMarkIncompatibleWarned(configPath))
                {
                    _logService.Add($"Skip ApplyConfig: profile is not Mihomo-compatible: {configPath}", LogLevel.Warning);
                }

                return false;
            }

            GeoDataOperationResult geoDataEnsureResult = await _geoDataService.EnsureGeoDataReadyAsync(cancellationToken).ConfigureAwait(false);
            if (!geoDataEnsureResult.Success)
            {
                _logService.Add($"GeoData ensure failed before apply: {geoDataEnsureResult.Details}", LogLevel.Warning);
            }

            DateTimeOffset operationStartedAt = DateTimeOffset.UtcNow;
            bool applied = ShouldRestartForTun(configPath)
                ? await ApplyConfigWithRestartAsync(configPath, cancellationToken).ConfigureAwait(false)
                : await ApplyConfigCoreAsync(configPath, cancellationToken).ConfigureAwait(false);
            if (applied)
            {
                return true;
            }

            if (ShouldAttemptGeoDataRecovery(operationStartedAt))
            {
                return await RecoverFromGeoDataFailureAsync(configPath, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        private bool ShouldRestartForTun(string configPath)
        {
            if (_tunService.IsTunEnabled(configPath))
            {
                return true;
            }

            string? currentConfigPath = _processService.CurrentConfigPath;
            return !string.IsNullOrWhiteSpace(currentConfigPath) && _tunService.IsTunEnabled(currentConfigPath);
        }

        private async Task<bool> ApplyConfigWithRestartAsync(string configPath, CancellationToken cancellationToken)
        {
            try
            {
                bool restarted = await _processService.RestartAsync(configPath, cancellationToken).ConfigureAwait(false);
                if (!restarted)
                {
                    _logService.Add($"Mihomo restart apply failed for config: {configPath}", LogLevel.Warning);
                    return false;
                }

                ControllerConvergenceResult convergence = await WaitForControllerConvergenceAsync(
                    configPath,
                    "restart",
                    RestartConvergenceAttempts,
                    cancellationToken).ConfigureAwait(false);
                if (!convergence.IsConverged)
                {
                    _logService.Add(
                        $"Mihomo controller still not converged after restart apply: {configPath}. {BuildConvergenceSummary(convergence)}",
                        LogLevel.Warning);
                    return false;
                }

                return await CompleteSuccessfulApplyAsync(
                    configPath,
                    convergence.ControllerBackedGroups,
                    $"Mihomo config applied with restart: {configPath}",
                    "restart apply",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string key = $"{configPath}|RESTART|{ex.GetType().Name}";
                if (ShouldLogApplyFailure(key))
                {
                    _logService.Add($"Mihomo restart apply unexpected error: {ex.Message}", LogLevel.Warning);
                }

                return false;
            }
        }

        private async Task<bool> ApplyConfigCoreAsync(string configPath, CancellationToken cancellationToken)
        {
            try
            {
                var payload = new MihomoApplyConfigPayload
                {
                    Path = configPath,
                    Force = true
                };
                string payloadJson = JsonSerializer.Serialize(payload, ClashJsonContext.Default.MihomoApplyConfigPayload);

                ApplyConfigRequestResult patchResult = await SendApplyConfigRequestAsync(HttpMethod.Patch, payloadJson, cancellationToken).ConfigureAwait(false);
                if (patchResult.Success)
                {
                    return await CompleteConfigApplyAsync(configPath, "hot-apply", cancellationToken).ConfigureAwait(false);
                }

                if (ShouldFallbackToPut(patchResult))
                {
                    if (ShouldLogApplyFailure($"{configPath}|PATCH_UNSUPPORTED"))
                    {
                        _logService.Add("Mihomo apply config: PATCH unsupported, fallback to PUT.", LogLevel.Info);
                    }

                    ApplyConfigRequestResult putResult = await SendApplyConfigRequestAsync(HttpMethod.Put, payloadJson, cancellationToken).ConfigureAwait(false);
                    if (putResult.Success)
                    {
                        return await CompleteConfigApplyAsync(configPath, "hot-apply", cancellationToken).ConfigureAwait(false);
                    }

                    LogApplyFailure(configPath, putResult);
                    return false;
                }

                LogApplyFailure(configPath, patchResult);
                return false;
            }
            catch (Exception ex)
            {
                string key = $"{configPath}|UNEXPECTED|{ex.GetType().Name}";
                if (ShouldLogApplyFailure(key))
                {
                    _logService.Add($"Mihomo apply config unexpected error: {ex.Message}", LogLevel.Warning);
                }

                return false;
            }
        }

        private bool ShouldAttemptGeoDataRecovery(DateTimeOffset operationStartedAt)
        {
            MihomoFailureDiagnostic diagnostic = _processService.LastFailureDiagnostic;
            return diagnostic.Kind == MihomoFailureKind.GeoData
                && diagnostic.OccurredAt >= operationStartedAt;
        }

        private async Task<bool> RecoverFromGeoDataFailureAsync(string configPath, CancellationToken cancellationToken)
        {
            MihomoFailureDiagnostic diagnostic = _processService.LastFailureDiagnostic;
            _logService.Add(
                $"GeoData issue detected during Mihomo apply. Force refresh GeoData and retry config: {configPath}. Detail={diagnostic.Message}",
                LogLevel.Warning);

            GeoDataOperationResult updateResult = await _geoDataService.UpdateGeoDataAsync(cancellationToken).ConfigureAwait(false);
            if (!updateResult.Success)
            {
                _logService.Add($"GeoData refresh failed during Mihomo apply recovery: {updateResult.Details}", LogLevel.Warning);
                return false;
            }

            bool restarted = await _processService.RestartAsync(configPath, cancellationToken).ConfigureAwait(false);
            if (!restarted)
            {
                _logService.Add($"Mihomo restart failed after GeoData refresh: {configPath}", LogLevel.Warning);
                return false;
            }

            ControllerConvergenceResult convergence = await WaitForControllerConvergenceAsync(
                configPath,
                "geo-data-retry",
                RestartConvergenceAttempts,
                cancellationToken).ConfigureAwait(false);
            if (!convergence.IsConverged)
            {
                _logService.Add(
                    $"Mihomo controller still not converged after GeoData refresh: {configPath}. {BuildConvergenceSummary(convergence)}",
                    LogLevel.Warning);
                return false;
            }

            return await CompleteSuccessfulApplyAsync(
                configPath,
                convergence.ControllerBackedGroups,
                $"Mihomo config applied after GeoData refresh: {configPath}",
                "GeoData recovery",
                cancellationToken).ConfigureAwait(false);
        }

        private bool TryMarkIncompatibleWarned(string configPath)
        {
            lock (_warnSync)
            {
                return _incompatibleApplyWarned.Add(configPath);
            }
        }

        private ProfileCompatibilityStatus EnsureProfileCompatibility(string configPath)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(configPath);
                SubscriptionContentNormalizationResult normalization = SubscriptionContentNormalizer.Normalize(raw);
                switch (normalization.Status)
                {
                    case SubscriptionContentNormalizationStatus.AlreadyYaml:
                        return ProfileCompatibilityStatus.Compatible;
                    case SubscriptionContentNormalizationStatus.DecodedFromBase64:
                        File.WriteAllBytes(configPath, normalization.Content);
                        _logService.Add($"Config normalized from Base64 to YAML before apply: {configPath}", LogLevel.Warning);
                        return ProfileCompatibilityStatus.Compatible;
                    case SubscriptionContentNormalizationStatus.Base64DecodedButNotYaml:
                        if (ShareLinkSubscriptionConverter.TryConvertToMihomoYaml(normalization.Content, out byte[] convertedYaml, out int convertedCount))
                        {
                            File.WriteAllBytes(configPath, convertedYaml);
                            _logService.Add($"Config converted from share links to Mihomo YAML before apply. Nodes={convertedCount}: {configPath}", LogLevel.Warning);
                            return ProfileCompatibilityStatus.Compatible;
                        }

                        return ProfileCompatibilityStatus.Base64NotYaml;
                    case SubscriptionContentNormalizationStatus.Base64DecodeFailed:
                        return ProfileCompatibilityStatus.Base64NotYaml;
                    case SubscriptionContentNormalizationStatus.Unrecognized:
                        if (ShareLinkSubscriptionConverter.TryConvertToMihomoYaml(normalization.Content, out byte[] convertedYamlUnknown, out int convertedCountUnknown))
                        {
                            File.WriteAllBytes(configPath, convertedYamlUnknown);
                            _logService.Add($"Config converted from share links to Mihomo YAML before apply. Nodes={convertedCountUnknown}: {configPath}", LogLevel.Warning);
                            return ProfileCompatibilityStatus.Compatible;
                        }

                        return ProfileCompatibilityStatus.Unknown;
                    default:
                        return ProfileCompatibilityStatus.Unknown;
                }
            }
            catch
            {
                return ProfileCompatibilityStatus.Unknown;
            }
        }

        private async Task<ApplyConfigRequestResult> SendApplyConfigRequestAsync(
            HttpMethod method,
            string payloadJson,
            CancellationToken cancellationToken)
        {
            try
            {
                using var request = CreateRequest(method, "/configs");
                request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return new ApplyConfigRequestResult
                    {
                        Method = method,
                        Success = true,
                    };
                }

                string body = await SafeReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
                return new ApplyConfigRequestResult
                {
                    Method = method,
                    Success = false,
                    StatusCode = response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase,
                    Body = body,
                };
            }
            catch (Exception ex)
            {
                return new ApplyConfigRequestResult
                {
                    Method = method,
                    Success = false,
                    Exception = ex,
                };
            }
        }

        private static bool ShouldFallbackToPut(ApplyConfigRequestResult patchResult)
        {
            if (patchResult.Method != HttpMethod.Patch)
            {
                return false;
            }

            if (patchResult.StatusCode is HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.NotFound
                or HttpStatusCode.NotImplemented)
            {
                return true;
            }

            string body = patchResult.Body ?? string.Empty;
            string reason = patchResult.ReasonPhrase ?? string.Empty;
            return body.Contains("method not allowed", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("method not allowed", StringComparison.OrdinalIgnoreCase);
        }

        private void LogApplyFailure(string configPath, ApplyConfigRequestResult result)
        {
            string body = NormalizeResponseBody(result.Body);
            string key = $"{configPath}|{result.Method.Method}|{(int?)result.StatusCode}|{body}";
            if (!ShouldLogApplyFailure(key))
            {
                return;
            }

            if (result.Exception is not null)
            {
                _logService.Add(
                    $"Mihomo apply config API error [{result.Method.Method}]: {result.Exception.Message}",
                    LogLevel.Warning);
                return;
            }

            _logService.Add(
                $"Mihomo apply config API failed [{result.Method.Method}] " +
                $"status={(int?)result.StatusCode} {result.ReasonPhrase}, body={body}",
                LogLevel.Warning);
        }

        private bool ShouldLogApplyFailure(string key)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (_applyFailureSync)
            {
                if (_applyFailureLoggedAt.TryGetValue(key, out DateTimeOffset lastLogAt)
                    && now - lastLogAt < ApplyFailureLogDedupeWindow)
                {
                    return false;
                }

                _applyFailureLoggedAt[key] = now;
                return true;
            }
        }

        private void ClearApplyFailureHistory(string configPath)
        {
            lock (_applyFailureSync)
            {
                if (_applyFailureLoggedAt.Count == 0)
                {
                    return;
                }

                string prefix = $"{configPath}|";
                var removeKeys = new List<string>();
                foreach (KeyValuePair<string, DateTimeOffset> item in _applyFailureLoggedAt)
                {
                    if (item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        removeKeys.Add(item.Key);
                    }
                }

                foreach (string key in removeKeys)
                {
                    _applyFailureLoggedAt.Remove(key);
                }
            }
        }

        private async Task<bool> CompleteSuccessfulApplyAsync(
            string configPath,
            IReadOnlyList<ProxyGroup> controllerBackedGroups,
            string successMessage,
            string stage,
            CancellationToken cancellationToken)
        {
            if (!await ValidateTunRuntimeOrMarkFailureAsync(configPath, stage, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            CacheRuntimeGroups(controllerBackedGroups);
            ClearApplyFailureHistory(configPath);
            _processService.ResetFailureDiagnostic();
            _logService.Add(successMessage);
            ConfigApplied?.Invoke(this, configPath);
            return true;
        }

        private async Task<bool> ValidateTunRuntimeOrMarkFailureAsync(string configPath, string stage, CancellationToken cancellationToken)
        {
            TunRuntimeValidationOutcome validation = await TunRuntimeValidationHelper.ValidateAsync(
                _tunService,
                _kernelPathService,
                _processService,
                configPath,
                cancellationToken).ConfigureAwait(false);
            if (validation.Success)
            {
                return true;
            }

            _processService.UpdateFailureDiagnostic(validation.FailureKind, validation.Message);
            _logService.Add(
                $"Mihomo controller converged, but TUN runtime is unhealthy after {stage}: {validation.Message}",
                LogLevel.Warning);
            return false;
        }

        private async Task<bool> CompleteConfigApplyAsync(string configPath, string stage, CancellationToken cancellationToken)
        {
            ControllerConvergenceResult convergence = await WaitForControllerConvergenceAsync(
                configPath,
                stage,
                HotApplyConvergenceAttempts,
                cancellationToken).ConfigureAwait(false);
            if (convergence.IsConverged)
            {
                return await CompleteSuccessfulApplyAsync(
                    configPath,
                    convergence.ControllerBackedGroups,
                    $"Mihomo config applied: {configPath}",
                    $"{stage} hot apply",
                    cancellationToken).ConfigureAwait(false);
            }

            _logService.Add(
                $"Mihomo hot apply not converged. Restart Mihomo with target runtime: {configPath}. {BuildConvergenceSummary(convergence)}",
                LogLevel.Warning);

            bool restarted = await _processService.RestartAsync(configPath, cancellationToken).ConfigureAwait(false);
            if (!restarted)
            {
                _logService.Add($"Mihomo restart fallback failed for config: {configPath}", LogLevel.Warning);
                return false;
            }

            ControllerConvergenceResult restartedConvergence = await WaitForControllerConvergenceAsync(
                configPath,
                "restart",
                RestartConvergenceAttempts,
                cancellationToken).ConfigureAwait(false);
            if (!restartedConvergence.IsConverged)
            {
                _logService.Add(
                    $"Mihomo controller still not converged after restart: {configPath}. {BuildConvergenceSummary(restartedConvergence)}",
                    LogLevel.Warning);
                return false;
            }

            return await CompleteSuccessfulApplyAsync(
                configPath,
                restartedConvergence.ControllerBackedGroups,
                $"Mihomo config applied after restart fallback: {configPath}",
                "restart fallback",
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<ControllerConvergenceResult> WaitForControllerConvergenceAsync(
            string configPath,
            string stage,
            int attempts,
            CancellationToken cancellationToken)
        {
            List<ProxyGroup> runtimeGroups = ProxyGroupParser.ParseFromFile(configPath)
                .Select(CloneProxyGroup)
                .ToList();
            CacheRuntimeGroups(runtimeGroups);

            ControllerConvergenceResult? lastResult = null;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                using JsonDocument? document = await GetProxiesDocumentAsync(cancellationToken, suppressErrors: true).ConfigureAwait(false);
                if (document is not null
                    && document.RootElement.TryGetProperty("proxies", out JsonElement proxiesElement)
                    && proxiesElement.ValueKind == JsonValueKind.Object)
                {
                    ControllerConvergenceResult currentResult = EvaluateControllerConvergence(runtimeGroups, proxiesElement, stage);
                    lastResult = currentResult;

                    if (currentResult.IsConverged)
                    {
                        _logService.Add(
                            $"Mihomo controller converged after {stage}. attempt={attempt}/{attempts}. {BuildConvergenceSummary(currentResult)}",
                            LogLevel.Info);
                        return currentResult;
                    }
                }

                if (attempt < attempts)
                {
                    await Task.Delay(ConfigConvergencePollDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            lastResult ??= ControllerConvergenceResult.Empty(runtimeGroups.Count);
            _logService.Add(
                $"Mihomo controller not converged after {stage}. {BuildConvergenceSummary(lastResult)}",
                LogLevel.Info);
            return lastResult;
        }

        private ControllerConvergenceResult EvaluateControllerConvergence(List<ProxyGroup> runtimeGroups, JsonElement proxiesElement, string stage)
        {
            bool isRestartStage = string.Equals(stage, "restart", StringComparison.OrdinalIgnoreCase);
            List<ProxyGroup> comparableGroups = runtimeGroups
                .Where(group => group.Members.Count > 0)
                .Select(CloneProxyGroup)
                .ToList();

            if (comparableGroups.Count == 0)
            {
                bool hasControllerGroups = EnumerateControllerGroups(proxiesElement).Any();
                return new ControllerConvergenceResult
                {
                    IsConverged = hasControllerGroups,
                    RuntimeGroupCount = 0,
                    ControllerGroupCount = EnumerateControllerGroups(proxiesElement).Count(),
                    AliasMatchedGroupCount = 0,
                    AliasMatchedMemberCount = 0,
                    TotalMemberCount = 0,
                    AnchorGroupCount = 0,
                    StableAnchorGroupCount = 0,
                    IgnoredGroupCount = 0,
                    AliasCoverageSatisfied = hasControllerGroups,
                    AnchorCoverageSatisfied = true,
                    Decision = hasControllerGroups ? ConvergenceDecision.HardConverged : ConvergenceDecision.NotConverged,
                    ControllerBackedGroups = runtimeGroups.Select(CloneProxyGroup).ToList(),
                    AnchorGroupPreview = "<none>",
                };
            }

            List<ProxyGroup> mergedGroups = MergeProxyGroups(
                comparableGroups.Select(CloneProxyGroup).ToList(),
                proxiesElement);

            int controllerGroupCount = EnumerateControllerGroups(proxiesElement).Count();
            int totalMembers = comparableGroups.Sum(group => group.Members.Count);
            int aliasMatchedGroupCount = mergedGroups.Count(group =>
                !string.IsNullOrWhiteSpace(group.ControllerName));
            int aliasMatchedMemberCount = mergedGroups.Sum(group =>
                group.Members.Count(member => !string.IsNullOrWhiteSpace(member.Node.ControllerName)));

            List<ProxyGroup> anchorGroups = GetAnchorGroups(comparableGroups)
                .Take(Math.Min(AnchorGroupCandidateCount, comparableGroups.Count))
                .ToList();

            var anchorGroupSummaries = new List<string>();
            int stableAnchorGroupCount = 0;

            foreach (ProxyGroup anchorGroup in anchorGroups)
            {
                GroupStructureMatch? best = GetBestGroupStructureMatch(anchorGroup, proxiesElement, out _);
                bool isStable = best is not null
                    && (best.ExactSetMatch
                        || (best.RuntimeCoverage >= MinimumStableRuntimeCoverage
                            && best.ControllerCoverage >= MinimumStableControllerCoverage));
                if (isStable)
                {
                    stableAnchorGroupCount++;
                }

                anchorGroupSummaries.Add(best is null
                    ? $"{anchorGroup.Name}:<none>"
                    : $"{anchorGroup.Name}->{best.ControllerGroupName}<{best.ControllerGroupType}>:runtime={best.RuntimeCoverage:F2},controller={best.ControllerCoverage:F2},exact={best.ExactSetMatch},current={best.CurrentMatches}");
            }

            double aliasGroupCoverage = comparableGroups.Count == 0
                ? 1
                : aliasMatchedGroupCount / (double)comparableGroups.Count;
            double aliasMemberCoverage = totalMembers == 0
                ? 1
                : aliasMatchedMemberCount / (double)totalMembers;
            double anchorStableCoverage = anchorGroups.Count == 0
                ? 1
                : stableAnchorGroupCount / (double)anchorGroups.Count;

            bool aliasCoverageSatisfied = aliasGroupCoverage >= (isRestartStage
                    ? MinimumRestartAliasGroupCoverage
                    : MinimumHotApplyAliasGroupCoverage)
                && aliasMemberCoverage >= (isRestartStage
                    ? MinimumRestartAliasMemberCoverage
                    : MinimumHotApplyAliasMemberCoverage);
            bool anchorCoverageSatisfied = anchorGroups.Count == 0
                || (stableAnchorGroupCount > 0 && (!isRestartStage || anchorStableCoverage >= 0)
                    && (isRestartStage || anchorStableCoverage >= MinimumHotApplyAnchorStableCoverage));

            ConvergenceDecision decision = ConvergenceDecision.NotConverged;
            if (aliasCoverageSatisfied && anchorCoverageSatisfied)
            {
                decision = isRestartStage
                    ? ConvergenceDecision.SoftConvergedAfterRestart
                    : ConvergenceDecision.HardConverged;
            }

            bool isConverged = decision != ConvergenceDecision.NotConverged;

            return new ControllerConvergenceResult
            {
                IsConverged = isConverged,
                RuntimeGroupCount = comparableGroups.Count,
                ControllerGroupCount = controllerGroupCount,
                AliasMatchedGroupCount = aliasMatchedGroupCount,
                AliasMatchedMemberCount = aliasMatchedMemberCount,
                TotalMemberCount = totalMembers,
                AnchorGroupCount = anchorGroups.Count,
                StableAnchorGroupCount = stableAnchorGroupCount,
                IgnoredGroupCount = Math.Max(0, comparableGroups.Count - anchorGroups.Count),
                AliasCoverageSatisfied = aliasCoverageSatisfied,
                AnchorCoverageSatisfied = anchorCoverageSatisfied,
                Decision = decision,
                ControllerBackedGroups = mergedGroups,
                AnchorGroupPreview = anchorGroupSummaries.Count == 0 ? "<none>" : string.Join(" | ", anchorGroupSummaries),
            };
        }

        private static string BuildConvergenceSummary(ControllerConvergenceResult result)
        {
            double aliasGroupCoverage = result.RuntimeGroupCount == 0
                ? 1
                : result.AliasMatchedGroupCount / (double)result.RuntimeGroupCount;
            double aliasMemberCoverage = result.TotalMemberCount == 0
                ? 1
                : result.AliasMatchedMemberCount / (double)result.TotalMemberCount;

            return
                $"runtimeGroups={result.RuntimeGroupCount}, controllerGroups={result.ControllerGroupCount}, " +
                $"decision={result.DecisionLabel}, " +
                $"aliasCoverageSatisfied={result.AliasCoverageSatisfied}, anchorGroupsStable={result.AnchorCoverageSatisfied}, " +
                $"aliasGroups={result.AliasMatchedGroupCount}/{result.RuntimeGroupCount}({aliasGroupCoverage:F2}), " +
                $"aliasMembers={result.AliasMatchedMemberCount}/{result.TotalMemberCount}({aliasMemberCoverage:F2}), " +
                $"stableAnchorGroups={result.StableAnchorGroupCount}/{result.AnchorGroupCount}, ignoredProviderLikeGroups={result.IgnoredGroupCount}, " +
                $"anchorGroups={result.AnchorGroupPreview}";
        }

        private static IEnumerable<ProxyGroup> GetAnchorGroups(IEnumerable<ProxyGroup> groups)
        {
            return groups
                .Where(group => AnchorGroupTypes.Contains(NormalizeGroupType(group.Type)))
                .Select(group => new
                {
                    Group = group,
                    LeafCount = group.Members.Count(member => IsLeafProxyNode(member.Node)),
                    LeafRatio = group.Members.Count == 0
                        ? 0
                        : group.Members.Count(member => IsLeafProxyNode(member.Node)) / (double)group.Members.Count,
                })
                .Where(candidate => candidate.LeafCount >= MinimumAnchorLeafMembers
                    && candidate.LeafRatio >= MinimumAnchorLeafRatio)
                .OrderByDescending(candidate => candidate.LeafCount)
                .ThenByDescending(candidate => candidate.LeafRatio)
                .ThenByDescending(candidate => candidate.Group.Members.Count)
                .Select(candidate => candidate.Group);
        }

        private static bool IsLeafProxyNode(ProxyNode node)
        {
            string type = NormalizeProxyType(node.Type);
            return !NonLeafProxyTypes.Contains(type);
        }

        private static async Task<string> SafeReadResponseBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeResponseBody(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return "<empty>";
            }

            string normalized = body.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();

            const int maxLength = 220;
            if (normalized.Length > maxLength)
            {
                return normalized[..maxLength] + "...";
            }

            return normalized;
        }

        public async Task<ProxyGroupLoadResult> GetProxyGroupsAsync(string runtimePath, CancellationToken cancellationToken = default)
        {
            List<ProxyGroup> runtimeGroups = GetOrLoadRuntimeGroups(runtimePath);
            CacheRuntimeGroups(runtimeGroups);

            try
            {
                using JsonDocument? document = await GetProxiesDocumentAsync(cancellationToken).ConfigureAwait(false);
                if (document is null
                    || !document.RootElement.TryGetProperty("proxies", out JsonElement proxiesElement)
                    || proxiesElement.ValueKind != JsonValueKind.Object)
                {
                    return new ProxyGroupLoadResult
                    {
                        Groups = runtimeGroups,
                        Source = ProxyGroupLoadSource.RuntimeFile,
                    };
                }

                List<ProxyGroup> mergedGroups = MergeProxyGroups(runtimeGroups, proxiesElement);
                return new ProxyGroupLoadResult
                {
                    Groups = mergedGroups,
                    Source = ProxyGroupLoadSource.MihomoController,
                };
            }
            catch (Exception ex)
            {
                _logService.Add($"Mihomo proxy groups request error: {ex.Message}", LogLevel.Warning);
                return new ProxyGroupLoadResult
                {
                    Groups = runtimeGroups,
                    Source = ProxyGroupLoadSource.RuntimeFile,
                };
            }
        }

        public async Task<IReadOnlyList<ProxyNode>> GetProxiesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using JsonDocument? document = await GetProxiesDocumentAsync(cancellationToken).ConfigureAwait(false);
                if (document is null
                    || !document.RootElement.TryGetProperty("proxies", out JsonElement proxiesElement)
                    || proxiesElement.ValueKind != JsonValueKind.Object)
                {
                    return Array.Empty<ProxyNode>();
                }

                var nodes = new List<ProxyNode>();
                foreach (JsonProperty proxy in proxiesElement.EnumerateObject())
                {
                    if (proxy.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string type = proxy.Value.TryGetProperty("type", out JsonElement typeElement)
                        ? typeElement.GetString() ?? "unknown"
                        : "unknown";

                    if (IsGroupType(type))
                    {
                        continue;
                    }

                    nodes.Add(new ProxyNode
                    {
                        Name = proxy.Name,
                        Type = type,
                    });
                }

                return nodes;
            }
            catch (Exception ex)
            {
                _logService.Add($"Mihomo proxies request error: {ex.Message}", LogLevel.Warning);
                return Array.Empty<ProxyNode>();
            }
        }

        public async Task<bool> SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(proxyName))
            {
                return false;
            }

            try
            {
                ProxySelectionAttemptResult initialAttempt = await SendSelectProxyRequestAsync(groupName, proxyName, cancellationToken).ConfigureAwait(false);
                if (initialAttempt.Success)
                {
                    _logService.Add($"Mihomo proxy selected: {groupName} -> {proxyName}");
                    return true;
                }

                if (initialAttempt.StatusCode == HttpStatusCode.NotFound)
                {
                    ProxySelectionResolution? resolution = await ResolveSelectionByStructureAsync(groupName, proxyName, cancellationToken).ConfigureAwait(false);
                    if (resolution is not null
                        && (!string.Equals(resolution.GroupName, groupName, StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(resolution.ProxyName, proxyName, StringComparison.OrdinalIgnoreCase)))
                    {
                        ProxySelectionAttemptResult retriedAttempt = await SendSelectProxyRequestAsync(resolution.GroupName, resolution.ProxyName, cancellationToken).ConfigureAwait(false);
                        if (retriedAttempt.Success)
                        {
                            _logService.Add(
                                $"Mihomo proxy selected after controller alias resolution: {groupName} -> {proxyName} => {resolution.GroupName} -> {resolution.ProxyName}");
                            return true;
                        }

                        initialAttempt = retriedAttempt;
                        groupName = resolution.GroupName;
                        proxyName = resolution.ProxyName;
                    }
                }

                _logService.Add(
                    $"Mihomo proxy select failed: group={groupName}, proxy={proxyName}, status={(int?)initialAttempt.StatusCode} {initialAttempt.ReasonPhrase}, body={NormalizeResponseBody(initialAttempt.Body)}",
                    LogLevel.Warning);
                return false;
            }
            catch (Exception ex)
            {
                _logService.Add($"Mihomo proxy select error: {ex.Message}", LogLevel.Warning);
                return false;
            }
        }

        public async Task<int?> TestProxyDelayAsync(string proxyName, string testUrl, int timeoutMilliseconds = 5000, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(proxyName) || string.IsNullOrWhiteSpace(testUrl))
            {
                return null;
            }

            string encodedProxyName = Uri.EscapeDataString(proxyName);
            string encodedUrl = Uri.EscapeDataString(testUrl);
            string path = $"/proxies/{encodedProxyName}/delay?url={encodedUrl}&timeout={timeoutMilliseconds}";

            try
            {
                using var request = CreateRequest(HttpMethod.Get, path);
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logService.Add($"Mihomo delay test failed for {proxyName}: {(int)response.StatusCode} {response.ReasonPhrase}", LogLevel.Warning);
                    return null;
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!doc.RootElement.TryGetProperty("delay", out JsonElement delayElement))
                {
                    return null;
                }

                int delay = delayElement.GetInt32();
                return delay >= 0 ? delay : null;
            }
            catch (Exception ex)
            {
                _logService.Add($"Mihomo delay test error for {proxyName}: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        public async Task<IReadOnlyList<ConnectionEntry>> GetConnectionsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, "/connections");
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logService.Add($"Mihomo connections request failed: {(int)response.StatusCode} {response.ReasonPhrase}", LogLevel.Warning);
                    return Array.Empty<ConnectionEntry>();
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("connections", out JsonElement connectionsElement)
                    || connectionsElement.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<ConnectionEntry>();
                }

                var rawConnections = new List<ConnectionEntry>();
                foreach (JsonElement item in connectionsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    rawConnections.Add(ParseConnectionEntry(item));
                }

                return ComputeConnectionSpeeds(rawConnections);
            }
            catch (Exception ex)
            {
                _logService.Add($"Mihomo connections request error: {ex.Message}", LogLevel.Warning);
                return Array.Empty<ConnectionEntry>();
            }
        }

        public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, "/version");
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logService.Add($"Mihomo version request failed: {(int)response.StatusCode} {response.ReasonPhrase}", LogLevel.Warning);
                    return null;
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    return root.GetString();
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (root.TryGetProperty("version", out JsonElement versionElement) && versionElement.ValueKind == JsonValueKind.String)
                {
                    return versionElement.GetString();
                }

                if (root.TryGetProperty("meta", out JsonElement metaElement)
                    && metaElement.ValueKind == JsonValueKind.Object
                    && metaElement.TryGetProperty("version", out JsonElement nestedVersion)
                    && nestedVersion.ValueKind == JsonValueKind.String)
                {
                    return nestedVersion.GetString();
                }

                return null;
            }
            catch (Exception ex)
            {
                _logService.Add($"Mihomo version request error: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        public async Task<bool> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return false;
            }

            try
            {
                using var request = CreateRequest(HttpMethod.Delete, $"/connections/{Uri.EscapeDataString(connectionId)}");
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                string body = await SafeReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
                _logService.Add(
                    $"Mihomo close connection failed: id={connectionId}, status={(int)response.StatusCode} {response.ReasonPhrase}, body={NormalizeResponseBody(body)}",
                    LogLevel.Warning);
                return false;
            }
            catch (Exception ex)
            {
                _logService.Add($"Mihomo close connection error: id={connectionId}, error={ex.Message}", LogLevel.Warning);
                return false;
            }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
        {
            var request = new HttpRequestMessage(method, $"{_controllerBaseUrl}{relativePath}");
            if (!string.IsNullOrWhiteSpace(_controllerSecret))
            {
                request.Headers.Add("Authorization", $"Bearer {_controllerSecret}");
            }

            return request;
        }

        private static ConnectionEntry ParseConnectionEntry(JsonElement connectionElement)
        {
            JsonElement metadataElement = TryGetObject(connectionElement, "metadata");
            return new ConnectionEntry
            {
                Id = TryGetString(connectionElement, "id"),
                HostDisplay = BuildConnectionHostDisplay(metadataElement),
                TypeDisplay = BuildConnectionTypeDisplay(metadataElement),
                RuleDisplay = BuildConnectionRuleDisplay(connectionElement, metadataElement),
                ChainDisplay = BuildConnectionChainDisplay(connectionElement, metadataElement),
                DownloadSpeed = 0,
                UploadSpeed = 0,
                Download = TryGetInt64(connectionElement, "download"),
                Upload = TryGetInt64(connectionElement, "upload"),
                StartedAt = TryGetDateTimeOffset(connectionElement, "start"),
            };
        }

        private IReadOnlyList<ConnectionEntry> ComputeConnectionSpeeds(IReadOnlyList<ConnectionEntry> connections)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var computedConnections = new List<ConnectionEntry>(connections.Count);
            var nextSnapshots = new Dictionary<string, ConnectionTrafficSnapshot>(StringComparer.OrdinalIgnoreCase);

            lock (_connectionStatsSync)
            {
                foreach (ConnectionEntry connection in connections)
                {
                    long downloadSpeed = 0;
                    long uploadSpeed = 0;

                    if (_connectionTrafficSnapshots.TryGetValue(connection.Id, out ConnectionTrafficSnapshot? previous))
                    {
                        double elapsedSeconds = (now - previous.Timestamp).TotalSeconds;
                        if (elapsedSeconds > 0)
                        {
                            long downloadDelta = Math.Max(0, connection.Download - previous.Download);
                            long uploadDelta = Math.Max(0, connection.Upload - previous.Upload);
                            downloadSpeed = (long)Math.Max(0, Math.Round(downloadDelta / elapsedSeconds));
                            uploadSpeed = (long)Math.Max(0, Math.Round(uploadDelta / elapsedSeconds));
                        }
                    }

                    computedConnections.Add(new ConnectionEntry
                    {
                        Id = connection.Id,
                        HostDisplay = connection.HostDisplay,
                        TypeDisplay = connection.TypeDisplay,
                        RuleDisplay = connection.RuleDisplay,
                        ChainDisplay = connection.ChainDisplay,
                        DownloadSpeed = downloadSpeed,
                        UploadSpeed = uploadSpeed,
                        Download = connection.Download,
                        Upload = connection.Upload,
                        StartedAt = connection.StartedAt,
                    });

                    nextSnapshots[connection.Id] = new ConnectionTrafficSnapshot
                    {
                        Timestamp = now,
                        Download = connection.Download,
                        Upload = connection.Upload,
                    };
                }

                _connectionTrafficSnapshots = nextSnapshots;
            }

            return computedConnections;
        }

        private static string BuildConnectionHostDisplay(JsonElement metadataElement)
        {
            string host = FirstNonEmpty(
                TryGetString(metadataElement, "host"),
                TryGetString(metadataElement, "destinationIP"),
                TryGetString(metadataElement, "remoteDestination"),
                TryGetString(metadataElement, "sniffHost"));
            string port = FirstNonEmpty(
                TryGetString(metadataElement, "destinationPort"),
                TryGetString(metadataElement, "dstPort"));

            if (string.IsNullOrWhiteSpace(host))
            {
                host = "--";
            }

            if (string.IsNullOrWhiteSpace(port) || host.EndsWith($":{port}", StringComparison.OrdinalIgnoreCase))
            {
                return host;
            }

            return $"{host}:{port}";
        }

        private static string BuildConnectionTypeDisplay(JsonElement metadataElement)
        {
            string connectionType = NormalizeConnectionType(TryGetString(metadataElement, "type"));
            string network = NormalizeConnectionNetwork(TryGetString(metadataElement, "network"));

            if (string.IsNullOrWhiteSpace(connectionType))
            {
                return string.IsNullOrWhiteSpace(network) ? "--" : network;
            }

            return string.IsNullOrWhiteSpace(network)
                ? connectionType
                : $"{connectionType} | {network}";
        }

        private static string BuildConnectionRuleDisplay(JsonElement connectionElement, JsonElement metadataElement)
        {
            return FirstNonEmpty(
                TryGetString(connectionElement, "rule"),
                TryGetString(metadataElement, "specialRules"),
                TryGetString(metadataElement, "rulePayload"),
                "--");
        }

        private static string BuildConnectionChainDisplay(JsonElement connectionElement, JsonElement metadataElement)
        {
            IReadOnlyList<string> chains = TryGetStringArray(connectionElement, "chains");
            if (chains.Count == 0)
            {
                chains = TryGetStringArray(metadataElement, "chain");
            }

            if (chains.Count == 0)
            {
                string singleChain = FirstNonEmpty(
                    TryGetString(metadataElement, "specialProxy"),
                    TryGetString(metadataElement, "proxy"),
                    "--");
                return singleChain;
            }

            return string.Join(" -> ", chains);
        }

        private static JsonElement TryGetObject(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Object)
                {
                    return property.Value;
                }
            }

            return default;
        }

        private List<ProxyGroup> MergeProxyGroups(List<ProxyGroup> runtimeGroups, JsonElement proxiesElement)
        {
            Dictionary<string, ProxyNode> nodeLookup = BuildSharedNodeLookup(runtimeGroups);
            UpdateNodesFromApi(nodeLookup, proxiesElement);

            if (runtimeGroups.Count == 0)
            {
                runtimeGroups = BuildGroupsFromApi(proxiesElement, nodeLookup);
            }

            foreach (ProxyGroup group in runtimeGroups)
            {
                if (TryResolveApiGroup(group, proxiesElement, out string controllerGroupName, out JsonElement groupElement))
                {
                    group.ControllerName = controllerGroupName;

                    string currentProxy = TryGetString(groupElement, "now");
                    if (!string.IsNullOrWhiteSpace(currentProxy))
                    {
                        group.SetCurrentProxy(ResolveDisplayNodeName(group, currentProxy));
                    }

                    string apiType = NormalizeGroupType(TryGetString(groupElement, "type"));
                    if (!string.IsNullOrWhiteSpace(apiType))
                    {
                        group.Type = apiType;
                    }

                    if (group.Members.Count == 0)
                    {
                        PopulateMembersFromApi(group, groupElement, nodeLookup);
                    }
                    else
                    {
                        BindMemberControllerNames(group, groupElement, nodeLookup);
                    }
                }
                else if (group.Members.Count > 0)
                {
                    group.SetCurrentProxy(group.CurrentProxyName);
                }
            }

            return runtimeGroups;
        }

        private static Dictionary<string, ProxyNode> BuildSharedNodeLookup(IEnumerable<ProxyGroup> groups)
        {
            var lookup = new Dictionary<string, ProxyNode>(StringComparer.OrdinalIgnoreCase);
            foreach (ProxyNode node in groups
                .SelectMany(group => group.Members)
                .Select(member => member.Node))
            {
                lookup[node.Name] = node;
            }

            return lookup;
        }

        private void UpdateNodesFromApi(Dictionary<string, ProxyNode> nodeLookup, JsonElement proxiesElement)
        {
            foreach (JsonProperty proxy in proxiesElement.EnumerateObject())
            {
                if (proxy.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                ProxyNode? node = FindMatchingNode(nodeLookup, proxy.Name);
                if (node is null)
                {
                    continue;
                }

                node.ControllerName = proxy.Name;

                string type = NormalizeProxyType(TryGetString(proxy.Value, "type"));
                if (!string.IsNullOrWhiteSpace(type) && !IsGroupType(type))
                {
                    node.Type = type;
                }

                if (proxy.Value.TryGetProperty("udp", out JsonElement udpElement)
                    && udpElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    node.SupportsUdp = udpElement.GetBoolean();
                }

                string network = TryGetString(proxy.Value, "network");
                if (!string.IsNullOrWhiteSpace(network))
                {
                    node.Network = network;
                    node.TransportText = GetTransportText(network);
                }
            }
        }

        private List<ProxyGroup> BuildGroupsFromApi(JsonElement proxiesElement, Dictionary<string, ProxyNode> nodeLookup)
        {
            var groups = new List<ProxyGroup>();

            foreach (JsonProperty proxy in proxiesElement.EnumerateObject())
            {
                if (proxy.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string type = NormalizeGroupType(TryGetString(proxy.Value, "type"));
                if (!IsGroupType(type))
                {
                    continue;
                }

                var group = new ProxyGroup
                {
                    Name = NormalizeDisplayName(proxy.Name),
                    ControllerName = proxy.Name,
                    Type = type,
                };

                PopulateMembersFromApi(group, proxy.Value, nodeLookup);
                group.SetCurrentProxy(ResolveDisplayNodeName(group, TryGetString(proxy.Value, "now")));
                groups.Add(group);
            }

            return groups;
        }

        private void PopulateMembersFromApi(ProxyGroup group, JsonElement groupElement, Dictionary<string, ProxyNode> nodeLookup)
        {
            if (!groupElement.TryGetProperty("all", out JsonElement allElement)
                || allElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            group.Members.Clear();
            foreach (JsonElement item in allElement.EnumerateArray())
            {
                string memberName = item.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(memberName))
                {
                    continue;
                }

                ProxyNode? node = FindMatchingNode(nodeLookup, memberName);
                if (node is null)
                {
                    node = new ProxyNode
                    {
                        Name = NormalizeDisplayName(memberName),
                        ControllerName = memberName,
                        Type = NormalizeProxyType(memberName),
                        TransportText = string.Empty,
                    };
                    nodeLookup[node.Name] = node;
                }
                else if (string.IsNullOrWhiteSpace(node.ControllerName))
                {
                    node.ControllerName = memberName;
                }

                group.Members.Add(new ProxyGroupMember
                {
                    GroupName = group.Name,
                    Node = node,
                });
            }
        }

        private void BindMemberControllerNames(ProxyGroup group, JsonElement groupElement, Dictionary<string, ProxyNode> nodeLookup)
        {
            if (!groupElement.TryGetProperty("all", out JsonElement allElement)
                || allElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement item in allElement.EnumerateArray())
            {
                string memberName = item.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(memberName))
                {
                    continue;
                }

                ProxyGroupMember? member = group.Members.FirstOrDefault(current =>
                    NamesMatch(current.Node.Name, memberName)
                    || (!string.IsNullOrWhiteSpace(current.Node.ControllerName)
                        && NamesMatch(current.Node.ControllerName, memberName)));
                if (member is null)
                {
                    continue;
                }

                member.Node.ControllerName = memberName;
                nodeLookup[member.Node.Name] = member.Node;
            }
        }

        private bool TryResolveApiGroup(ProxyGroup runtimeGroup, JsonElement proxiesElement, out string controllerGroupName, out JsonElement proxyElement)
        {
            foreach (JsonProperty proxy in EnumerateControllerGroups(proxiesElement))
            {
                if (!string.IsNullOrWhiteSpace(runtimeGroup.ControllerName)
                    && string.Equals(proxy.Name, runtimeGroup.ControllerName, StringComparison.OrdinalIgnoreCase))
                {
                    controllerGroupName = proxy.Name;
                    proxyElement = proxy.Value;
                    return true;
                }
            }

            foreach (JsonProperty proxy in EnumerateControllerGroups(proxiesElement))
            {
                if (string.Equals(proxy.Name, runtimeGroup.Name, StringComparison.OrdinalIgnoreCase))
                {
                    controllerGroupName = proxy.Name;
                    proxyElement = proxy.Value;
                    return true;
                }
            }

            foreach (JsonProperty proxy in EnumerateControllerGroups(proxiesElement))
            {
                if (NamesMatch(proxy.Name, runtimeGroup.Name))
                {
                    controllerGroupName = proxy.Name;
                    proxyElement = proxy.Value;
                    return true;
                }
            }

            if (TryResolveApiGroupByStructure(runtimeGroup, proxiesElement, out controllerGroupName, out proxyElement))
            {
                return true;
            }

            controllerGroupName = string.Empty;
            proxyElement = default;
            return false;
        }

        private static string TryGetString(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                        JsonValueKind.Number => property.Value.GetRawText(),
                        JsonValueKind.True => bool.TrueString,
                        JsonValueKind.False => bool.FalseString,
                        _ => string.Empty,
                    };
                }
            }

            return string.Empty;
        }

        private static long TryGetInt64(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out long numericValue))
                {
                    return numericValue;
                }

                if (property.Value.ValueKind == JsonValueKind.String
                    && long.TryParse(property.Value.GetString(), out long parsedValue))
                {
                    return parsedValue;
                }
            }

            return 0;
        }

        private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
        {
            string value = TryGetString(element, propertyName);
            return DateTimeOffset.TryParse(value, out DateTimeOffset parsedValue) ? parsedValue : null;
        }

        private static IReadOnlyList<string> TryGetStringArray(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    return property.Value.EnumerateArray()
                        .Select(item => item.GetString() ?? string.Empty)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToList();
                }

                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    string singleValue = property.Value.GetString() ?? string.Empty;
                    return string.IsNullOrWhiteSpace(singleValue)
                        ? Array.Empty<string>()
                        : [singleValue];
                }
            }

            return Array.Empty<string>();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static string NormalizeConnectionType(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            string trimmed = raw.Trim();
            return trimmed.ToLowerInvariant() switch
            {
                "http" => "HTTP",
                "https" => "HTTPS",
                "socks5" => "Socks5",
                "socks" => "Socks5",
                "redir" => "Redir",
                "tproxy" => "TProxy",
                "tun" => "TUN",
                _ when trimmed.Length <= 4 => trimmed.ToUpperInvariant(),
                _ => char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant(),
            };
        }

        private static string NormalizeConnectionNetwork(string? raw)
        {
            return string.IsNullOrWhiteSpace(raw)
                ? string.Empty
                : raw.Trim().ToUpperInvariant();
        }

        private static string GetTransportText(string network)
        {
            return string.IsNullOrWhiteSpace(network)
                ? string.Empty
                : network.Trim();
        }

        private static ProxyNode? FindMatchingNode(Dictionary<string, ProxyNode> nodeLookup, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (nodeLookup.TryGetValue(name, out ProxyNode? exact))
            {
                return exact;
            }

            return nodeLookup.Values.FirstOrDefault(node =>
                NamesMatch(node.Name, name)
                || (!string.IsNullOrWhiteSpace(node.ControllerName) && NamesMatch(node.ControllerName, name)));
        }

        private static string ResolveDisplayNodeName(ProxyGroup group, string? controllerNodeName)
        {
            if (string.IsNullOrWhiteSpace(controllerNodeName))
            {
                return string.Empty;
            }

            ProxyGroupMember? matchedMember = group.Members.FirstOrDefault(member =>
                NamesMatch(member.Node.Name, controllerNodeName)
                || (!string.IsNullOrWhiteSpace(member.Node.ControllerName)
                    && NamesMatch(member.Node.ControllerName, controllerNodeName)));

            return matchedMember?.Node.Name ?? NormalizeDisplayName(controllerNodeName);
        }

        private static string NormalizeDisplayName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim();
            string repaired = TryDecodeUtf8Latin1Mojibake(trimmed);
            return string.IsNullOrWhiteSpace(repaired) ? trimmed : repaired;
        }

        private static bool NamesMatch(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            HashSet<string> rightCandidates = GetNameCandidates(right);
            return GetNameCandidates(left).Any(rightCandidates.Contains);
        }

        private static HashSet<string> GetNameCandidates(string value)
        {
            string trimmed = value.Trim();
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                trimmed,
            };

            string repaired = TryDecodeUtf8Latin1Mojibake(trimmed);
            if (!string.IsNullOrWhiteSpace(repaired))
            {
                candidates.Add(repaired.Trim());
            }

            return candidates;
        }

        private static string TryDecodeUtf8Latin1Mojibake(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (value.Any(ch => ch > byte.MaxValue))
            {
                return value;
            }

            byte[] bytes = value.Select(ch => (byte)ch).ToArray();
            string decoded = Encoding.UTF8.GetString(bytes);
            return decoded.Contains('\uFFFD') ? value : decoded;
        }

        private static bool IsGroupType(string? type)
        {
            return !string.IsNullOrWhiteSpace(type)
                && GroupTypes.Contains(type);
        }

        private static string NormalizeProxyType(string? raw)
        {
            return string.IsNullOrWhiteSpace(raw)
                ? "unknown"
                : raw.Trim().ToLowerInvariant();
        }

        private static string NormalizeGroupType(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            string normalized = raw.Trim()
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();

            return normalized switch
            {
                "select" => "selector",
                "selector" => "selector",
                "urltest" => "urltest",
                "fallback" => "fallback",
                "loadbalance" => "loadbalance",
                "direct" => "direct",
                "reject" => "reject",
                "compatible" => "compatible",
                _ => normalized,
            };
        }

        private void CacheRuntimeGroups(IEnumerable<ProxyGroup> runtimeGroups)
        {
            lock (_groupCacheSync)
            {
                _lastRuntimeGroups = ModelSnapshotCloneHelper.CloneProxyGroups(runtimeGroups).ToList();
            }
        }

        private List<ProxyGroup> GetOrLoadRuntimeGroups(string runtimePath)
        {
            string runtimeFingerprint = FileFingerprintHelper.GetFingerprintOrMissing(runtimePath);
            lock (_groupCacheSync)
            {
                if (_runtimeGroupCache.TryGetValue(runtimeFingerprint, out RuntimeGroupCacheEntry? cachedEntry))
                {
                    cachedEntry.LastAccessedAt = DateTimeOffset.UtcNow;
                    return ModelSnapshotCloneHelper.CloneProxyGroups(cachedEntry.Groups).ToList();
                }
            }

            List<ProxyGroup> parsedGroups = ProxyGroupParser.ParseFromFile(runtimePath).ToList();
            lock (_groupCacheSync)
            {
                _runtimeGroupCache[runtimeFingerprint] = new RuntimeGroupCacheEntry(ModelSnapshotCloneHelper.CloneProxyGroups(parsedGroups));
                TrimRuntimeGroupCache();
            }

            return parsedGroups;
        }

        private void TrimRuntimeGroupCache()
        {
            while (_runtimeGroupCache.Count > 16)
            {
                string? oldestKey = null;
                DateTimeOffset oldestAccess = DateTimeOffset.MaxValue;

                foreach ((string key, RuntimeGroupCacheEntry entry) in _runtimeGroupCache)
                {
                    if (entry.LastAccessedAt < oldestAccess)
                    {
                        oldestAccess = entry.LastAccessedAt;
                        oldestKey = key;
                    }
                }

                if (oldestKey is null)
                {
                    break;
                }

                _runtimeGroupCache.Remove(oldestKey);
            }
        }

        private ProxyGroup? FindCachedRuntimeGroup(string groupName, string proxyName)
        {
            lock (_groupCacheSync)
            {
                ProxyGroup? matched = _lastRuntimeGroups.FirstOrDefault(group =>
                    string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(group.ControllerName)
                        && string.Equals(group.ControllerName, groupName, StringComparison.OrdinalIgnoreCase)));
                if (matched is not null)
                {
                    return CloneProxyGroup(matched);
                }

                matched = _lastRuntimeGroups.FirstOrDefault(group =>
                    group.Members.Any(member =>
                        string.Equals(member.Node.Name, proxyName, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(member.Node.ControllerName)
                            && string.Equals(member.Node.ControllerName, proxyName, StringComparison.OrdinalIgnoreCase))));
                return matched is null ? null : CloneProxyGroup(matched);
            }
        }

        private static ProxyGroup CloneProxyGroup(ProxyGroup source)
        {
            return ModelSnapshotCloneHelper.CloneProxyGroup(source);
        }

        private static IEnumerable<JsonProperty> EnumerateControllerGroups(JsonElement proxiesElement)
        {
            foreach (JsonProperty proxy in proxiesElement.EnumerateObject())
            {
                if (proxy.Value.ValueKind == JsonValueKind.Object
                    && IsGroupType(NormalizeGroupType(TryGetString(proxy.Value, "type"))))
                {
                    yield return proxy;
                }
            }
        }

        private static bool TryResolveApiGroupByStructure(ProxyGroup runtimeGroup, JsonElement proxiesElement, out string controllerGroupName, out JsonElement proxyElement)
        {
            GroupStructureMatch? best = GetBestGroupStructureMatch(runtimeGroup, proxiesElement, out _);
            if (best is null)
            {
                controllerGroupName = string.Empty;
                proxyElement = default;
                return false;
            }

            controllerGroupName = best.ControllerGroupName;
            proxyElement = best.ControllerGroupElement;
            return true;
        }

        private static GroupStructureMatch? GetBestGroupStructureMatch(ProxyGroup runtimeGroup, JsonElement proxiesElement, out GroupStructureMatch? runnerUp)
        {
            runnerUp = null;

            List<GroupStructureMatch> candidates = GetGroupStructureCandidates(runtimeGroup, proxiesElement);

            if (candidates.Count == 0)
            {
                return null;
            }

            GroupStructureMatch best = candidates[0];
            if (!best.ExactSetMatch && best.RuntimeCoverage < 0.8)
            {
                return null;
            }

            runnerUp = candidates
                .Skip(1)
                .FirstOrDefault(candidate => !string.Equals(candidate.ControllerGroupName, best.ControllerGroupName, StringComparison.OrdinalIgnoreCase));

            if (runnerUp is not null
                && best.ExactSetMatch == runnerUp.ExactSetMatch
                && Math.Abs(best.RuntimeCoverage - runnerUp.RuntimeCoverage) < 0.0001
                && Math.Abs(best.ControllerCoverage - runnerUp.ControllerCoverage) < 0.0001
                && best.CurrentMatches == runnerUp.CurrentMatches
                && best.OverlapCount == runnerUp.OverlapCount)
            {
                return null;
            }

            return best;
        }

        private static List<GroupStructureMatch> GetGroupStructureCandidates(ProxyGroup runtimeGroup, JsonElement proxiesElement)
        {
            string runtimeType = NormalizeGroupType(runtimeGroup.Type);
            List<GroupStructureMatch> candidates = new();

            foreach (JsonProperty proxy in EnumerateControllerGroups(proxiesElement))
            {
                string controllerType = NormalizeGroupType(TryGetString(proxy.Value, "type"));
                if (!string.Equals(controllerType, runtimeType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryBuildGroupStructureMatch(runtimeGroup, proxy, out GroupStructureMatch? match) || match is null)
                {
                    continue;
                }

                candidates.Add(match);
            }

            return candidates
                .OrderByDescending(candidate => candidate.ExactSetMatch)
                .ThenByDescending(candidate => candidate.RuntimeCoverage)
                .ThenByDescending(candidate => candidate.ControllerCoverage)
                .ThenByDescending(candidate => candidate.CurrentMatches)
                .ThenByDescending(candidate => candidate.OverlapCount)
                .ToList();
        }

        private static bool TryBuildGroupStructureMatch(ProxyGroup runtimeGroup, JsonProperty controllerGroup, out GroupStructureMatch? match)
        {
            match = null;
            string controllerGroupType = NormalizeGroupType(TryGetString(controllerGroup.Value, "type"));

            if (!controllerGroup.Value.TryGetProperty("all", out JsonElement allElement)
                || allElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            List<string> runtimeMembers = runtimeGroup.Members
                .Select(member => member.Node.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (runtimeMembers.Count == 0)
            {
                return false;
            }

            List<string> controllerMembers = allElement.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (controllerMembers.Count == 0)
            {
                return false;
            }

            int overlapCount = runtimeMembers.Count(runtimeMember =>
                controllerMembers.Any(controllerMember => NamesMatch(runtimeMember, controllerMember)));
            if (overlapCount == 0)
            {
                return false;
            }

            double runtimeCoverage = overlapCount / (double)runtimeMembers.Count;
            double controllerCoverage = overlapCount / (double)controllerMembers.Count;
            bool exactSetMatch = overlapCount == runtimeMembers.Count && overlapCount == controllerMembers.Count;
            bool currentMatches = false;

            string runtimeCurrent = runtimeGroup.CurrentProxyName;
            string controllerCurrent = TryGetString(controllerGroup.Value, "now");
            if (!string.IsNullOrWhiteSpace(runtimeCurrent)
                && !string.IsNullOrWhiteSpace(controllerCurrent)
                && NamesMatch(runtimeCurrent, controllerCurrent))
            {
                currentMatches = true;
            }

            match = new GroupStructureMatch
            {
                ControllerGroupName = controllerGroup.Name,
                ControllerGroupType = controllerGroupType,
                ControllerGroupElement = controllerGroup.Value,
                OverlapCount = overlapCount,
                RuntimeCoverage = runtimeCoverage,
                ControllerCoverage = controllerCoverage,
                ExactSetMatch = exactSetMatch,
                CurrentMatches = currentMatches,
            };
            return true;
        }

        private async Task<ProxySelectionResolution?> ResolveSelectionByStructureAsync(string groupName, string proxyName, CancellationToken cancellationToken)
        {
            ProxyGroup? runtimeGroup = FindCachedRuntimeGroup(groupName, proxyName);

            using JsonDocument? document = await GetProxiesDocumentAsync(cancellationToken).ConfigureAwait(false);
            if (document is null
                || !document.RootElement.TryGetProperty("proxies", out JsonElement proxiesElement)
                || proxiesElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (runtimeGroup is not null)
            {
                List<GroupStructureMatch> structureCandidates = GetGroupStructureCandidates(runtimeGroup, proxiesElement);
                GroupStructureMatch? bestStructureMatch = GetBestGroupStructureMatch(runtimeGroup, proxiesElement, out GroupStructureMatch? runnerUpStructureMatch);
                if (bestStructureMatch is not null)
                {
                    string runtimeTypeNormalized = NormalizeGroupType(runtimeGroup.Type);
                    string runnerUpSummary = runnerUpStructureMatch is null
                        ? "runnerUp=<none>"
                        : $"runnerUp={runnerUpStructureMatch.ControllerGroupName}, runnerUpType={runnerUpStructureMatch.ControllerGroupType}, overlap={runnerUpStructureMatch.OverlapCount}, runtimeCoverage={runnerUpStructureMatch.RuntimeCoverage:F2}, controllerCoverage={runnerUpStructureMatch.ControllerCoverage:F2}, exactSet={runnerUpStructureMatch.ExactSetMatch}, currentMatches={runnerUpStructureMatch.CurrentMatches}";

                    _logService.Add(
                        $"Mihomo structure match for selection retry: requestedGroup={groupName}, cachedGroup={runtimeGroup.Name}, " +
                        $"runtimeTypeNormalized={runtimeTypeNormalized}, best={bestStructureMatch.ControllerGroupName}, bestType={bestStructureMatch.ControllerGroupType}, overlap={bestStructureMatch.OverlapCount}, " +
                        $"runtimeCoverage={bestStructureMatch.RuntimeCoverage:F2}, controllerCoverage={bestStructureMatch.ControllerCoverage:F2}, " +
                        $"exactSet={bestStructureMatch.ExactSetMatch}, currentMatches={bestStructureMatch.CurrentMatches}, {runnerUpSummary}",
                        LogLevel.Info);
                }
                else
                {
                    string runtimeTypeNormalized = NormalizeGroupType(runtimeGroup.Type);
                    string candidateSummary = structureCandidates.Count == 0
                        ? "<none>"
                        : string.Join(" | ", structureCandidates.Take(5).Select(candidate =>
                            $"{candidate.ControllerGroupName}<{candidate.ControllerGroupType}>:overlap={candidate.OverlapCount},runtime={candidate.RuntimeCoverage:F2},controller={candidate.ControllerCoverage:F2},exactSet={candidate.ExactSetMatch},current={candidate.CurrentMatches}"));

                    _logService.Add(
                        $"Mihomo structure match for selection retry found no candidate: requestedGroup={groupName}, cachedGroup={runtimeGroup.Name}, runtimeTypeNormalized={runtimeTypeNormalized}, sameTypeCandidateCount={structureCandidates.Count}, requestedProxy={proxyName}, topCandidates={candidateSummary}, availableGroups={BuildControllerGroupPreview(proxiesElement)}",
                        LogLevel.Info);
                }
            }

            if (runtimeGroup is not null
                && TryResolveApiGroup(runtimeGroup, proxiesElement, out string controllerGroupName, out JsonElement controllerGroupElement)
                && TryResolveControllerProxyName(runtimeGroup, proxyName, controllerGroupElement, out string controllerProxyName, out string matchedBy))
            {
                _logService.Add(
                    $"Mihomo controller alias resolved for selection: requestedGroup={groupName}, controllerGroup={controllerGroupName}, requestedProxy={proxyName}, controllerProxy={controllerProxyName}, matchedBy={matchedBy}",
                    LogLevel.Info);

                return new ProxySelectionResolution
                {
                    GroupName = controllerGroupName,
                    ProxyName = controllerProxyName,
                };
            }

            if (runtimeGroup is not null
                && TryResolveApiGroup(runtimeGroup, proxiesElement, out string unresolvedControllerGroupName, out JsonElement unresolvedControllerGroupElement))
            {
                _logService.Add(
                    $"Mihomo controller proxy resolution failed after group match: requestedGroup={groupName}, controllerGroup={unresolvedControllerGroupName}, requestedProxy={proxyName}, controllerMembers={BuildControllerMembersPreview(unresolvedControllerGroupElement)}",
                    LogLevel.Info);
            }

            foreach (JsonProperty group in EnumerateControllerGroups(proxiesElement))
            {
                if (!TryResolveControllerProxyName(proxyName, group.Value, out string matchedControllerProxyName))
                {
                    continue;
                }

                if (string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase)
                    || NamesMatch(group.Name, groupName))
                {
                    return new ProxySelectionResolution
                    {
                        GroupName = group.Name,
                        ProxyName = matchedControllerProxyName,
                    };
                }
            }

            return null;
        }

        private static bool TryResolveControllerProxyName(ProxyGroup runtimeGroup, string proxyName, JsonElement controllerGroupElement, out string controllerProxyName, out string matchedBy)
        {
            controllerProxyName = string.Empty;
            matchedBy = string.Empty;
            if (!controllerGroupElement.TryGetProperty("all", out JsonElement allElement)
                || allElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            ProxyGroupMember? matchedMember = runtimeGroup.Members.FirstOrDefault(member =>
                string.Equals(member.Node.Name, proxyName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(member.Node.ControllerName)
                    && string.Equals(member.Node.ControllerName, proxyName, StringComparison.OrdinalIgnoreCase)));

            List<string> lookupNames = new();
            if (matchedMember is not null)
            {
                lookupNames.Add(matchedMember.Node.Name);
                if (!string.IsNullOrWhiteSpace(matchedMember.Node.ControllerName))
                {
                    lookupNames.Add(matchedMember.Node.ControllerName);
                }
            }

            lookupNames.Add(proxyName);

            List<string> uniqueLookupNames = lookupNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (JsonElement item in allElement.EnumerateArray())
            {
                string candidate = item.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string? matchedLookup = uniqueLookupNames.FirstOrDefault(name => NamesMatch(name, candidate));
                if (matchedLookup is not null)
                {
                    controllerProxyName = candidate;
                    matchedBy = matchedLookup;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveControllerProxyName(string proxyName, JsonElement controllerGroupElement, out string controllerProxyName)
        {
            controllerProxyName = string.Empty;
            if (!controllerGroupElement.TryGetProperty("all", out JsonElement allElement)
                || allElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement item in allElement.EnumerateArray())
            {
                string candidate = item.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (string.Equals(candidate, proxyName, StringComparison.OrdinalIgnoreCase)
                    || NamesMatch(candidate, proxyName))
                {
                    controllerProxyName = candidate;
                    return true;
                }
            }

            return false;
        }

        private static string BuildControllerMembersPreview(JsonElement controllerGroupElement, int maxItems = 8)
        {
            if (!controllerGroupElement.TryGetProperty("all", out JsonElement allElement)
                || allElement.ValueKind != JsonValueKind.Array)
            {
                return "<no-members>";
            }

            List<string> members = allElement.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(maxItems)
                .ToList();

            int totalCount = allElement.GetArrayLength();
            return members.Count == 0
                ? $"count={totalCount}"
                : $"{string.Join(", ", members)}{(totalCount > members.Count ? $" ... (count={totalCount})" : string.Empty)}";
        }

        private static string BuildControllerGroupPreview(JsonElement proxiesElement, int maxItems = 10)
        {
            List<string> groups = EnumerateControllerGroups(proxiesElement)
                .Take(maxItems)
                .Select(group =>
                {
                    string type = NormalizeGroupType(TryGetString(group.Value, "type"));
                    int memberCount = group.Value.TryGetProperty("all", out JsonElement allElement) && allElement.ValueKind == JsonValueKind.Array
                        ? allElement.GetArrayLength()
                        : 0;
                    return $"{group.Name}<{type}>[{memberCount}]";
                })
                .ToList();

            return groups.Count == 0 ? "<none>" : string.Join(", ", groups);
        }

        private async Task<ProxySelectionAttemptResult> SendSelectProxyRequestAsync(string groupName, string proxyName, CancellationToken cancellationToken)
        {
            try
            {
                string payloadJson = JsonSerializer.Serialize(
                    new MihomoProxySelectionPayload { Name = proxyName },
                    ClashJsonContext.Default.MihomoProxySelectionPayload);

                using var request = CreateRequest(
                    HttpMethod.Put,
                    $"/proxies/{Uri.EscapeDataString(groupName)}");
                request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return new ProxySelectionAttemptResult
                    {
                        Success = true,
                        StatusCode = response.StatusCode,
                        ReasonPhrase = response.ReasonPhrase,
                        Body = string.Empty,
                    };
                }

                string body = await SafeReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
                return new ProxySelectionAttemptResult
                {
                    Success = false,
                    StatusCode = response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase,
                    Body = body,
                };
            }
            catch (Exception ex)
            {
                return new ProxySelectionAttemptResult
                {
                    Success = false,
                    ReasonPhrase = ex.Message,
                    Body = string.Empty,
                };
            }
        }

        private async Task<JsonDocument?> GetProxiesDocumentAsync(CancellationToken cancellationToken, bool suppressErrors = false)
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, "/proxies");
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (!suppressErrors)
                    {
                        _logService.Add($"Mihomo proxies request failed: {(int)response.StatusCode} {response.ReasonPhrase}", LogLevel.Warning);
                    }
                    return null;
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!suppressErrors)
                {
                    _logService.Add($"Mihomo proxy groups request error: {ex.Message}", LogLevel.Warning);
                }
                return null;
            }
        }

        private sealed class ApplyConfigRequestResult
        {
            public required HttpMethod Method { get; init; }
            public required bool Success { get; init; }
            public HttpStatusCode? StatusCode { get; init; }
            public string? ReasonPhrase { get; init; }
            public string? Body { get; init; }
            public Exception? Exception { get; init; }
        }

        private sealed class ProxySelectionAttemptResult
        {
            public required bool Success { get; init; }
            public HttpStatusCode? StatusCode { get; init; }
            public string? ReasonPhrase { get; init; }
            public string? Body { get; init; }
        }

        private sealed class ProxySelectionResolution
        {
            public required string GroupName { get; init; }
            public required string ProxyName { get; init; }
        }

        private sealed class GroupStructureMatch
        {
            public required string ControllerGroupName { get; init; }
            public required string ControllerGroupType { get; init; }
            public required JsonElement ControllerGroupElement { get; init; }
            public required int OverlapCount { get; init; }
            public required double RuntimeCoverage { get; init; }
            public required double ControllerCoverage { get; init; }
            public required bool ExactSetMatch { get; init; }
            public required bool CurrentMatches { get; init; }
        }

        private sealed class ControllerConvergenceResult
        {
            public required bool IsConverged { get; init; }
            public required int RuntimeGroupCount { get; init; }
            public required int ControllerGroupCount { get; init; }
            public required int AliasMatchedGroupCount { get; init; }
            public required int AliasMatchedMemberCount { get; init; }
            public required int TotalMemberCount { get; init; }
            public required int AnchorGroupCount { get; init; }
            public required int StableAnchorGroupCount { get; init; }
            public required int IgnoredGroupCount { get; init; }
            public required bool AliasCoverageSatisfied { get; init; }
            public required bool AnchorCoverageSatisfied { get; init; }
            public required ConvergenceDecision Decision { get; init; }
            public required IReadOnlyList<ProxyGroup> ControllerBackedGroups { get; init; }
            public required string AnchorGroupPreview { get; init; }

            public string DecisionLabel => Decision switch
            {
                ConvergenceDecision.HardConverged => "hard",
                ConvergenceDecision.SoftConvergedAfterRestart => "soft",
                _ => "failed",
            };

            public static ControllerConvergenceResult Empty(int runtimeGroupCount)
            {
                return new ControllerConvergenceResult
                {
                    IsConverged = false,
                    RuntimeGroupCount = runtimeGroupCount,
                    ControllerGroupCount = 0,
                    AliasMatchedGroupCount = 0,
                    AliasMatchedMemberCount = 0,
                    TotalMemberCount = 0,
                    AnchorGroupCount = 0,
                    StableAnchorGroupCount = 0,
                    IgnoredGroupCount = 0,
                    AliasCoverageSatisfied = false,
                    AnchorCoverageSatisfied = false,
                    Decision = ConvergenceDecision.NotConverged,
                    ControllerBackedGroups = Array.Empty<ProxyGroup>(),
                    AnchorGroupPreview = "<none>",
                };
            }
        }

        private enum ConvergenceDecision
        {
            NotConverged = 0,
            HardConverged = 1,
            SoftConvergedAfterRestart = 2,
        }

        private sealed class RuntimeGroupCacheEntry
        {
            public RuntimeGroupCacheEntry(IReadOnlyList<ProxyGroup> groups)
            {
                Groups = groups;
                LastAccessedAt = DateTimeOffset.UtcNow;
            }

            public IReadOnlyList<ProxyGroup> Groups { get; }

            public DateTimeOffset LastAccessedAt { get; set; }
        }

        private sealed class ConnectionTrafficSnapshot
        {
            public required DateTimeOffset Timestamp { get; init; }

            public required long Download { get; init; }

            public required long Upload { get; init; }
        }
    }
}

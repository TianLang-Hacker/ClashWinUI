
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

namespace ClashWinUI.Services.Implementations
{
    public class MihomoService : IMihomoService
    {
        private static readonly HashSet<string> GroupTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Selector",
            "URLTest",
            "Fallback",
            "LoadBalance",
            "Direct",
            "Reject",
            "Compatible",
        };

        private readonly IAppLogService _logService;
        private readonly HttpClient _httpClient;
        private readonly string _controllerBaseUrl;
        private readonly string? _controllerSecret;
        private readonly HashSet<string> _incompatibleApplyWarned = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _warnSync = new();
        private readonly Dictionary<string, DateTimeOffset> _applyFailureLoggedAt = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _applyFailureSync = new();

        private static readonly TimeSpan ApplyFailureLogDedupeWindow = TimeSpan.FromSeconds(20);

        public event EventHandler<string>? ConfigApplied;

        public MihomoService(IAppLogService logService)
        {
            _logService = logService;
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
                    ClearApplyFailureHistory(configPath);
                    _logService.Add($"Mihomo config applied: {configPath}");
                    ConfigApplied?.Invoke(this, configPath);
                    return true;
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
                        ClearApplyFailureHistory(configPath);
                        _logService.Add($"Mihomo config applied: {configPath}");
                        ConfigApplied?.Invoke(this, configPath);
                        return true;
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

        public async Task<IReadOnlyList<ProxyNode>> GetProxiesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, "/proxies");
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logService.Add($"Mihomo proxies request failed: {(int)response.StatusCode} {response.ReasonPhrase}", LogLevel.Warning);
                    return Array.Empty<ProxyNode>();
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!doc.RootElement.TryGetProperty("proxies", out JsonElement proxiesElement)
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

                    if (GroupTypes.Contains(type))
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

        private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
        {
            var request = new HttpRequestMessage(method, $"{_controllerBaseUrl}{relativePath}");
            if (!string.IsNullOrWhiteSpace(_controllerSecret))
            {
                request.Headers.Add("Authorization", $"Bearer {_controllerSecret}");
            }

            return request;
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
    }
}

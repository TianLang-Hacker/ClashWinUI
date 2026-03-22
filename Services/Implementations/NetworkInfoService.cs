using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Implementations
{
    public sealed class NetworkInfoService : INetworkInfoService
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
        private static readonly Uri LookupUri = new("http://ip-api.com/json/?fields=status,message,query,country,regionName,city,timezone,isp,org,as,lat,lon");

        private readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        private readonly IAppLogService _logService;
        private readonly SemaphoreSlim _sync = new(1, 1);

        private PublicNetworkInfo? _cachedInfo;
        private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;

        public NetworkInfoService(IAppLogService logService)
        {
            _logService = logService;
        }

        public async Task<PublicNetworkInfo?> GetPublicNetworkInfoAsync(CancellationToken cancellationToken = default)
        {
            if (_cachedInfo is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
            {
                return _cachedInfo;
            }

            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedInfo is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
                {
                    return _cachedInfo;
                }

                using HttpResponseMessage response = await _httpClient.GetAsync(LookupUri, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logService.Add($"Public network info request failed: {(int)response.StatusCode} {response.ReasonPhrase}", LogLevel.Warning);
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (root.TryGetProperty("status", out JsonElement statusElement)
                    && statusElement.ValueKind == JsonValueKind.String
                    && !string.Equals(statusElement.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    string message = GetString(root, "message");
                    string suffix = string.IsNullOrWhiteSpace(message) ? string.Empty : $": {message}";
                    _logService.Add($"Public network info request failed: ip-api.com returned an error{suffix}", LogLevel.Warning);
                    return null;
                }

                var info = new PublicNetworkInfo
                {
                    Ip = GetString(root, "query"),
                    Location = BuildLocation(root),
                    AsNumber = BuildAsNumber(root),
                    ServiceProvider = GetString(root, "isp"),
                    Organization = GetString(root, "org"),
                    TimeZone = GetString(root, "timezone"),
                    Coordinates = BuildCoordinates(root),
                };

                _cachedInfo = info;
                _cacheExpiresAt = DateTimeOffset.UtcNow.Add(CacheDuration);
                return info;
            }
            catch (Exception ex)
            {
                _logService.Add($"Public network info request error: {ex.Message}", LogLevel.Warning);
                return null;
            }
            finally
            {
                _sync.Release();
            }
        }

        private static string BuildLocation(JsonElement root)
        {
            string country = GetString(root, "country");
            string region = GetString(root, "regionName");
            string city = GetString(root, "city");
            var segments = new List<string>();

            if (!string.IsNullOrWhiteSpace(country))
            {
                segments.Add(country);
            }

            if (!string.IsNullOrWhiteSpace(region))
            {
                segments.Add(region);
            }

            if (!string.IsNullOrWhiteSpace(city))
            {
                segments.Add(city);
            }

            return string.Join(" / ", segments);
        }

        private static string BuildAsNumber(JsonElement root)
        {
            string value = GetString(root, "as");
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string[] segments = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0 && segments[0].StartsWith("AS", StringComparison.OrdinalIgnoreCase))
            {
                return segments[0];
            }

            return value;
        }

        private static string BuildCoordinates(JsonElement root)
        {
            if (!root.TryGetProperty("lat", out JsonElement latitudeElement)
                || !root.TryGetProperty("lon", out JsonElement longitudeElement)
                || latitudeElement.ValueKind != JsonValueKind.Number
                || longitudeElement.ValueKind != JsonValueKind.Number)
            {
                return string.Empty;
            }

            double latitude = latitudeElement.GetDouble();
            double longitude = longitudeElement.GetDouble();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{latitude.ToString("0.####", CultureInfo.InvariantCulture)}, {longitude.ToString("0.####", CultureInfo.InvariantCulture)}");
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
    }
}

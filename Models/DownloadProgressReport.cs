using System;
using System.Text.Json;

namespace ClashWinUI.Models
{
    public sealed class DownloadProgressReport
    {
        private const string ScriptProgressPrefix = "CWUI_PROGRESS ";

        public string Stage { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        public long BytesReceived { get; init; }

        public long? TotalBytes { get; init; }

        public double Percentage { get; init; }

        public bool IsIndeterminate { get; init; }

        public static bool TryParseScriptLine(string? line, out DownloadProgressReport report)
        {
            report = new DownloadProgressReport();
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(ScriptProgressPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            string json = line[ScriptProgressPrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                report = new DownloadProgressReport
                {
                    Stage = GetString(root, "stage"),
                    FileName = GetString(root, "fileName"),
                    BytesReceived = GetInt64(root, "bytesReceived"),
                    TotalBytes = GetNullableInt64(root, "totalBytes"),
                    Percentage = GetDouble(root, "percentage"),
                    IsIndeterminate = GetBoolean(root, "isIndeterminate"),
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetString(JsonElement root, string propertyName)
        {
            return root.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static long GetInt64(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement property))
            {
                return 0;
            }

            return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long value)
                ? value
                : 0;
        }

        private static long? GetNullableInt64(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long value)
                ? value
                : null;
        }

        private static double GetDouble(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement property))
            {
                return 0;
            }

            return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out double value)
                ? value
                : 0;
        }

        private static bool GetBoolean(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement property))
            {
                return false;
            }

            return property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => false,
            };
        }
    }
}

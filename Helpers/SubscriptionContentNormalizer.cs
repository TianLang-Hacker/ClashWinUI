using System;
using System.Text;

namespace ClashWinUI.Helpers
{
    public enum SubscriptionContentNormalizationStatus
    {
        AlreadyYaml = 0,
        DecodedFromBase64 = 1,
        Unrecognized = 2,
        Base64DecodedButNotYaml = 3,
        Base64DecodeFailed = 4,
    }

    public sealed class SubscriptionContentNormalizationResult
    {
        public required byte[] Content { get; init; }
        public required SubscriptionContentNormalizationStatus Status { get; init; }
    }

    public static class SubscriptionContentNormalizer
    {
        public static SubscriptionContentNormalizationResult Normalize(byte[] rawContent)
        {
            if (rawContent is null || rawContent.Length == 0)
            {
                return new SubscriptionContentNormalizationResult
                {
                    Content = rawContent ?? [],
                    Status = SubscriptionContentNormalizationStatus.Unrecognized,
                };
            }

            string text = Encoding.UTF8.GetString(rawContent).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return new SubscriptionContentNormalizationResult
                {
                    Content = rawContent,
                    Status = SubscriptionContentNormalizationStatus.Unrecognized,
                };
            }

            if (LooksLikeYaml(text))
            {
                return new SubscriptionContentNormalizationResult
                {
                    Content = rawContent,
                    Status = SubscriptionContentNormalizationStatus.AlreadyYaml,
                };
            }

            if (!LooksLikeBase64(text))
            {
                return new SubscriptionContentNormalizationResult
                {
                    Content = rawContent,
                    Status = SubscriptionContentNormalizationStatus.Unrecognized,
                };
            }

            if (!TryDecodeBase64Text(text, out string? decodedText))
            {
                return new SubscriptionContentNormalizationResult
                {
                    Content = rawContent,
                    Status = SubscriptionContentNormalizationStatus.Base64DecodeFailed,
                };
            }

            if (string.IsNullOrWhiteSpace(decodedText) || !LooksLikeYaml(decodedText))
            {
                return new SubscriptionContentNormalizationResult
                {
                    Content = Encoding.UTF8.GetBytes(decodedText ?? string.Empty),
                    Status = SubscriptionContentNormalizationStatus.Base64DecodedButNotYaml,
                };
            }

            return new SubscriptionContentNormalizationResult
            {
                Content = Encoding.UTF8.GetBytes(decodedText),
                Status = SubscriptionContentNormalizationStatus.DecodedFromBase64,
            };
        }

        public static bool LooksLikeYaml(string text)
        {
            string normalized = text.TrimStart();
            return normalized.StartsWith("proxies:", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\nproxies:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("mixed-port:", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\nrules:", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\nproxy-groups:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("port:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeBase64(string text)
        {
            string compact = text.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Trim();

            if (compact.Length < 16)
            {
                return false;
            }

            foreach (char ch in compact)
            {
                bool valid = (ch >= 'A' && ch <= 'Z')
                    || (ch >= 'a' && ch <= 'z')
                    || (ch >= '0' && ch <= '9')
                    || ch == '+'
                    || ch == '/'
                    || ch == '-'
                    || ch == '_'
                    || ch == '=';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryDecodeBase64Text(string input, out string? decodedText)
        {
            decodedText = null;

            string compact = input.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Trim();
            if (string.IsNullOrWhiteSpace(compact))
            {
                return false;
            }

            compact = compact.Replace('-', '+').Replace('_', '/');
            int remainder = compact.Length % 4;
            if (remainder > 0)
            {
                compact = compact.PadRight(compact.Length + (4 - remainder), '=');
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(compact);
                decodedText = Encoding.UTF8.GetString(bytes);
                return !string.IsNullOrWhiteSpace(decodedText);
            }
            catch
            {
                return false;
            }
        }
    }
}

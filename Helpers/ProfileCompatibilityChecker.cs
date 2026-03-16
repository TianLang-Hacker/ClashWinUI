using System;
using System.IO;

namespace ClashWinUI.Helpers
{
    public enum ProfileCompatibilityStatus
    {
        Compatible = 0,
        Base64NotYaml = 1,
        Unknown = 2,
        Missing = 3,
    }

    public static class ProfileCompatibilityChecker
    {
        public static ProfileCompatibilityStatus Check(string? configPath)
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                return ProfileCompatibilityStatus.Missing;
            }

            try
            {
                byte[] raw = File.ReadAllBytes(configPath);
                SubscriptionContentNormalizationResult normalization = SubscriptionContentNormalizer.Normalize(raw);
                return normalization.Status switch
                {
                    SubscriptionContentNormalizationStatus.AlreadyYaml => ProfileCompatibilityStatus.Compatible,
                    SubscriptionContentNormalizationStatus.DecodedFromBase64 => ProfileCompatibilityStatus.Compatible,
                    SubscriptionContentNormalizationStatus.Base64DecodedButNotYaml
                        => ShareLinkSubscriptionConverter.CanConvertToMihomoYaml(normalization.Content)
                            ? ProfileCompatibilityStatus.Compatible
                            : ProfileCompatibilityStatus.Base64NotYaml,
                    SubscriptionContentNormalizationStatus.Base64DecodeFailed => ProfileCompatibilityStatus.Base64NotYaml,
                    SubscriptionContentNormalizationStatus.Unrecognized
                        => ShareLinkSubscriptionConverter.CanConvertToMihomoYaml(normalization.Content)
                            ? ProfileCompatibilityStatus.Compatible
                            : ProfileCompatibilityStatus.Unknown,
                    _ => ProfileCompatibilityStatus.Unknown,
                };
            }
            catch
            {
                return ProfileCompatibilityStatus.Unknown;
            }
        }
    }
}

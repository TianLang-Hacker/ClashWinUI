using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClashWinUI.Helpers
{
    public static class FileFingerprintHelper
    {
        public static bool TryGetFingerprint(string? path, out string fingerprint)
        {
            fingerprint = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(path.Trim());
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var fileInfo = new FileInfo(fullPath);
            fingerprint = $"{fullPath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
            return true;
        }

        public static string GetFingerprintOrMissing(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "<null>";
            }

            string fullPath = Path.GetFullPath(path.Trim());
            if (!File.Exists(fullPath))
            {
                return $"{fullPath}|missing";
            }

            var fileInfo = new FileInfo(fullPath);
            return $"{fullPath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
        }

        public static string Combine(params string?[] fingerprints)
        {
            return string.Join(
                "||",
                fingerprints
                    .Where(item => item is not null)
                    .Select(item => item ?? string.Empty));
        }

        public static string Combine(IEnumerable<string?> fingerprints)
        {
            return string.Join(
                "||",
                fingerprints
                    .Where(item => item is not null)
                    .Select(item => item ?? string.Empty));
        }
    }
}

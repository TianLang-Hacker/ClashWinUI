using System;
using System.Globalization;

namespace ClashWinUI.Models
{
    public sealed class ConnectionEntry
    {
        public required string Id { get; init; }

        public required string HostDisplay { get; init; }

        public required string TypeDisplay { get; init; }

        public required string RuleDisplay { get; init; }

        public required string ChainDisplay { get; init; }

        public required long DownloadSpeed { get; init; }

        public required long UploadSpeed { get; init; }

        public required long Download { get; init; }

        public required long Upload { get; init; }

        public required DateTimeOffset? StartedAt { get; init; }

        public string DownloadSpeedText => $"{FormatBytes(DownloadSpeed)}/s";

        public string UploadSpeedText => $"{FormatBytes(UploadSpeed)}/s";

        public string DownloadText => FormatBytes(Download);

        public string UploadText => FormatBytes(Upload);

        public string DurationText => FormatDuration(StartedAt);

        private static string FormatBytes(long value)
        {
            if (value <= 0)
            {
                return "0 B";
            }

            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = value;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            string format = size >= 100 || unitIndex == 0 ? "0" : size >= 10 ? "0.0" : "0.##";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{size.ToString(format, CultureInfo.InvariantCulture)} {units[unitIndex]}");
        }

        private static string FormatDuration(DateTimeOffset? startedAt)
        {
            if (startedAt is null)
            {
                return "--";
            }

            TimeSpan elapsed = DateTimeOffset.Now - startedAt.Value.ToLocalTime();
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            bool isChinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            if (elapsed.TotalDays >= 1)
            {
                int days = (int)elapsed.TotalDays;
                return isChinese
                    ? $"{days} 天前"
                    : $"{days}d ago";
            }

            if (elapsed.TotalHours >= 1)
            {
                int hours = (int)elapsed.TotalHours;
                return isChinese
                    ? $"{hours} 小时前"
                    : $"{hours}h ago";
            }

            if (elapsed.TotalMinutes >= 1)
            {
                int minutes = (int)elapsed.TotalMinutes;
                return isChinese
                    ? $"{minutes} 分钟前"
                    : $"{minutes}m ago";
            }

            int seconds = Math.Max(0, elapsed.Seconds);
            if (seconds <= 5)
            {
                return isChinese ? "几秒前" : "just now";
            }

            return isChinese
                ? $"{seconds} 秒前"
                : $"{seconds}s ago";
        }
    }
}

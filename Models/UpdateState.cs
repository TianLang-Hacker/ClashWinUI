namespace ClashWinUI.Models
{
    public sealed class UpdateState
    {
        public UpdateStatus Status { get; init; } = UpdateStatus.Unknown;

        public string CurrentVersion { get; init; } = string.Empty;

        public string LatestVersion { get; init; } = string.Empty;

        public string ReleasePageUrl { get; init; } = string.Empty;

        public string SelectedAssetName { get; init; } = string.Empty;

        public double DownloadProgressPercentage { get; init; }

        public bool IsDownloadProgressIndeterminate { get; init; }

        public bool IsBusy { get; init; }
    }
}

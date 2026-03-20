namespace ClashWinUI.Models
{
    public sealed class GeoDataAssetStatus
    {
        public required string Name { get; init; }

        public required bool Exists { get; init; }

        public required long Length { get; init; }
    }
}

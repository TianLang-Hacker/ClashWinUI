namespace ClashWinUI.Models
{
    public sealed class TunPreparationResult
    {
        public required bool Success { get; init; }

        public required MihomoFailureKind FailureKind { get; init; }

        public required string Message { get; init; }

        public static TunPreparationResult Ok()
        {
            return new TunPreparationResult
            {
                Success = true,
                FailureKind = MihomoFailureKind.None,
                Message = string.Empty,
            };
        }
    }
}

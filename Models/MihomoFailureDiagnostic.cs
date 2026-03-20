using System;

namespace ClashWinUI.Models
{
    public sealed class MihomoFailureDiagnostic
    {
        public static MihomoFailureDiagnostic None { get; } = new()
        {
            Kind = MihomoFailureKind.None,
            Message = string.Empty,
            OccurredAt = DateTimeOffset.MinValue,
        };

        public required MihomoFailureKind Kind { get; init; }

        public required string Message { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }
    }
}

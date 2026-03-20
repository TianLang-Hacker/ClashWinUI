using System;
using System.Collections.Generic;

namespace ClashWinUI.Models
{
    public sealed class GeoDataOperationResult
    {
        public static GeoDataOperationResult None { get; } = new()
        {
            HasRun = false,
            Success = true,
            OperationKind = GeoDataOperationKind.None,
            FailureKind = GeoDataFailureKind.None,
            Details = string.Empty,
            Assets = Array.Empty<GeoDataAssetStatus>(),
        };

        public required bool HasRun { get; init; }

        public required bool Success { get; init; }

        public required GeoDataOperationKind OperationKind { get; init; }

        public required GeoDataFailureKind FailureKind { get; init; }

        public required string Details { get; init; }

        public required IReadOnlyList<GeoDataAssetStatus> Assets { get; init; }
    }
}

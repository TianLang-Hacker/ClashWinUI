using System;

namespace ClashWinUI.Models
{
    public sealed class ProfileItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string DisplayName { get; set; } = string.Empty;
        public string SourceType { get; set; } = "subscription";
        public string Source { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
        public int NodeCount { get; set; }
    }
}

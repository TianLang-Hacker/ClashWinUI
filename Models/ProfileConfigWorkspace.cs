namespace ClashWinUI.Models
{
    public sealed class ProfileConfigWorkspace
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required string MixinPath { get; init; }
        public required string RuntimePath { get; init; }
        public required string RulesOverridePath { get; init; }
    }
}

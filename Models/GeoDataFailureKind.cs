namespace ClashWinUI.Models
{
    public enum GeoDataFailureKind
    {
        None = 0,
        MissingOrEmpty,
        Invalid,
        DownloadFailed,
        ScriptMissing,
        ScriptLaunchFailed,
        Unknown,
    }
}

using System;
using System.IO;

namespace ClashWinUI.Common
{
    internal static class AppDataPaths
    {
        private static readonly string RootDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClashWinUI");

        private static readonly string StateDirectoryPath = Path.Combine(RootDirectoryPath, "State");

        public static string RootDirectory => RootDirectoryPath;

        public static string StateDirectory => StateDirectoryPath;

        public static string PendingLaunchFilePath => Path.Combine(StateDirectoryPath, "pending-launch.json");

        public static string RuntimeStateFilePath => Path.Combine(StateDirectoryPath, "runtime-state.json");

        public static string AppSettingsFilePath => Path.Combine(RootDirectoryPath, "appsettings.json");

        public static string ProfilesDirectoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "ClashWinUI",
            "Profiles");

        public static string ProfilesIndexFilePath => Path.Combine(ProfilesDirectoryPath, "profiles.json");

        public static void EnsureStateDirectory()
        {
            Directory.CreateDirectory(StateDirectoryPath);
        }
    }
}

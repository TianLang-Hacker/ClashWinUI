using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace ClashWinUI.Helpers
{
    internal static class AppElevationHelper
    {
        private const int UserCancelledErrorCode = 1223;
        private const string DefaultPackagedAppId = "App";

        public static bool IsProcessElevated()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static ElevationRelaunchOutcome TryRelaunchAsAdministrator()
        {
            ElevationTargetInfo target = ResolveElevationTarget();
            if (!IsValidElevationTarget(target))
            {
                return ElevationRelaunchOutcome.Failed(
                    target,
                    $"Elevation target executable is unavailable. Mode={target.LaunchMode}; Path={target.ExecutablePath}");
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target.ExecutablePath,
                    Arguments = string.Empty,
                    WorkingDirectory = target.WorkingDirectory,
                    UseShellExecute = true,
                    Verb = "runas",
                });

                return ElevationRelaunchOutcome.Relaunched(target);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == UserCancelledErrorCode)
            {
                return ElevationRelaunchOutcome.UserCancelled(target, ex.Message);
            }
            catch (Exception ex)
            {
                return ElevationRelaunchOutcome.Failed(target, ex.Message);
            }
        }

        public static ElevationRelaunchOutcome TryRelaunch()
        {
            return TryRelaunchCore(delaySeconds: 0);
        }

        public static ElevationRelaunchOutcome TryRelaunchDelayed(int delaySeconds = 1)
        {
            return TryRelaunchCore(delaySeconds: Math.Clamp(delaySeconds, 1, 10));
        }

        public static ElevationRelaunchOutcome TryLaunchNewInstance()
        {
            return TryRelaunch();
        }

        public static ElevationRelaunchOutcome TryLaunchUnelevatedInstance()
        {
            if (!IsProcessElevated())
            {
                return TryLaunchNewInstance();
            }

            ElevationTargetInfo target = ResolveElevationTarget();
            if (!IsValidElevationTarget(target))
            {
                return ElevationRelaunchOutcome.Failed(
                    target,
                    $"Unelevated launch target executable is unavailable. Mode={target.LaunchMode}; Path={target.ExecutablePath}");
            }

            try
            {
                // explorer.exe runs at medium IL and can start a non-elevated companion from an elevated parent.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + target.ExecutablePath + "\"",
                    UseShellExecute = true,
                });

                return ElevationRelaunchOutcome.Relaunched(target);
            }
            catch (Exception ex)
            {
                return ElevationRelaunchOutcome.Failed(target, ex.Message);
            }
        }

        private static ElevationRelaunchOutcome TryRelaunchCore(int delaySeconds)
        {
            ElevationTargetInfo target = ResolveElevationTarget();
            if (!IsValidElevationTarget(target))
            {
                return ElevationRelaunchOutcome.Failed(
                    target,
                    $"Relaunch target executable is unavailable. Mode={target.LaunchMode}; Path={target.ExecutablePath}");
            }

            try
            {
                if (delaySeconds <= 0)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = target.ExecutablePath,
                        Arguments = string.Empty,
                        WorkingDirectory = target.WorkingDirectory,
                        UseShellExecute = true,
                    });
                }
                else
                {
                    string launchCommand = string.Equals(target.LaunchMode, "packaged", StringComparison.OrdinalIgnoreCase)
                        ? $"explorer.exe \"{EscapeForCmd(target.ExecutablePath)}\""
                        : $"start \"\" \"{EscapeForCmd(target.ExecutablePath)}\"";

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c timeout /t {delaySeconds} /nobreak >nul & {launchCommand}",
                        WorkingDirectory = string.IsNullOrWhiteSpace(target.WorkingDirectory)
                            ? Environment.SystemDirectory
                            : target.WorkingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                }

                return ElevationRelaunchOutcome.Relaunched(target);
            }
            catch (Exception ex)
            {
                return ElevationRelaunchOutcome.Failed(target, ex.Message);
            }
        }

        private static string EscapeForCmd(string value)
        {
            return value.Replace("\"", "\"\"", StringComparison.Ordinal);
        }

        private static ElevationTargetInfo ResolveElevationTarget()
        {
            if (TryResolvePackagedShellTarget(out string shellTarget))
            {
                return new ElevationTargetInfo(
                    shellTarget,
                    AppContext.BaseDirectory,
                    LaunchMode: "packaged");
            }

            string currentExecutablePath = ResolveCurrentExecutablePath();
            string workingDirectory = string.IsNullOrWhiteSpace(currentExecutablePath)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(currentExecutablePath) ?? AppContext.BaseDirectory;
            return new ElevationTargetInfo(
                currentExecutablePath,
                workingDirectory,
                LaunchMode: "unpackaged");
        }

        private static bool TryResolvePackagedShellTarget(out string shellTarget)
        {
            shellTarget = string.Empty;

            if (!AppPackageInfoHelper.IsPackaged())
            {
                return false;
            }

            try
            {
                string? packageFamilyName = AppPackageInfoHelper.TryGetPackageFamilyName();
                if (string.IsNullOrWhiteSpace(packageFamilyName))
                {
                    return false;
                }

                shellTarget = $@"shell:AppsFolder\{packageFamilyName}!{DefaultPackagedAppId}";
                return true;
            }
            catch
            {
                shellTarget = string.Empty;
                return false;
            }
        }

        private static string ResolveCurrentExecutablePath()
        {
            return Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? string.Empty;
        }

        private static bool IsValidElevationTarget(ElevationTargetInfo target)
        {
            if (string.IsNullOrWhiteSpace(target.ExecutablePath))
            {
                return false;
            }

            return string.Equals(target.LaunchMode, "packaged", StringComparison.OrdinalIgnoreCase)
                || File.Exists(target.ExecutablePath);
        }
    }

    internal enum ElevationRelaunchStatus
    {
        Relaunched,
        UserCancelled,
        Failed,
    }

    internal readonly record struct ElevationTargetInfo(string ExecutablePath, string WorkingDirectory, string LaunchMode);

    internal readonly record struct ElevationRelaunchOutcome(ElevationRelaunchStatus Status, ElevationTargetInfo Target, string Message)
    {
        public static ElevationRelaunchOutcome Relaunched(ElevationTargetInfo target)
        {
            return new(ElevationRelaunchStatus.Relaunched, target, string.Empty);
        }

        public static ElevationRelaunchOutcome UserCancelled(ElevationTargetInfo target, string message)
        {
            return new(ElevationRelaunchStatus.UserCancelled, target, message);
        }

        public static ElevationRelaunchOutcome Failed(ElevationTargetInfo target, string message)
        {
            return new(ElevationRelaunchStatus.Failed, target, message);
        }
    }
}

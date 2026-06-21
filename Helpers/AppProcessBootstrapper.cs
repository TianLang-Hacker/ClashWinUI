using ClashWinUI.Models;
using ClashWinUI.ViewModels;
using System;
using System.Threading;

namespace ClashWinUI.Helpers
{
    internal sealed class AppProcessBootstrapResult
    {
        public bool ShouldExit { get; init; }

        public AppProcessRole Role { get; init; }

        public bool ShouldOwnStartupPipeline { get; init; }

        public bool ShouldSpawnTrayCompanion { get; init; }
    }

    internal static class AppProcessBootstrapper
    {
        private static AppProcessBootstrapResult? _current;
        private static Mutex? _roleMutex;

        public static AppProcessBootstrapResult Current
        {
            get
            {
                return _current ?? throw new InvalidOperationException("App process bootstrap has not been initialized.");
            }
        }

        public static AppProcessBootstrapResult TryInitialize()
        {
            if (_current is not null)
            {
                return _current;
            }

            bool uiRunning = IsRoleRunning(AppProcessRole.Ui);
            bool trayRunning = IsRoleRunning(AppProcessRole.Tray);

            if (uiRunning && trayRunning)
            {
                AppControlChannel.TrySendAsync(
                    AppProcessRole.Ui,
                    AppControlCommand.CreateShowRoute(MainViewModel.HomeRouteKey)).GetAwaiter().GetResult();

                _current = new AppProcessBootstrapResult
                {
                    ShouldExit = true,
                    Role = AppProcessRole.Ui,
                };
                return _current;
            }

            AppProcessRole desiredRole = uiRunning && !trayRunning
                ? AppProcessRole.Tray
                : AppProcessRole.Ui;

            if (!TryAcquireRole(desiredRole, out Mutex? roleMutex))
            {
                _current = new AppProcessBootstrapResult
                {
                    ShouldExit = true,
                    Role = desiredRole,
                };
                return _current;
            }

            _roleMutex = roleMutex;
            _current = new AppProcessBootstrapResult
            {
                ShouldExit = false,
                Role = desiredRole,
                ShouldOwnStartupPipeline = desiredRole == AppProcessRole.Ui && !trayRunning,
                ShouldSpawnTrayCompanion = desiredRole == AppProcessRole.Ui && !trayRunning,
            };
            return _current;
        }

        public static bool IsRoleRunning(AppProcessRole role)
        {
            try
            {
                return Mutex.TryOpenExisting(GetMutexName(role), out Mutex? existing) && CloseExistingMutex(existing);
            }
            catch
            {
                return false;
            }
        }

        private static bool CloseExistingMutex(Mutex? mutex)
        {
            mutex?.Dispose();
            return true;
        }

        private static bool TryAcquireRole(AppProcessRole role, out Mutex? mutex)
        {
            mutex = null;

            try
            {
                Mutex candidate = new(false, GetMutexName(role));
                bool acquired;
                try
                {
                    acquired = candidate.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    candidate.Dispose();
                    return false;
                }

                mutex = candidate;
                return true;
            }
            catch
            {
                mutex?.Dispose();
                mutex = null;
                return false;
            }
        }

        private static string GetMutexName(AppProcessRole role)
        {
            string roleSegment = role == AppProcessRole.Tray ? "tray" : "ui";
            string userSegment = Environment.UserName.Replace('\\', '_').Replace('/', '_');
            return $@"Local\ClashWinUI.{userSegment}.{roleSegment}";
        }
    }
}

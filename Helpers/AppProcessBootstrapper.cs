using ClashWinUI.Models;
using ClashWinUI.ViewModels;
using System;
using System.Threading;
using System.Threading.Tasks;

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

            if (TryTakeOverAsElevatedUi(out AppProcessBootstrapResult? elevatedResult) && elevatedResult is not null)
            {
                _current = elevatedResult;
                return _current;
            }

            bool uiRunning = IsRoleRunning(AppProcessRole.Ui);
            bool trayRunning = IsRoleRunning(AppProcessRole.Tray);

            if (uiRunning && trayRunning)
            {
                bool forwarded = AppControlChannel.TrySendAsync(
                    AppProcessRole.Ui,
                    AppControlCommand.CreateShowRoute(MainViewModel.HomeRouteKey)).GetAwaiter().GetResult();

                if (forwarded)
                {
                    _current = new AppProcessBootstrapResult
                    {
                        ShouldExit = true,
                        Role = AppProcessRole.Ui,
                    };
                    return _current;
                }

                // UI mutex exists but control channel is dead (stale/zombie UI).
                // Fall through and try to reclaim the UI role.
            }

            AppProcessRole desiredRole = uiRunning && !trayRunning
                ? AppProcessRole.Tray
                : AppProcessRole.Ui;

            // When reclaiming a stale UI while tray is alive, prefer UI.
            if (uiRunning && trayRunning)
            {
                desiredRole = AppProcessRole.Ui;
            }

            if (!TryAcquireRole(desiredRole, out Mutex? roleMutex))
            {
                // One more chance: if UI looked running but is stale, wait briefly and retry UI.
                if (desiredRole == AppProcessRole.Ui)
                {
                    WaitUntilRoleApparentlyFree(AppProcessRole.Ui, TimeSpan.FromSeconds(2));
                    if (TryAcquireRole(AppProcessRole.Ui, out roleMutex))
                    {
                        _roleMutex = roleMutex;
                        _current = new AppProcessBootstrapResult
                        {
                            ShouldExit = false,
                            Role = AppProcessRole.Ui,
                            ShouldOwnStartupPipeline = !IsRoleRunning(AppProcessRole.Tray),
                            ShouldSpawnTrayCompanion = !IsRoleRunning(AppProcessRole.Tray),
                        };
                        return _current;
                    }
                }

                _current = new AppProcessBootstrapResult
                {
                    ShouldExit = true,
                    Role = desiredRole,
                };
                return _current;
            }

            _roleMutex = roleMutex;
            bool peerTrayRunning = desiredRole == AppProcessRole.Ui && IsRoleRunning(AppProcessRole.Tray);
            _current = new AppProcessBootstrapResult
            {
                ShouldExit = false,
                Role = desiredRole,
                ShouldOwnStartupPipeline = desiredRole == AppProcessRole.Ui && !peerTrayRunning,
                ShouldSpawnTrayCompanion = desiredRole == AppProcessRole.Ui && !peerTrayRunning,
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

        public static void ReleaseRole()
        {
            Mutex? mutex = Interlocked.Exchange(ref _roleMutex, null);
            if (mutex is null)
            {
                return;
            }

            try
            {
                mutex.ReleaseMutex();
            }
            catch
            {
                // Best-effort only.
            }

            try
            {
                mutex.Dispose();
            }
            catch
            {
                // Best-effort only.
            }
        }

        private static bool TryTakeOverAsElevatedUi(out AppProcessBootstrapResult? result)
        {
            result = null;
            if (!AppElevationHelper.IsProcessElevated() || !PendingElevatedStartStore.IsPending())
            {
                return false;
            }

            // Old non-elevated UI/tray are exiting after UAC. Wait and exclusively take UI ownership
            // so we do not flash-exit as a "duplicate instance" or fall into tray-only mode.
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (TryAcquireRole(AppProcessRole.Ui, out Mutex? roleMutex))
                {
                    _roleMutex = roleMutex;
                    PendingElevatedStartStore.Clear();
                    bool trayRunning = IsRoleRunning(AppProcessRole.Tray);
                    result = new AppProcessBootstrapResult
                    {
                        ShouldExit = false,
                        Role = AppProcessRole.Ui,
                        ShouldOwnStartupPipeline = true,
                        ShouldSpawnTrayCompanion = !trayRunning,
                    };
                    return true;
                }

                Thread.Sleep(150);
            }

            // Timed out still cannot own UI. Clear the flag to avoid sticky takeover loops.
            PendingElevatedStartStore.Clear();
            result = new AppProcessBootstrapResult
            {
                ShouldExit = true,
                Role = AppProcessRole.Ui,
            };
            return true;
        }

        private static void WaitUntilRoleApparentlyFree(AppProcessRole role, TimeSpan timeout)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (!IsRoleRunning(role))
                {
                    return;
                }

                Thread.Sleep(100);
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

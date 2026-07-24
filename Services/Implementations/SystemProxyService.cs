using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Services.Implementations
{
    public class SystemProxyService : ISystemProxyService
    {
        private const string InternetSettingsSubKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const string CrossProcessMutexName = @"Local\ClashWinUI.SystemProxyService";
        private const int InternetOptionPerConnectionOption = 75;
        private const int InternetOptionSettingsChanged = 39;
        private const int InternetOptionRefresh = 37;
        private const int InternetPerConnFlags = 1;
        private const int InternetPerConnProxyServer = 2;
        private const int InternetPerConnProxyBypass = 3;
        private const int InternetPerConnAutoconfigUrl = 4;
        private const int ProxyTypeDirect = 0x00000001;
        private const int ProxyTypeProxy = 0x00000002;
        private const int MaxApplyAttempts = 3;
        private const int ApplyRetryDelayMs = 80;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint WmSettingChange = 0x001A;
        private const string noneLabel = "<none>";

        private readonly IAppLogService _logService;
        private readonly object _stateGate = new();
        private readonly Mutex _crossProcessMutex;

        private bool _sessionOwnsProxy;
        private string _ownedProxyServer = string.Empty;
        private string _ownedBypassList = string.Empty;
        private SystemProxyState _previousState = SystemProxyState.Disabled();

        public SystemProxyService(IAppLogService logService)
        {
            _logService = logService;
            _crossProcessMutex = CreateCrossProcessMutex();
        }

        public Task EnableAsync(string host, int port, string bypassList, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
            int normalizedPort = port > 0 && port <= 65535 ? port : 7890;
            string proxyServer = $"{normalizedHost}:{normalizedPort}";
            string normalizedBypass = string.IsNullOrWhiteSpace(bypassList) ? "localhost;127.*" : bypassList.Trim();

            bool lockTaken = false;
            try
            {
                lockTaken = TryEnterCrossProcessMutex();
                lock (_stateGate)
                {
                    SystemProxyState currentState = GetCurrentStateUnlocked();
                    if (IsSameProxyState(currentState, proxyServer, normalizedBypass))
                    {
                        ClaimOwnershipUnlocked(proxyServer, normalizedBypass, alreadyEnabled: true);
                        return Task.CompletedTask;
                    }

                    string? lastError = null;
                    for (int attempt = 1; attempt <= MaxApplyAttempts; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!TryApplyProxyState(
                                enable: true,
                                proxyServer,
                                normalizedBypass,
                                out string? error,
                                out bool usedRegistryFallback))
                        {
                            lastError = error ?? "Unknown system proxy apply failure.";
                            _logService.Add(
                                $"System proxy enable attempt {attempt}/{MaxApplyAttempts} failed. Error={lastError}",
                                LogLevel.Warning);
                        }
                        else if (IsSameProxyState(GetCurrentStateUnlocked(), proxyServer, normalizedBypass))
                        {
                            ClaimOwnershipUnlocked(proxyServer, normalizedBypass, alreadyEnabled: false, previousState: currentState);

                            if (usedRegistryFallback)
                            {
                                _logService.Add($"System proxy enabled via registry fallback: {proxyServer}", LogLevel.Warning);
                            }
                            else
                            {
                                _logService.Add($"System proxy enabled: {proxyServer}");
                            }

                            return Task.CompletedTask;
                        }
                        else
                        {
                            lastError = "System proxy write did not stick after verification.";
                            _logService.Add(
                                $"System proxy enable attempt {attempt}/{MaxApplyAttempts} did not stick after verification.",
                                LogLevel.Warning);
                        }

                        if (attempt < MaxApplyAttempts)
                        {
                            Thread.Sleep(ApplyRetryDelayMs * attempt);
                        }
                    }

                    _logService.Add(
                        $"System proxy enable failed after {MaxApplyAttempts} attempts: {lastError ?? noneLabel}",
                        LogLevel.Warning);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.Add($"System proxy enable failed: {ex.Message}", LogLevel.Warning);
            }
            finally
            {
                if (lockTaken)
                {
                    TryExitCrossProcessMutex();
                }
            }

            return Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool lockTaken = false;
            try
            {
                lockTaken = TryEnterCrossProcessMutex();
                lock (_stateGate)
                {
                    if (!_sessionOwnsProxy)
                    {
                        return Task.CompletedTask;
                    }

                    SystemProxyState currentState = GetCurrentStateUnlocked();
                    if (currentState.IsEnabled && !IsSameProxyState(currentState, _ownedProxyServer, _ownedBypassList))
                    {
                        _logService.Add(
                            "System proxy disable skipped because current OS proxy no longer matches this session.",
                            LogLevel.Warning);
                        ClearOwnershipUnlocked();
                        return Task.CompletedTask;
                    }

                    SystemProxyState restoreState = CloneState(_previousState);
                    string? lastError = null;
                    for (int attempt = 1; attempt <= MaxApplyAttempts; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!TryApplyProxyState(
                                restoreState.IsEnabled,
                                restoreState.ProxyServer,
                                restoreState.BypassList,
                                out string? error,
                                out bool usedRegistryFallback))
                        {
                            lastError = error ?? "Unknown system proxy restore failure.";
                            _logService.Add(
                                $"System proxy restore attempt {attempt}/{MaxApplyAttempts} failed. Error={lastError}",
                                LogLevel.Warning);
                        }
                        else if (MatchesRestoreState(GetCurrentStateUnlocked(), restoreState))
                        {
                            ClearOwnershipUnlocked();

                            if (restoreState.IsEnabled)
                            {
                                string displayAddress = string.IsNullOrWhiteSpace(restoreState.ProxyServer)
                                    ? "existing proxy"
                                    : restoreState.ProxyServer;
                                if (usedRegistryFallback)
                                {
                                    _logService.Add($"System proxy restored via registry fallback: {displayAddress}", LogLevel.Warning);
                                }
                                else
                                {
                                    _logService.Add($"System proxy restored: {displayAddress}");
                                }
                            }
                            else
                            {
                                _logService.Add("System proxy disabled.");
                            }

                            return Task.CompletedTask;
                        }
                        else
                        {
                            lastError = "System proxy restore did not stick after verification.";
                            _logService.Add(
                                $"System proxy restore attempt {attempt}/{MaxApplyAttempts} did not stick after verification.",
                                LogLevel.Warning);
                        }

                        if (attempt < MaxApplyAttempts)
                        {
                            Thread.Sleep(ApplyRetryDelayMs * attempt);
                        }
                    }

                    _logService.Add(
                        $"System proxy disable failed after {MaxApplyAttempts} attempts: {lastError ?? noneLabel}",
                        LogLevel.Warning);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.Add($"System proxy disable failed: {ex.Message}", LogLevel.Warning);
            }
            finally
            {
                if (lockTaken)
                {
                    TryExitCrossProcessMutex();
                }
            }

            return Task.CompletedTask;
        }

        public SystemProxyState GetCurrentState()
        {
            bool lockTaken = false;
            try
            {
                lockTaken = TryEnterCrossProcessMutex(TimeSpan.FromMilliseconds(250));
                lock (_stateGate)
                {
                    return GetCurrentStateUnlocked();
                }
            }
            catch (Exception ex)
            {
                _logService.Add($"Read system proxy state failed: {ex.Message}", LogLevel.Warning);
                return SystemProxyState.Disabled();
            }
            finally
            {
                if (lockTaken)
                {
                    TryExitCrossProcessMutex();
                }
            }
        }

        private void ClaimOwnershipUnlocked(string proxyServer, string bypassList, bool alreadyEnabled, SystemProxyState? previousState = null)
        {
            if (!_sessionOwnsProxy)
            {
                _previousState = alreadyEnabled || previousState is null
                    ? SystemProxyState.Disabled()
                    : CloneState(previousState);
                _sessionOwnsProxy = true;
            }

            _ownedProxyServer = proxyServer;
            _ownedBypassList = bypassList;

            if (alreadyEnabled)
            {
                _logService.Add($"System proxy already enabled; claimed session ownership: {proxyServer}");
            }
        }

        private void ClearOwnershipUnlocked()
        {
            _sessionOwnsProxy = false;
            _ownedProxyServer = string.Empty;
            _ownedBypassList = string.Empty;
            _previousState = SystemProxyState.Disabled();
        }

        private SystemProxyState GetCurrentStateUnlocked()
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(InternetSettingsSubKey, writable: false);
                if (key is null)
                {
                    using RegistryKey fallbackRoot = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                    using RegistryKey? fallbackKey = fallbackRoot.OpenSubKey(InternetSettingsSubKey, writable: false);
                    if (fallbackKey is null)
                    {
                        return SystemProxyState.Disabled();
                    }

                    return ReadStateFromKey(fallbackKey);
                }

                return ReadStateFromKey(key);
            }
            catch (Exception ex)
            {
                _logService.Add($"Read system proxy state failed: {ex.Message}", LogLevel.Warning);
                return SystemProxyState.Disabled();
            }
        }

        private static SystemProxyState ReadStateFromKey(RegistryKey key)
        {
            return new SystemProxyState
            {
                IsEnabled = ReadDwordValue(key, "ProxyEnable") != 0,
                ProxyServer = NormalizeProxyServer((key.GetValue("ProxyServer") as string ?? string.Empty).Trim()),
                BypassList = (key.GetValue("ProxyOverride") as string ?? string.Empty).Trim(),
            };
        }

        private static SystemProxyState CloneState(SystemProxyState state)
        {
            return new SystemProxyState
            {
                IsEnabled = state.IsEnabled,
                ProxyServer = state.ProxyServer,
                BypassList = state.BypassList,
            };
        }

        private static bool MatchesRestoreState(SystemProxyState current, SystemProxyState restoreState)
        {
            if (!restoreState.IsEnabled)
            {
                return !current.IsEnabled;
            }

            return IsSameProxyState(current, restoreState.ProxyServer, restoreState.BypassList);
        }

        private static bool IsSameProxyState(SystemProxyState state, string proxyServer, string bypassList)
        {
            if (!state.IsEnabled)
            {
                return false;
            }

            string currentServer = NormalizeProxyServer(state.ProxyServer);
            string expectedServer = NormalizeProxyServer(proxyServer);
            if (!string.Equals(currentServer, expectedServer, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return BypassListContainsRequiredEntries(state.BypassList, bypassList);
        }

        private static bool BypassListContainsRequiredEntries(string? currentBypass, string? requiredBypass)
        {
            string current = currentBypass ?? string.Empty;
            string required = requiredBypass ?? string.Empty;
            if (string.IsNullOrWhiteSpace(required))
            {
                return true;
            }

            if (string.Equals(current.Trim(), required.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string[] requiredParts = required.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string[] currentParts = current.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string requiredPart in requiredParts)
            {
                bool found = false;
                foreach (string currentPart in currentParts)
                {
                    if (string.Equals(currentPart, requiredPart, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        private static string NormalizeProxyServer(string? proxyServer)
        {
            if (string.IsNullOrWhiteSpace(proxyServer))
            {
                return string.Empty;
            }

            string trimmed = proxyServer.Trim();
            if (trimmed.Contains('=', StringComparison.Ordinal))
            {
                string[] parts = trimmed.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string part in parts)
                {
                    int eq = part.IndexOf('=');
                    string value = eq >= 0 ? part[(eq + 1)..].Trim() : part;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return trimmed;
        }

        private static bool TryApplyProxyState(
            bool enable,
            string? proxyServer,
            string? bypassList,
            out string? error,
            out bool usedRegistryFallback)
        {
            usedRegistryFallback = false;
            bool winInetOk = TryApplyWinInetProxy(enable, proxyServer, bypassList, out string? winInetError);
            bool registryOk = TryWriteRegistryProxy(enable, proxyServer, bypassList, out string? registryError);
            NotifySystemProxyChanged();

            if (!winInetOk && !registryOk)
            {
                error = $"WinINet={winInetError ?? noneLabel}, Registry={registryError ?? noneLabel}";
                return false;
            }

            usedRegistryFallback = !winInetOk && registryOk;
            error = winInetOk ? null : registryError ?? winInetError;
            return true;
        }

        private static bool TryApplyWinInetProxy(bool enable, string? proxyServer, string? bypassList, out string? error)
        {
            error = null;

            IntPtr optionsPointer = IntPtr.Zero;
            IntPtr serverPointer = IntPtr.Zero;
            IntPtr bypassPointer = IntPtr.Zero;
            IntPtr autoConfigPointer = IntPtr.Zero;
            try
            {
                int optionCount = enable ? 4 : 1;
                int optionSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION>();
                optionsPointer = Marshal.AllocHGlobal(optionSize * optionCount);

                var options = new INTERNET_PER_CONN_OPTION[optionCount];
                options[0] = new INTERNET_PER_CONN_OPTION
                {
                    dwOption = InternetPerConnFlags,
                    Value = new INTERNET_PER_CONN_OPTION_VALUE
                    {
                        dwValue = enable ? (ProxyTypeDirect | ProxyTypeProxy) : ProxyTypeDirect
                    }
                };

                if (enable)
                {
                    serverPointer = Marshal.StringToHGlobalUni(proxyServer ?? string.Empty);
                    bypassPointer = Marshal.StringToHGlobalUni(bypassList ?? string.Empty);
                    autoConfigPointer = Marshal.StringToHGlobalUni(string.Empty);

                    options[1] = new INTERNET_PER_CONN_OPTION
                    {
                        dwOption = InternetPerConnProxyServer,
                        Value = new INTERNET_PER_CONN_OPTION_VALUE { pszValue = serverPointer }
                    };
                    options[2] = new INTERNET_PER_CONN_OPTION
                    {
                        dwOption = InternetPerConnProxyBypass,
                        Value = new INTERNET_PER_CONN_OPTION_VALUE { pszValue = bypassPointer }
                    };
                    options[3] = new INTERNET_PER_CONN_OPTION
                    {
                        dwOption = InternetPerConnAutoconfigUrl,
                        Value = new INTERNET_PER_CONN_OPTION_VALUE { pszValue = autoConfigPointer }
                    };
                }

                for (int i = 0; i < optionCount; i++)
                {
                    IntPtr current = optionsPointer + (i * optionSize);
                    Marshal.StructureToPtr(options[i], current, fDeleteOld: false);
                }

                var optionList = new INTERNET_PER_CONN_OPTION_LIST
                {
                    dwSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION_LIST>(),
                    pszConnection = IntPtr.Zero,
                    dwOptionCount = optionCount,
                    dwOptionError = 0,
                    pOptions = optionsPointer
                };

                bool setOk = InternetSetOption(IntPtr.Zero, InternetOptionPerConnectionOption, ref optionList, optionList.dwSize);
                if (!setOk)
                {
                    error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }

                if (!RefreshInternetSettings(out string? refreshError))
                {
                    error = refreshError;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (serverPointer != IntPtr.Zero) Marshal.FreeHGlobal(serverPointer);
                if (bypassPointer != IntPtr.Zero) Marshal.FreeHGlobal(bypassPointer);
                if (autoConfigPointer != IntPtr.Zero) Marshal.FreeHGlobal(autoConfigPointer);
                if (optionsPointer != IntPtr.Zero) Marshal.FreeHGlobal(optionsPointer);
            }
        }

        private static bool TryWriteRegistryProxy(bool enable, string? proxyServer, string? bypassList, out string? error)
        {
            error = null;
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey key = root.CreateSubKey(InternetSettingsSubKey, writable: true)
                    ?? throw new InvalidOperationException("Unable to open Internet Settings registry key.");

                key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
                if (enable)
                {
                    key.SetValue("ProxyServer", proxyServer ?? string.Empty, RegistryValueKind.String);
                    key.SetValue("ProxyOverride", bypassList ?? string.Empty, RegistryValueKind.String);
                    key.SetValue("AutoConfigURL", string.Empty, RegistryValueKind.String);
                }

                return RefreshInternetSettings(out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool RefreshInternetSettings(out string? error)
        {
            error = null;

            bool changed = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            if (!changed)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            bool refreshed = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
            if (!refreshed)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            return true;
        }

        private static void NotifySystemProxyChanged()
        {
            try
            {
                _ = SendMessageTimeout(
                    new IntPtr(0xFFFF),
                    WmSettingChange,
                    IntPtr.Zero,
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings",
                    SmtoAbortIfHung,
                    1000,
                    out _);
            }
            catch
            {
            }
        }

        private static int ReadDwordValue(RegistryKey key, string valueName)
        {
            object? value = key.GetValue(valueName);
            return value switch
            {
                int intValue => intValue,
                byte byteValue => byteValue,
                short shortValue => shortValue,
                long longValue => (int)longValue,
                _ => 0,
            };
        }

        private static Mutex CreateCrossProcessMutex()
        {
            try
            {
                return new Mutex(initiallyOwned: false, CrossProcessMutexName);
            }
            catch
            {
                return new Mutex(initiallyOwned: false);
            }
        }

        private bool TryEnterCrossProcessMutex(TimeSpan? timeout = null)
        {
            TimeSpan wait = timeout ?? TimeSpan.FromSeconds(3);
            try
            {
                return _crossProcessMutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
            catch (Exception ex)
            {
                _logService.Add($"System proxy mutex wait failed: {ex.Message}", LogLevel.Warning);
                return false;
            }
        }

        private void TryExitCrossProcessMutex()
        {
            try
            {
                _crossProcessMutex.ReleaseMutex();
            }
            catch
            {
            }
        }

        [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, ref INTERNET_PER_CONN_OPTION_LIST lpBuffer, int dwBufferLength);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            string lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct INTERNET_PER_CONN_OPTION_LIST
        {
            public int dwSize;
            public IntPtr pszConnection;
            public int dwOptionCount;
            public int dwOptionError;
            public IntPtr pOptions;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct INTERNET_PER_CONN_OPTION
        {
            public int dwOption;
            public INTERNET_PER_CONN_OPTION_VALUE Value;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        private struct INTERNET_PER_CONN_OPTION_VALUE
        {
            [FieldOffset(0)]
            public int dwValue;

            [FieldOffset(0)]
            public IntPtr pszValue;
        }
    }
}


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
        private const int InternetOptionPerConnectionOption = 75;
        private const int InternetOptionSettingsChanged = 39;
        private const int InternetOptionRefresh = 37;

        private const int InternetPerConnFlags = 1;
        private const int InternetPerConnProxyServer = 2;
        private const int InternetPerConnProxyBypass = 3;

        private const int ProxyTypeDirect = 0x00000001;
        private const int ProxyTypeProxy = 0x00000002;

        private readonly IAppLogService _logService;
        private readonly object _stateGate = new();

        private bool _sessionOwnsProxy;
        private SystemProxyState _previousState = SystemProxyState.Disabled();

        public SystemProxyService(IAppLogService logService)
        {
            _logService = logService;
        }

        public Task EnableAsync(string host, int port, string bypassList, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
            int normalizedPort = port > 0 && port <= 65535 ? port : 7890;
            string proxyServer = $"{normalizedHost}:{normalizedPort}";
            string normalizedBypass = string.IsNullOrWhiteSpace(bypassList) ? "localhost;127.*" : bypassList.Trim();
            SystemProxyState currentState = GetCurrentState();

            if (!GetSessionOwnsProxy() && IsSameProxyState(currentState, proxyServer, normalizedBypass))
            {
                return Task.CompletedTask;
            }

            try
            {
                if (!TryApplyProxyState(enable: true, proxyServer, normalizedBypass, out string? error, out bool usedRegistryFallback))
                {
                    _logService.Add(
                        $"System proxy enable failed. Error={error ?? "<none>"}",
                        LogLevel.Warning);
                    return Task.CompletedTask;
                }

                lock (_stateGate)
                {
                    if (!_sessionOwnsProxy)
                    {
                        _previousState = CloneState(currentState);
                    }

                    _sessionOwnsProxy = true;
                }

                if (usedRegistryFallback)
                {
                    _logService.Add($"System proxy enabled via registry fallback: {proxyServer}", LogLevel.Warning);
                }
                else
                {
                    _logService.Add($"System proxy enabled: {proxyServer}");
                }
            }
            catch (Exception ex)
            {
                _logService.Add($"System proxy enable failed: {ex.Message}", LogLevel.Warning);
            }

            return Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SystemProxyState restoreState;
            lock (_stateGate)
            {
                if (!_sessionOwnsProxy)
                {
                    return Task.CompletedTask;
                }

                restoreState = CloneState(_previousState);
            }

            try
            {
                if (!TryApplyProxyState(
                    restoreState.IsEnabled,
                    restoreState.ProxyServer,
                    restoreState.BypassList,
                    out string? error,
                    out bool usedRegistryFallback))
                {
                    _logService.Add(
                        $"System proxy restore failed. Error={error ?? "<none>"}",
                        LogLevel.Warning);
                    return Task.CompletedTask;
                }

                lock (_stateGate)
                {
                    _sessionOwnsProxy = false;
                    _previousState = SystemProxyState.Disabled();
                }

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
            }
            catch (Exception ex)
            {
                _logService.Add($"System proxy disable failed: {ex.Message}", LogLevel.Warning);
            }

            return Task.CompletedTask;
        }

        public SystemProxyState GetCurrentState()
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                using RegistryKey? key = root.OpenSubKey(InternetSettingsSubKey, writable: false);
                if (key is null)
                {
                    return SystemProxyState.Disabled();
                }

                return new SystemProxyState
                {
                    IsEnabled = ReadDwordValue(key, "ProxyEnable") != 0,
                    ProxyServer = (key.GetValue("ProxyServer") as string ?? string.Empty).Trim(),
                    BypassList = (key.GetValue("ProxyOverride") as string ?? string.Empty).Trim(),
                };
            }
            catch (Exception ex)
            {
                _logService.Add($"Read system proxy state failed: {ex.Message}", LogLevel.Warning);
                return SystemProxyState.Disabled();
            }
        }

        private bool GetSessionOwnsProxy()
        {
            lock (_stateGate)
            {
                return _sessionOwnsProxy;
            }
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

        private static bool IsSameProxyState(SystemProxyState state, string proxyServer, string bypassList)
        {
            return state.IsEnabled
                && string.Equals(state.ProxyServer?.Trim(), proxyServer.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(state.BypassList?.Trim(), bypassList.Trim(), StringComparison.OrdinalIgnoreCase);
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

            if (!winInetOk && !registryOk)
            {
                error = $"WinINet={winInetError ?? "<none>"}, Registry={registryError ?? "<none>"}";
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
            try
            {
                int optionCount = enable ? 3 : 1;
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

                    options[1] = new INTERNET_PER_CONN_OPTION
                    {
                        dwOption = InternetPerConnProxyServer,
                        Value = new INTERNET_PER_CONN_OPTION_VALUE
                        {
                            pszValue = serverPointer
                        }
                    };

                    options[2] = new INTERNET_PER_CONN_OPTION
                    {
                        dwOption = InternetPerConnProxyBypass,
                        Value = new INTERNET_PER_CONN_OPTION_VALUE
                        {
                            pszValue = bypassPointer
                        }
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
                if (serverPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(serverPointer);
                }

                if (bypassPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(bypassPointer);
                }

                if (optionsPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(optionsPointer);
                }
            }
        }

        private static bool TryWriteRegistryProxy(bool enable, string? proxyServer, string? bypassList, out string? error)
        {
            error = null;
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                using RegistryKey key = root.CreateSubKey(InternetSettingsSubKey, writable: true)
                    ?? throw new InvalidOperationException("Unable to open Internet Settings registry key.");

                key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
                if (enable)
                {
                    key.SetValue("ProxyServer", proxyServer ?? string.Empty, RegistryValueKind.String);
                    key.SetValue("ProxyOverride", bypassList ?? string.Empty, RegistryValueKind.String);
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

        private static int ReadDwordValue(RegistryKey key, string valueName)
        {
            object? value = key.GetValue(valueName);
            return value switch
            {
                int intValue => intValue,
                byte byteValue => byteValue,
                short shortValue => shortValue,
                _ => 0,
            };
        }

        [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, ref INTERNET_PER_CONN_OPTION_LIST lpBuffer, int dwBufferLength);

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
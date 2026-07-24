using ClashWinUI.Models;
using ClashWinUI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClashWinUI.Helpers
{
    internal static class MihomoPortConflictResolver
    {
        private const int AfInet = 2;
        private const int TcpTableOwnerPidListener = 3;
        private const int ErrorInsufficientBuffer = 122;

        private static readonly Regex MixedPortRegex = new(@"^\uFEFF?mixed-port\s*:\s*(?<value>\d+)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PortRegex = new(@"^\uFEFF?port\s*:\s*(?<value>\d+)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SocksPortRegex = new(@"^\uFEFF?socks-port\s*:\s*(?<value>\d+)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RedirPortRegex = new(@"^\uFEFF?redir-port\s*:\s*(?<value>\d+)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TproxyPortRegex = new(@"^\uFEFF?tproxy-port\s*:\s*(?<value>\d+)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ExternalControllerPortRegex = new(@"^\uFEFF?external-controller\s*:\s*.*:(?<value>\d+)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> SafeOwnerNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "mihomo",
            "clash",
            "clash-meta",
            "clashmeta",
        };

        private static readonly Mutex CrossProcessStartMutex = new(initiallyOwned: false, @"Local\ClashWinUI.MihomoStart");

        public static IDisposable? TryEnterStartGate(TimeSpan timeout)
        {
            try
            {
                if (!CrossProcessStartMutex.WaitOne(timeout))
                {
                    return null;
                }

                return new MutexReleaser(CrossProcessStartMutex);
            }
            catch (AbandonedMutexException)
            {
                return new MutexReleaser(CrossProcessStartMutex);
            }
            catch
            {
                return null;
            }
        }

        public static IReadOnlyCollection<int> ResolveRequiredPorts(string configPath, int controllerPort, int defaultProxyPort)
        {
            var ports = new HashSet<int> { controllerPort };
            if (defaultProxyPort > 0 && defaultProxyPort <= 65535)
            {
                ports.Add(defaultProxyPort);
            }

            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                return ports;
            }

            try
            {
                foreach (string line in File.ReadLines(configPath))
                {
                    TryAddPort(ports, MixedPortRegex, line);
                    TryAddPort(ports, PortRegex, line);
                    TryAddPort(ports, SocksPortRegex, line);
                    TryAddPort(ports, RedirPortRegex, line);
                    TryAddPort(ports, TproxyPortRegex, line);
                    TryAddPort(ports, ExternalControllerPortRegex, line);
                }
            }
            catch
            {
                // Best-effort only.
            }

            return ports;
        }

        public static int FreeConflictingPorts(
            string configPath,
            string kernelPath,
            int controllerPort,
            int defaultProxyPort,
            IAppLogService logService)
        {
            IReadOnlyCollection<int> requiredPorts = ResolveRequiredPorts(configPath, controllerPort, defaultProxyPort);
            Dictionary<int, HashSet<int>> listeners = GetListeningPidsByPort(requiredPorts);
            if (listeners.Count == 0)
            {
                return 0;
            }

            int currentPid = Environment.ProcessId;
            string normalizedKernel = NormalizePath(kernelPath);
            int terminated = 0;
            var terminatedPids = new HashSet<int>();

            foreach ((int port, HashSet<int> pids) in listeners)
            {
                foreach (int pid in pids)
                {
                    if (pid <= 0 || pid == currentPid || !terminatedPids.Add(pid))
                    {
                        continue;
                    }

                    if (!TryGetProcessInfo(pid, out string processName, out string? processPath))
                    {
                        logService.Add($"Port {port} is held by PID={pid}, but process details are unavailable.", LogLevel.Warning);
                        continue;
                    }

                    if (!IsSafeToTerminate(processName, processPath, normalizedKernel))
                    {
                        logService.Add(
                            $"Port {port} is occupied by non-Clash process {processName} (PID={pid}). Skip auto-terminate.",
                            LogLevel.Warning);
                        continue;
                    }

                    if (TryTerminatePid(pid))
                    {
                        terminated++;
                        logService.Add(
                            $"Terminated conflicting Clash/Mihomo process holding port {port}. Name={processName}, PID={pid}",
                            LogLevel.Warning);
                    }
                    else
                    {
                        logService.Add(
                            $"Failed to terminate conflicting process holding port {port}. Name={processName}, PID={pid}",
                            LogLevel.Warning);
                    }
                }
            }

            return terminated;
        }

        public static bool LooksLikePortBindConflict(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            return line.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase)
                || line.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || line.Contains("bind: An attempt was made to access a socket in a way forbidden by its access permissions", StringComparison.OrdinalIgnoreCase)
                || line.Contains("端口被占用", StringComparison.OrdinalIgnoreCase)
                || line.Contains("地址已在使用", StringComparison.OrdinalIgnoreCase)
                || (line.Contains("listen tcp", StringComparison.OrdinalIgnoreCase)
                    && (line.Contains("bind:", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("address already in use", StringComparison.OrdinalIgnoreCase)));
        }

        private static void TryAddPort(HashSet<int> ports, Regex regex, string line)
        {
            Match match = regex.Match(line.Trim());
            if (!match.Success)
            {
                return;
            }

            if (int.TryParse(match.Groups["value"].Value, out int port)
                && port > 0
                && port <= 65535)
            {
                ports.Add(port);
            }
        }

        private static bool IsSafeToTerminate(string processName, string? processPath, string normalizedKernelPath)
        {
            if (SafeOwnerNames.Contains(processName))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(processPath)
                && !string.IsNullOrWhiteSpace(normalizedKernelPath)
                && string.Equals(NormalizePath(processPath), normalizedKernelPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(processPath))
            {
                string fileName = Path.GetFileNameWithoutExtension(processPath);
                if (SafeOwnerNames.Contains(fileName))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetProcessInfo(int pid, out string processName, out string? processPath)
        {
            processName = string.Empty;
            processPath = null;
            try
            {
                using Process process = Process.GetProcessById(pid);
                processName = process.ProcessName;
                try
                {
                    processPath = process.MainModule?.FileName;
                }
                catch
                {
                    processPath = null;
                }

                return !string.IsNullOrWhiteSpace(processName);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryTerminatePid(int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }

                process.Kill(entireProcessTree: true);
                return process.WaitForExit(4000);
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<int, HashSet<int>> GetListeningPidsByPort(IReadOnlyCollection<int> ports)
        {
            var result = new Dictionary<int, HashSet<int>>();
            if (ports.Count == 0)
            {
                return result;
            }

            var wanted = new HashSet<int>(ports);
            int bufferSize = 0;
            uint ret = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, AfInet, TcpTableOwnerPidListener, 0);
            if (ret != 0 && ret != ErrorInsufficientBuffer)
            {
                return result;
            }

            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                ret = GetExtendedTcpTable(buffer, ref bufferSize, true, AfInet, TcpTableOwnerPidListener, 0);
                if (ret != 0)
                {
                    return result;
                }

                int rowCount = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = buffer + 4;
                int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (int i = 0; i < rowCount; i++)
                {
                    MibTcpRowOwnerPid row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                    int port = (int)(((row.localPort & 0xFF00u) >> 8) | ((row.localPort & 0x00FFu) << 8));
                    if (wanted.Contains(port) && row.owningPid > 0)
                    {
                        if (!result.TryGetValue(port, out HashSet<int>? pids))
                        {
                            pids = new HashSet<int>();
                            result[port] = pids;
                        }

                        pids.Add((int)row.owningPid);
                    }

                    rowPtr += rowSize;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return result;
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path.Trim().Trim('"'));
            }
            catch
            {
                return path.Trim();
            }
        }

        private sealed class MutexReleaser : IDisposable
        {
            private Mutex? _mutex;

            public MutexReleaser(Mutex mutex)
            {
                _mutex = mutex;
            }

            public void Dispose()
            {
                Mutex? mutex = Interlocked.Exchange(ref _mutex, null);
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
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int dwOutBufLen,
            bool sort,
            int ipVersion,
            int tblClass,
            uint reserved);
    }
}

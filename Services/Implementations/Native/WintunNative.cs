using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ClashWinUI.Services.Implementations.Native
{
    internal sealed class WintunNative : IDisposable
    {
        private const uint NoError = 0;

        private readonly IntPtr _libraryHandle;
        private readonly WintunGetRunningDriverVersionDelegate _getRunningDriverVersion;
        private readonly WintunOpenAdapterDelegate _openAdapter;
        private readonly WintunCloseAdapterDelegate _closeAdapter;
        private readonly WintunGetAdapterLuidDelegate? _getAdapterLuid;
        private bool _disposed;

        private WintunNative(
            IntPtr libraryHandle,
            WintunGetRunningDriverVersionDelegate getRunningDriverVersion,
            WintunOpenAdapterDelegate openAdapter,
            WintunCloseAdapterDelegate closeAdapter,
            WintunGetAdapterLuidDelegate? getAdapterLuid)
        {
            _libraryHandle = libraryHandle;
            _getRunningDriverVersion = getRunningDriverVersion;
            _openAdapter = openAdapter;
            _closeAdapter = closeAdapter;
            _getAdapterLuid = getAdapterLuid;
        }

        public static WintunNative Load(string dllPath)
        {
            if (string.IsNullOrWhiteSpace(dllPath))
            {
                throw new ArgumentException("Wintun dll path is empty.", nameof(dllPath));
            }

            string fullPath = Path.GetFullPath(dllPath.Trim());
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Wintun dll not found.", fullPath);
            }

            IntPtr libraryHandle = NativeLibrary.Load(fullPath);
            try
            {
                return new WintunNative(
                    libraryHandle,
                    GetDelegate<WintunGetRunningDriverVersionDelegate>(libraryHandle, "WintunGetRunningDriverVersion"),
                    GetDelegate<WintunOpenAdapterDelegate>(libraryHandle, "WintunOpenAdapter"),
                    GetDelegate<WintunCloseAdapterDelegate>(libraryHandle, "WintunCloseAdapter"),
                    GetOptionalDelegate<WintunGetAdapterLuidDelegate>(libraryHandle, "WintunGetAdapterLuid"));
            }
            catch
            {
                NativeLibrary.Free(libraryHandle);
                throw;
            }
        }

        public uint GetRunningDriverVersion()
        {
            ThrowIfDisposed();
            return _getRunningDriverVersion();
        }

        public bool TryOpenAdapter(string name, out AdapterHandle? adapter, out string? error)
        {
            ThrowIfDisposed();

            adapter = null;
            error = null;

            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Adapter name is empty.";
                return false;
            }

            IntPtr adapterHandle = _openAdapter(name.Trim());
            if (adapterHandle == IntPtr.Zero)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            adapter = new AdapterHandle(this, adapterHandle, name.Trim());
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_libraryHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(_libraryHandle);
            }
        }

        private ulong GetAdapterLuid(IntPtr handle, string name)
        {
            ThrowIfDisposed();

            if (_getAdapterLuid is not null)
            {
                var adapterLuid = new NetLuid();
                _getAdapterLuid(handle, out adapterLuid);
                return adapterLuid.Value;
            }

            uint result = ConvertInterfaceAliasToLuid(name, out NetLuid luid);
            if (result != NoError)
            {
                throw new Win32Exception(unchecked((int)result));
            }

            return luid.Value;
        }

        private void CloseAdapter(IntPtr handle)
        {
            if (handle == IntPtr.Zero || _disposed)
            {
                return;
            }

            _closeAdapter(handle);
        }

        private static T GetDelegate<T>(IntPtr libraryHandle, string exportName) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(libraryHandle, exportName, out IntPtr export) || export == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException($"Missing Wintun export: {exportName}");
            }

            return Marshal.GetDelegateForFunctionPointer<T>(export);
        }

        private static T? GetOptionalDelegate<T>(IntPtr libraryHandle, string exportName) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(libraryHandle, exportName, out IntPtr export) || export == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.GetDelegateForFunctionPointer<T>(export);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
        private static extern uint ConvertInterfaceAliasToLuid(string interfaceAlias, out NetLuid interfaceLuid);

        [StructLayout(LayoutKind.Sequential)]
        private struct NetLuid
        {
            public ulong Value;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
        private delegate uint WintunGetRunningDriverVersionDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
        private delegate IntPtr WintunOpenAdapterDelegate(string name);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WintunCloseAdapterDelegate(IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WintunGetAdapterLuidDelegate(IntPtr adapter, out NetLuid luid);

        internal sealed class AdapterHandle : IDisposable
        {
            private readonly WintunNative _owner;
            private IntPtr _handle;

            internal AdapterHandle(WintunNative owner, IntPtr handle, string name)
            {
                _owner = owner;
                _handle = handle;
                Name = name;
            }

            public string Name { get; }

            public ulong GetLuid()
            {
                ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
                return _owner.GetAdapterLuid(_handle, Name);
            }

            public void Dispose()
            {
                if (_handle == IntPtr.Zero)
                {
                    return;
                }

                _owner.CloseAdapter(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}

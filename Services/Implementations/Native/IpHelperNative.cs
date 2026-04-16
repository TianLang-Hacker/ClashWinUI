using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClashWinUI.Services.Implementations.Native
{
    internal static class IpHelperNative
    {
        private const int AddressFamilyUnspecified = 0;
        internal const int AddressFamilyInet = 2;
        internal const int AddressFamilyInet6 = 23;
        private const uint NoError = 0;
        private const uint Ipv4HalfDefaultUpper = 0x00000080;
        private const string ManagedParserVersion = "managed-offsetof-v2";
        private const string InterfaceTableSource = "GetIpInterfaceTable(AF_UNSPEC)/managed-offsetof-v2";
        private const string RouteTableSource = "GetIpForwardTable2(AF_UNSPEC)/managed-offsetof-v2";
        private const string SkippedSource = "Skipped (no interface identifier)";

        private static readonly int IpInterfaceTableRowOffset = (int)Marshal.OffsetOf<MibIpInterfaceTableLayout>(nameof(MibIpInterfaceTableLayout.Table));
        private static readonly int IpInterfaceRowSize = Marshal.SizeOf<MibIpInterfaceRowLayout>();
        private static readonly int IpInterfaceFamilyOffset = (int)Marshal.OffsetOf<MibIpInterfaceRowLayout>(nameof(MibIpInterfaceRowLayout.Family));
        private static readonly int IpInterfaceLuidOffset = (int)Marshal.OffsetOf<MibIpInterfaceRowLayout>(nameof(MibIpInterfaceRowLayout.InterfaceLuid));
        private static readonly int IpInterfaceIndexOffset = (int)Marshal.OffsetOf<MibIpInterfaceRowLayout>(nameof(MibIpInterfaceRowLayout.InterfaceIndex));

        private static readonly int IpForwardTableRowOffset = (int)Marshal.OffsetOf<MibIpForwardTable2Layout>(nameof(MibIpForwardTable2Layout.Table));
        private static readonly int IpForwardRowSize = Marshal.SizeOf<MibIpForwardRow2Layout>();
        private static readonly int IpForwardLuidOffset = (int)Marshal.OffsetOf<MibIpForwardRow2Layout>(nameof(MibIpForwardRow2Layout.InterfaceLuid));
        private static readonly int IpForwardIndexOffset = (int)Marshal.OffsetOf<MibIpForwardRow2Layout>(nameof(MibIpForwardRow2Layout.InterfaceIndex));
        private static readonly int IpForwardDestinationPrefixOffset = (int)Marshal.OffsetOf<MibIpForwardRow2Layout>(nameof(MibIpForwardRow2Layout.DestinationPrefix));
        private static readonly int IpAddressPrefixPrefixOffset = (int)Marshal.OffsetOf<IpAddressPrefixLayout>(nameof(IpAddressPrefixLayout.Prefix));
        private static readonly int IpAddressPrefixPrefixLengthOffset = (int)Marshal.OffsetOf<IpAddressPrefixLayout>(nameof(IpAddressPrefixLayout.PrefixLength));
        private static readonly int SockAddrFamilyOffset = (int)Marshal.OffsetOf<SockAddrInetLayout>(nameof(SockAddrInetLayout.Family));
        private static readonly int SockAddrIpv4AddressOffset = (int)Marshal.OffsetOf<SockAddrInetLayout>(nameof(SockAddrInetLayout.AddressOrFlowInfo));
        private static readonly int SockAddrIpv6Word1Offset = (int)Marshal.OffsetOf<SockAddrInetLayout>(nameof(SockAddrInetLayout.Word1));
        private static readonly int SockAddrIpv6Word2Offset = (int)Marshal.OffsetOf<SockAddrInetLayout>(nameof(SockAddrInetLayout.Word2));
        private static readonly int SockAddrIpv6Word3Offset = (int)Marshal.OffsetOf<SockAddrInetLayout>(nameof(SockAddrInetLayout.Word3));
        private static readonly int SockAddrIpv6Word4Offset = (int)Marshal.OffsetOf<SockAddrInetLayout>(nameof(SockAddrInetLayout.Word4));

        internal const string RuntimeInspectionFailureMessage = "IP Helper runtime inspection failed.";

        internal static bool TryConvertInterfaceLuidToIndex(ulong interfaceLuid, out uint interfaceIndex, out string error)
        {
            interfaceIndex = 0;
            error = string.Empty;

            try
            {
                var luid = new NetLuid { Value = interfaceLuid };
                uint result = ConvertInterfaceLuidToIndex(ref luid, out interfaceIndex);
                if (result != NoError)
                {
                    throw new Win32Exception(unchecked((int)result));
                }

                return true;
            }
            catch (Exception ex)
            {
                error = NormalizeScalar(ex.Message);
                return false;
            }
        }

        internal static RuntimeInspectionResult InspectRuntimeInterface(ulong? interfaceLuid, uint interfaceIndex)
        {
            if (!interfaceLuid.HasValue && interfaceIndex == 0)
            {
                return new RuntimeInspectionResult(
                    IpHelperReadSucceeded: true,
                    HasIpInterface: false,
                    RouteAttached: false,
                    IpInterfaceCheckSource: SkippedSource,
                    RouteTableReadSource: SkippedSource,
                    InteropError: string.Empty,
                    InterfaceSnapshots: Array.Empty<IpInterfaceSnapshot>(),
                    RouteSnapshots: Array.Empty<IpRouteSnapshot>(),
                    InterfaceEntryCount: 0,
                    RouteEntryCount: 0,
                    ParserVersion: ManagedParserVersion);
            }

            bool interfaceReadSucceeded = TryReadIpInterfaceSnapshots(
                out IReadOnlyList<IpInterfaceSnapshot> interfaceSnapshots,
                out string interfaceError,
                out int interfaceEntryCount);
            bool routeReadSucceeded = TryReadRouteSnapshots(
                out IReadOnlyList<IpRouteSnapshot> routeSnapshots,
                out string routeError,
                out int routeEntryCount);

            bool hasIpInterface = false;
            if (interfaceReadSucceeded)
            {
                foreach (IpInterfaceSnapshot snapshot in interfaceSnapshots)
                {
                    if (!MatchesInterface(snapshot.InterfaceLuid, snapshot.InterfaceIndex, interfaceLuid, interfaceIndex))
                    {
                        continue;
                    }

                    hasIpInterface = true;
                    break;
                }
            }

            bool routeAttached = routeReadSucceeded && HasDefaultRouteAttachment(routeSnapshots, interfaceLuid, interfaceIndex);
            if (routeAttached)
            {
                hasIpInterface = true;
            }

            return new RuntimeInspectionResult(
                IpHelperReadSucceeded: interfaceReadSucceeded && routeReadSucceeded,
                HasIpInterface: hasIpInterface,
                RouteAttached: routeAttached,
                IpInterfaceCheckSource: interfaceReadSucceeded ? InterfaceTableSource : $"{InterfaceTableSource} failed",
                RouteTableReadSource: routeReadSucceeded ? RouteTableSource : $"{RouteTableSource} failed",
                InteropError: CombineErrors(
                    interfaceReadSucceeded ? string.Empty : $"{InterfaceTableSource}: {interfaceError}",
                    routeReadSucceeded ? string.Empty : $"{RouteTableSource}: {routeError}"),
                InterfaceSnapshots: interfaceSnapshots,
                RouteSnapshots: routeSnapshots,
                InterfaceEntryCount: interfaceEntryCount,
                RouteEntryCount: routeEntryCount,
                ParserVersion: ManagedParserVersion);
        }

        private static bool TryReadIpInterfaceSnapshots(
            out IReadOnlyList<IpInterfaceSnapshot> snapshots,
            out string error,
            out int entryCount)
        {
            snapshots = Array.Empty<IpInterfaceSnapshot>();
            error = string.Empty;
            entryCount = 0;

            IntPtr tablePointer = IntPtr.Zero;
            try
            {
                uint result = GetIpInterfaceTable(AddressFamilyUnspecified, out tablePointer);
                if (result != NoError)
                {
                    throw new Win32Exception(unchecked((int)result));
                }

                entryCount = ReadEntryCount(tablePointer);
                var entries = new IpInterfaceSnapshot[entryCount];

                for (int index = 0; index < entryCount; index++)
                {
                    IntPtr rowPointer = GetRowPointer(tablePointer, IpInterfaceTableRowOffset, IpInterfaceRowSize, index);
                    entries[index] = new IpInterfaceSnapshot(
                        Family: ReadUInt16(rowPointer, IpInterfaceFamilyOffset),
                        InterfaceLuid: ReadUInt64(rowPointer, IpInterfaceLuidOffset),
                        InterfaceIndex: ReadUInt32(rowPointer, IpInterfaceIndexOffset));
                }

                snapshots = entries;
                return true;
            }
            catch (Exception ex)
            {
                error = NormalizeScalar(ex.Message);
                return false;
            }
            finally
            {
                if (tablePointer != IntPtr.Zero)
                {
                    FreeMibTable(tablePointer);
                }
            }
        }

        private static bool TryReadRouteSnapshots(
            out IReadOnlyList<IpRouteSnapshot> snapshots,
            out string error,
            out int entryCount)
        {
            snapshots = Array.Empty<IpRouteSnapshot>();
            error = string.Empty;
            entryCount = 0;

            IntPtr tablePointer = IntPtr.Zero;
            try
            {
                uint result = GetIpForwardTable2(AddressFamilyUnspecified, out tablePointer);
                if (result != NoError)
                {
                    throw new Win32Exception(unchecked((int)result));
                }

                entryCount = ReadEntryCount(tablePointer);
                var entries = new IpRouteSnapshot[entryCount];

                for (int index = 0; index < entryCount; index++)
                {
                    IntPtr rowPointer = GetRowPointer(tablePointer, IpForwardTableRowOffset, IpForwardRowSize, index);
                    entries[index] = ReadRouteSnapshot(rowPointer);
                }

                snapshots = entries;
                return true;
            }
            catch (Exception ex)
            {
                error = NormalizeScalar(ex.Message);
                return false;
            }
            finally
            {
                if (tablePointer != IntPtr.Zero)
                {
                    FreeMibTable(tablePointer);
                }
            }
        }

        private static IpRouteSnapshot ReadRouteSnapshot(IntPtr rowPointer)
        {
            ulong interfaceLuid = ReadUInt64(rowPointer, IpForwardLuidOffset);
            uint interfaceIndex = ReadUInt32(rowPointer, IpForwardIndexOffset);
            int prefixPointerOffset = checked(IpForwardDestinationPrefixOffset + IpAddressPrefixPrefixOffset);
            ushort addressFamily = ReadUInt16(rowPointer, checked(prefixPointerOffset + SockAddrFamilyOffset));
            byte prefixLength = ReadByte(rowPointer, checked(IpForwardDestinationPrefixOffset + IpAddressPrefixPrefixLengthOffset));

            uint ipv4Address = 0;
            In6Addr ipv6Address = In6Addr.Zero;

            switch (addressFamily)
            {
                case AddressFamilyInet:
                    ipv4Address = ReadUInt32(rowPointer, checked(prefixPointerOffset + SockAddrIpv4AddressOffset));
                    break;

                case AddressFamilyInet6:
                    ipv6Address = ReadIpv6Address(rowPointer, prefixPointerOffset);
                    break;
            }

            return new IpRouteSnapshot(
                InterfaceLuid: interfaceLuid,
                InterfaceIndex: interfaceIndex,
                AddressFamily: addressFamily,
                PrefixLength: prefixLength,
                Ipv4Address: ipv4Address,
                Ipv6Address: ipv6Address);
        }

        private static In6Addr ReadIpv6Address(IntPtr basePointer, int sockaddrOffset)
        {
            uint word1 = ReadUInt32(basePointer, checked(sockaddrOffset + SockAddrIpv6Word1Offset));
            uint word2 = ReadUInt32(basePointer, checked(sockaddrOffset + SockAddrIpv6Word2Offset));
            uint word3 = ReadUInt32(basePointer, checked(sockaddrOffset + SockAddrIpv6Word3Offset));
            uint word4 = ReadUInt32(basePointer, checked(sockaddrOffset + SockAddrIpv6Word4Offset));

            ulong low64 = ((ulong)word2 << 32) | word1;
            ulong high64 = ((ulong)word4 << 32) | word3;
            return new In6Addr(low64, high64);
        }

        private static bool HasDefaultRouteAttachment(
            IReadOnlyList<IpRouteSnapshot> routeSnapshots,
            ulong? interfaceLuid,
            uint interfaceIndex)
        {
            bool hasIpv4Default = false;
            bool hasIpv4LowerHalf = false;
            bool hasIpv4UpperHalf = false;
            bool hasIpv6Default = false;
            bool hasIpv6LowerHalf = false;
            bool hasIpv6UpperHalf = false;

            foreach (IpRouteSnapshot snapshot in routeSnapshots)
            {
                if (!MatchesInterface(snapshot.InterfaceLuid, snapshot.InterfaceIndex, interfaceLuid, interfaceIndex))
                {
                    continue;
                }

                switch (snapshot.AddressFamily)
                {
                    case AddressFamilyInet:
                        if (snapshot.PrefixLength == 0 && snapshot.Ipv4Address == 0)
                        {
                            hasIpv4Default = true;
                            break;
                        }

                        if (snapshot.PrefixLength == 1)
                        {
                            if (snapshot.Ipv4Address == 0)
                            {
                                hasIpv4LowerHalf = true;
                            }
                            else if (snapshot.Ipv4Address == Ipv4HalfDefaultUpper)
                            {
                                hasIpv4UpperHalf = true;
                            }
                        }

                        break;

                    case AddressFamilyInet6:
                        if (snapshot.PrefixLength == 0 && snapshot.Ipv6Address.IsZero)
                        {
                            hasIpv6Default = true;
                            break;
                        }

                        if (snapshot.PrefixLength == 1)
                        {
                            if (snapshot.Ipv6Address.IsZero)
                            {
                                hasIpv6LowerHalf = true;
                            }
                            else if (snapshot.Ipv6Address.IsUpperHalfSplitDefault)
                            {
                                hasIpv6UpperHalf = true;
                            }
                        }

                        break;
                }
            }

            return hasIpv4Default
                || (hasIpv4LowerHalf && hasIpv4UpperHalf)
                || hasIpv6Default
                || (hasIpv6LowerHalf && hasIpv6UpperHalf);
        }

        private static bool MatchesInterface(ulong rowLuid, uint rowIndex, ulong? targetLuid, uint targetIndex)
        {
            if (targetLuid.HasValue && rowLuid == targetLuid.Value)
            {
                return true;
            }

            return targetIndex != 0 && rowIndex == targetIndex;
        }

        private static int ReadEntryCount(IntPtr tablePointer)
        {
            int entryCount = Marshal.ReadInt32(tablePointer);
            if (entryCount < 0)
            {
                throw new InvalidOperationException("The IP Helper table returned a negative entry count.");
            }

            return entryCount;
        }

        private static IntPtr GetRowPointer(IntPtr tablePointer, int rowOffset, int rowSize, int index)
        {
            long byteOffset = checked(rowOffset + ((long)rowSize * index));
            if (byteOffset > int.MaxValue)
            {
                throw new InvalidOperationException("The IP Helper table offset exceeded the supported range.");
            }

            return IntPtr.Add(tablePointer, (int)byteOffset);
        }

        private static ushort ReadUInt16(IntPtr pointer, int offset)
        {
            return unchecked((ushort)Marshal.ReadInt16(pointer, offset));
        }

        private static uint ReadUInt32(IntPtr pointer, int offset)
        {
            return unchecked((uint)Marshal.ReadInt32(pointer, offset));
        }

        private static ulong ReadUInt64(IntPtr pointer, int offset)
        {
            return unchecked((ulong)Marshal.ReadInt64(pointer, offset));
        }

        private static byte ReadByte(IntPtr pointer, int offset)
        {
            return Marshal.ReadByte(pointer, offset);
        }

        private static string CombineErrors(params string[] values)
        {
            var errors = new List<string>();
            foreach (string value in values)
            {
                string normalized = NormalizeScalar(value);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    errors.Add(normalized);
                }
            }

            return errors.Count == 0 ? string.Empty : string.Join(" | ", errors);
        }

        private static string NormalizeScalar(string? value)
        {
            return value?.Trim() ?? string.Empty;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetIpForwardTable2(int family, out IntPtr table);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetIpInterfaceTable(int family, out IntPtr table);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint ConvertInterfaceLuidToIndex(ref NetLuid interfaceLuid, out uint interfaceIndex);

        [DllImport("iphlpapi.dll")]
        private static extern void FreeMibTable(IntPtr memory);

        internal readonly record struct RuntimeInspectionResult(
            bool IpHelperReadSucceeded,
            bool HasIpInterface,
            bool RouteAttached,
            string IpInterfaceCheckSource,
            string RouteTableReadSource,
            string InteropError,
            IReadOnlyList<IpInterfaceSnapshot> InterfaceSnapshots,
            IReadOnlyList<IpRouteSnapshot> RouteSnapshots,
            int InterfaceEntryCount,
            int RouteEntryCount,
            string ParserVersion);

        internal readonly record struct IpInterfaceSnapshot(ushort Family, ulong InterfaceLuid, uint InterfaceIndex);

        internal readonly record struct IpRouteSnapshot(
            ulong InterfaceLuid,
            uint InterfaceIndex,
            ushort AddressFamily,
            byte PrefixLength,
            uint Ipv4Address,
            In6Addr Ipv6Address)
        {
            public bool IsDefaultLike => AddressFamily switch
            {
                AddressFamilyInet => (PrefixLength == 0 && Ipv4Address == 0)
                    || (PrefixLength == 1 && (Ipv4Address == 0 || Ipv4Address == Ipv4HalfDefaultUpper)),
                AddressFamilyInet6 => (PrefixLength == 0 && Ipv6Address.IsZero)
                    || (PrefixLength == 1 && (Ipv6Address.IsZero || Ipv6Address.IsUpperHalfSplitDefault)),
                _ => false,
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NetLuid
        {
            public ulong Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpInterfaceTableLayout
        {
            public uint NumEntries;
            private uint _Padding;
            public MibIpInterfaceRowLayout Table;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpInterfaceRowLayout
        {
            public ushort Family;
            private ushort _Padding0;
            private uint _Padding1;
            public ulong InterfaceLuid;
            public uint InterfaceIndex;
            public uint MaxReassemblySize;
            public ulong InterfaceIdentifier;
            public uint MinRouterAdvertisementInterval;
            public uint MaxRouterAdvertisementInterval;
            public byte AdvertisingEnabled;
            public byte ForwardingEnabled;
            public byte WeakHostSend;
            public byte WeakHostReceive;
            public byte UseAutomaticMetric;
            public byte UseNeighborUnreachabilityDetection;
            public byte ManagedAddressConfigurationSupported;
            public byte OtherStatefulConfigurationSupported;
            public byte AdvertiseDefaultRoute;
            private byte _Padding2;
            private byte _Padding3;
            private byte _Padding4;
            public uint RouterDiscoveryBehavior;
            public uint DadTransmits;
            public uint BaseReachableTime;
            public uint RetransmitTime;
            public uint PathMtuDiscoveryTimeout;
            public uint LinkLocalAddressBehavior;
            public uint LinkLocalAddressTimeout;
            public ZoneIndices16Layout ZoneIndices;
            public uint SitePrefixLength;
            public uint Metric;
            public uint NlMtu;
            public byte Connected;
            public byte SupportsWakeUpPatterns;
            public byte SupportsNeighborDiscovery;
            public byte SupportsRouterDiscovery;
            public uint ReachableTime;
            public byte TransmitOffload;
            public byte ReceiveOffload;
            public byte DisableDefaultRoutes;
            private byte _Padding5;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ZoneIndices16Layout
        {
            public uint Value0;
            public uint Value1;
            public uint Value2;
            public uint Value3;
            public uint Value4;
            public uint Value5;
            public uint Value6;
            public uint Value7;
            public uint Value8;
            public uint Value9;
            public uint Value10;
            public uint Value11;
            public uint Value12;
            public uint Value13;
            public uint Value14;
            public uint Value15;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpForwardTable2Layout
        {
            public uint NumEntries;
            private uint _Padding;
            public MibIpForwardRow2Layout Table;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpForwardRow2Layout
        {
            public ulong InterfaceLuid;
            public uint InterfaceIndex;
            public IpAddressPrefixLayout DestinationPrefix;
            public SockAddrInetLayout NextHop;
            public byte SitePrefixLength;
            private byte _Padding0;
            private byte _Padding1;
            private byte _Padding2;
            public uint ValidLifetime;
            public uint PreferredLifetime;
            public uint Metric;
            public uint Protocol;
            public byte Loopback;
            public byte AutoconfigureAddress;
            public byte Publish;
            public byte Immortal;
            public uint Age;
            public uint Origin;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IpAddressPrefixLayout
        {
            public SockAddrInetLayout Prefix;
            public byte PrefixLength;
            private byte _Padding0;
            private ushort _Padding1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SockAddrInetLayout
        {
            public ushort Family;
            public ushort Port;
            public uint AddressOrFlowInfo;
            public uint Word1;
            public uint Word2;
            public uint Word3;
            public uint Word4;
            public uint ScopeIdOrZero1;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct In6Addr
        {
            public static In6Addr Zero => new(0, 0);

            public In6Addr(ulong low64, ulong high64)
            {
                Low64 = low64;
                High64 = high64;
            }

            public ulong Low64 { get; }

            public ulong High64 { get; }

            public bool IsZero => Low64 == 0 && High64 == 0;

            public bool IsUpperHalfSplitDefault => (Low64 & 0xFFUL) == 0x80UL && (Low64 >> 8) == 0 && High64 == 0;
        }
    }
}

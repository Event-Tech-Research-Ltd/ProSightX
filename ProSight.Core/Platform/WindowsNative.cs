using System.Net;
using System.Runtime.InteropServices;

namespace ProSight;

/// <summary>
/// Thin iphlpapi wrappers. Structs are read at fixed offsets from documented x64/x86 layouts
/// (MIB_IPNET_ROW2 = 88 bytes, MIB_IF_ROW2 = 1352 bytes) and every reader self-checks so a layout
/// mismatch degrades to "no data" instead of wrong numbers.
/// </summary>
internal static class WindowsNative
{
    public sealed record Neighbor(IPAddress Address, string Mac);

    public sealed record IfCounters(long InOctets, long OutOctets, long InFloodOctets, long OutFloodOctets);

    const ushort AF_INET = 2;
    const int NlnsReachable = 5; // NL_NEIGHBOR_STATE; Permanent (6) = static entries, not evidence of life
    const int RowSize = 88, TableHeader = 8;
    const int IfRow2Size = 1352;

    [DllImport("iphlpapi.dll")] static extern int GetIpNetTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")] static extern void FreeMibTable(IntPtr memory);
    [DllImport("iphlpapi.dll")] static extern int GetIfEntry2(IntPtr row);

    /// <summary>
    /// IPv4 neighbours the OS has confirmed reachable right now (state Reachable). Stale/incomplete entries are
    /// excluded on purpose: a stale entry can belong to a host that left minutes ago.
    /// </summary>
    public static List<Neighbor> ReachableIPv4Neighbors()
    {
        var list = new List<Neighbor>();
        IntPtr table = IntPtr.Zero;
        try
        {
            if (GetIpNetTable2(AF_INET, out table) != 0 || table == IntPtr.Zero) return list;
            int count = Marshal.ReadInt32(table);
            if (count is < 0 or > 100_000) return list; // implausible → layout/ABI mismatch, bail
            for (int i = 0; i < count; i++)
            {
                IntPtr row = table + TableHeader + i * RowSize;
                if ((ushort)Marshal.ReadInt16(row, 0) != AF_INET) continue; // SOCKADDR_INET family
                var ip = new IPAddress(new[] { Marshal.ReadByte(row, 4), Marshal.ReadByte(row, 5), Marshal.ReadByte(row, 6), Marshal.ReadByte(row, 7) });
                int macLen = Marshal.ReadInt32(row, 72);
                int state = Marshal.ReadInt32(row, 76);
                bool unreachable = (Marshal.ReadByte(row, 80) & 0x02) != 0;
                if (state != NlnsReachable || unreachable || macLen != 6) continue;
                var mac = new byte[6];
                for (int b = 0; b < 6; b++) mac[b] = Marshal.ReadByte(row, 40 + b);
                if (mac.All(x => x == 0) || mac.All(x => x == 0xFF) || (mac[0] & 0x01) != 0) continue; // empty/broadcast/multicast
                list.Add(new Neighbor(ip, string.Join(":", mac.Select(x => x.ToString("X2")))));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        finally { if (table != IntPtr.Zero) FreeMibTable(table); }
        return list;
    }

    /// <summary>
    /// Octet counters incl. multicast+broadcast ("flood") bytes, which .NET doesn't expose. Returns null if the call
    /// fails or the row fails its consistency check (unicast+multicast+broadcast must equal the total within 10 %).
    /// </summary>
    public static IfCounters? InterfaceCounters(int interfaceIndex)
    {
        IntPtr row = Marshal.AllocHGlobal(IfRow2Size);
        try
        {
            for (int i = 0; i < IfRow2Size; i += 8) Marshal.WriteInt64(row, i, 0);
            Marshal.WriteInt32(row, 8, interfaceIndex); // InterfaceLuid = 0 → lookup by InterfaceIndex
            if (GetIfEntry2(row) != 0) return null;
            if (Marshal.ReadInt32(row, 8) != interfaceIndex) return null;
            long inOct = Marshal.ReadInt64(row, 1208), inUc = Marshal.ReadInt64(row, 1256), inMc = Marshal.ReadInt64(row, 1264), inBc = Marshal.ReadInt64(row, 1272);
            long outOct = Marshal.ReadInt64(row, 1280), outUc = Marshal.ReadInt64(row, 1320), outMc = Marshal.ReadInt64(row, 1328), outBc = Marshal.ReadInt64(row, 1336);
            if (!Consistent(inOct, inUc + inMc + inBc) || !Consistent(outOct, outUc + outMc + outBc)) return null;
            return new IfCounters(inOct, outOct, inMc + inBc, outMc + outBc);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally { Marshal.FreeHGlobal(row); }
    }

    static bool Consistent(long total, long parts) =>
        total >= 0 && parts >= 0 && Math.Abs(total - parts) <= Math.Max(64 * 1024, total / 10);

    /// <summary>Interface index used by iphlpapi, or null if the NIC has no IP stack bound.</summary>
    public static int? IndexOf(System.Net.NetworkInformation.NetworkInterface nic)
    {
        try { if (nic.GetIPProperties().GetIPv4Properties() is { } v4) return v4.Index; } // null (no IPv4) → try IPv6
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        try { return nic.GetIPProperties().GetIPv6Properties()?.Index; }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return null; }
    }
}

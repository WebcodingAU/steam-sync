using System.Net;
using System.Runtime.InteropServices;

namespace SteamSync;

/// <summary>One IPv4 TCP connection from the system table, with the process that owns it.</summary>
internal sealed record TcpRow(int Pid, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, bool Listening)
{
    internal uint RawState, RawLocalAddr, RawLocalPort, RawRemoteAddr, RawRemotePort;
}

/// <summary>Windows' own round-trip time for one TCP connection, in milliseconds.</summary>
internal sealed record TcpRtt(double SmoothedMs, double MinMs, double MaxMs, double VarianceMs);

/// <summary>
/// The IPv4 TCP table (GetExtendedTcpTable) and per-connection RTT (GetPerTcpConnectionEStats, FineRtt). Neither
/// needs admin rights: Windows already collects FineRtt for every connection, so reading it works for any user; only
/// switching collection on or off (SetPerTcpConnectionEStats) would need admin, and it isn't needed. The RTT is the
/// game's own connection, updated whenever it carries data.
/// </summary>
internal static class TcpStats
{
    private const int AfInet = 2, TcpTableOwnerPidAll = 5, StateListen = 2, EstatsFineRtt = 8;

    public static List<TcpRow> Rows()
    {
        var rows = new List<TcpRow>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0) continue; // grew: retry with the new size
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (var i = 0; i < count; i++)
                {
                    var r = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buffer + 4 + i * rowSize);
                    rows.Add(new TcpRow((int)r.Pid, new IPAddress(r.LocalAddr), Port(r.LocalPort), new IPAddress(r.RemoteAddr), Port(r.RemotePort),
                        r.State == StateListen)
                    {
                        RawState = r.State, RawLocalAddr = r.LocalAddr, RawLocalPort = r.LocalPort, RawRemoteAddr = r.RemoteAddr, RawRemotePort = r.RemotePort,
                    });
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return rows;
    }

    /// <summary>The connection's RTT, or null when Windows has no sample for it (yet).</summary>
    public static unsafe TcpRtt? Rtt(TcpRow row)
    {
        var r = new MibTcpRow { State = row.RawState, LocalAddr = row.RawLocalAddr, LocalPort = row.RawLocalPort, RemoteAddr = row.RawRemoteAddr, RemotePort = row.RawRemotePort };
        byte enabled = 0;
        FineRttRod rod = default;
        var error = GetPerTcpConnectionEStats(ref r, EstatsFineRtt, (IntPtr)(&enabled), 0, 1, IntPtr.Zero, 0, 0, (IntPtr)(&rod), 0, (uint)sizeof(FineRttRod));
        if (error != 0 || enabled == 0 || rod.MaxRtt == 0) return null;
        // SumRtt reads as the current smoothed RTT on Windows 11 (it moves with the connection, it doesn't grow), all in µs.
        return new TcpRtt(rod.SumRtt / 1000.0, rod.MinRtt / 1000.0, rod.MaxRtt / 1000.0, rod.RttVar / 1000.0);
    }

    private static int Port(uint p) => (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, Pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FineRttRod { public uint RttVar, MaxRtt, MinRtt, SumRtt; }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcpConnectionEStats(ref MibTcpRow row, int type, IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);
}

using System.Runtime.InteropServices;
using System.Text.Json;

namespace PingHelper;

/// <summary>
/// Connections that something on this PC redirected: a program asked for a server on the internet, but its traffic
/// goes to 127.0.0.1. ExitLag does this: its driver turns the game's connection to the game server into one to its
/// own local proxy, so the system tables only ever show 127.0.0.1 and the real server is out of sight.
/// <para>
/// Winsock (Microsoft-Windows-Winsock-AFD) still sees what the program asked for. Its bind event gives the socket's
/// local port and its connect event gives the address passed to connect(), linked by the socket's endpoint handle.
/// The first packet on that local port (Kernel-Network, FlowTable) then shows where the connection really went: an
/// internet address asked for and loopback reached means a redirect, kept here for as long as the connection carries
/// traffic. Seen with WoW through ExitLag (2026-10-24): connect to 66.40.178.45:3724, traffic to 127.0.0.1:51075.
/// </para>
/// Not thread-safe: FlowTable calls it under its own lock.
/// </summary>
internal sealed class RedirectTable
{
    private readonly record struct Address(ulong Hi, ulong Lo, ushort Port);
    private readonly record struct Socket(uint Pid, ushort LocalPort);

    private static readonly long WaitForConnect = 60_000;   // ms from bind to connect, and from connect to first packet
    private static readonly long KeepIdle = 10 * 60_000;    // a redirect with no traffic for this long is forgotten
    private const int MaxEntries = 10_000;

    private readonly Dictionary<ulong, (Socket Socket, long At)> _bound = [];           // endpoint → its local port
    private readonly Dictionary<Socket, (Address Asked, long At)> _connecting = [];      // waiting for the first packet
    private readonly Dictionary<Socket, (Address Asked, long LastSeen)> _redirected = [];

    public void Bind(ulong endpoint, uint pid, ushort localPort)
    {
        if (localPort == 0 || _bound.Count >= MaxEntries) return;
        _bound[endpoint] = (new Socket(pid, localPort), Environment.TickCount64);
    }

    public void Connect(ulong endpoint, ulong hi, ulong lo, ushort port)
    {
        if (!_bound.Remove(endpoint, out var bound) || IsLoopback(hi, lo) || _connecting.Count >= MaxEntries) return;
        _connecting[bound.Socket] = (new Address(hi, lo, port), Environment.TickCount64);
    }

    /// <summary>A packet on a local port (once per packet, so cheap): settles a pending connect, keeps a redirect alive.</summary>
    public void Traffic(uint pid, ushort localPort, ulong remoteHi, ulong remoteLo)
    {
        var socket = new Socket(pid, localPort);
        if (_connecting.Count > 0 && _connecting.Remove(socket, out var pending))
        {
            if (IsLoopback(remoteHi, remoteLo) && _redirected.Count < MaxEntries)
                _redirected[socket] = (pending.Asked, Environment.TickCount64);
            return;
        }
        if (_redirected.Count == 0) return;
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_redirected, socket);
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref entry)) entry.LastSeen = Environment.TickCount64;
    }

    /// <summary>Drops what is too old, then writes "redirects":[{"pid","lp","ip","rp"}].</summary>
    public void Write(Utf8JsonWriter json)
    {
        var now = Environment.TickCount64;
        foreach (var (k, v) in _bound) if (now - v.At > WaitForConnect) _bound.Remove(k);
        foreach (var (k, v) in _connecting) if (now - v.At > WaitForConnect) _connecting.Remove(k);
        foreach (var (k, v) in _redirected) if (now - v.LastSeen > KeepIdle) _redirected.Remove(k);

        json.WriteStartArray("redirects");
        foreach (var (socket, entry) in _redirected)
        {
            json.WriteStartObject();
            json.WriteNumber("pid", socket.Pid);
            json.WriteNumber("lp", socket.LocalPort);
            json.WriteString("ip", FlowTable.FormatAddress(entry.Asked.Hi, entry.Asked.Lo));
            json.WriteNumber("rp", entry.Asked.Port);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    /// <summary>127.0.0.0/8 (as ::ffff:127.x.x.x) or ::1.</summary>
    public static bool IsLoopback(ulong hi, ulong lo) =>
        hi == 0 && (lo == 1 || (lo >> 32 == 0xFFFF && (lo >> 24 & 0xFF) == 127));
}

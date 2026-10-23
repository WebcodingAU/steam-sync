using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SteamSync;

/// <summary>The router LivePing times instead of a server that ignores ping: the last one on the route that answers.</summary>
internal sealed record Hop(IPAddress Ip, int Ttl, bool SameNetwork, bool Edge);

/// <summary>
/// Last-hop timing for servers that ignore ping, like tracert: echo requests to the server with a rising TTL, so each
/// router on the way answers "TTL expired", and the last one that answers before the route goes silent is timed
/// instead, by sending to the server with that TTL again (so the probe follows the game's own route).
/// <para>
/// Uses IcmpSendEcho2 directly: it reports a TTL-expired reply's round trip, which .NET's Ping always gives as 0.
/// No admin rights needed. IPv4 only.
/// </para>
/// <para>
/// A last hop only counts when it says something about the server: in the same AWS region as the server
/// (SameNetwork), AWS's edge in front of a GLOBAL (anycast) address (Edge: a lower bound, the rest of the way is
/// AWS's network), or clearly far from home (30 ms past the first hops). From Australia a route often goes silent
/// right after the ISP (Hunger Playtest's did, at ~20 ms), and timing that would say nothing about the server.
/// The route is found once per server for the session; a hop that misses 10 replies in a row is looked for again.
/// </para>
/// </summary>
internal sealed class HopProber : IDisposable
{
    private const int MaxTtl = 30;
    private const int SilentHopsToStop = 6;            // this many silent TTLs in a row after an answer: the route has gone quiet
    private const int MissesBeforeRediscovery = 10;    // routers rate-limit their replies; only a run of misses means it's gone
    private const double FarFromHome = 30;             // ms past the first hops before a hop counts as near the server
    private static readonly TimeSpan RetryUnusableAfter = TimeSpan.FromMinutes(10);

    private sealed class State
    {
        public Task? Discovery;
        public Hop? Hop;
        public int Misses;
        public DateTimeOffset RetryAt;
    }

    private readonly CloudRegions _cloud;
    private readonly IntPtr _icmp = IcmpCreateFile();
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, State> _servers = [];

    public HopProber(CloudRegions cloud) => _cloud = cloud;

    /// <summary>The hop to time for this server, or null: still looking (the first call starts looking, in the
    /// background), none worth timing, or IPv6.</summary>
    public Hop? Find(IPAddress server, CloudRegion? serverRegion)
    {
        if (server.AddressFamily != AddressFamily.InterNetwork || _icmp == IntPtr.Zero || _icmp == new IntPtr(-1)) return null;
        lock (_gate)
        {
            if (!_servers.TryGetValue(server, out var s)) _servers[server] = s = new State();
            if (s.Hop is not null) return s.Hop;
            if (s.Discovery is { IsCompleted: false } || DateTimeOffset.UtcNow < s.RetryAt) return null;
            s.Discovery = Task.Run(() => Discover(server, serverRegion, s));
            return null;
        }
    }

    /// <summary>The hop's round trip in ms, or null when it didn't answer this time (counted; enough misses in a row and
    /// the route is looked for again).</summary>
    public async Task<double?> TimeAsync(IPAddress server, Hop hop)
    {
        var (status, from, ms) = await Task.Run(() => Echo(server, hop.Ttl, 1000));
        lock (_gate)
        {
            if (!_servers.TryGetValue(server, out var s)) return null;
            if (status == IpTtlExpiredTransit && from.Equals(hop.Ip))
            {
                s.Misses = 0;
                return ms;
            }
            if (++s.Misses >= MissesBeforeRediscovery)
            {
                Log.Info($"Live ping: hop {hop.Ip} in front of {server} stopped answering; finding the route again");
                s.Hop = null;
                s.Misses = 0;
            }
            return null;
        }
    }

    private void Discover(IPAddress server, CloudRegion? serverRegion, State state)
    {
        var answered = new List<(int Ttl, IPAddress Ip, double Ms)>();
        var silent = 0;
        for (var ttl = 1; ttl <= MaxTtl; ttl++)
        {
            (uint Status, IPAddress From, double Ms) reply = (0, IPAddress.None, 0);
            var got = false;
            for (var attempt = 0; attempt < 2 && !got; attempt++) // a second try: routers drop some replies
            {
                reply = Echo(server, ttl, 800);
                got = reply.Status is IpTtlExpiredTransit or IpSuccess;
            }
            if (got && reply.Status == IpSuccess)
            {
                // The server itself answered: it doesn't ignore ping after all, so there's no hop to time.
                lock (_gate) state.RetryAt = DateTimeOffset.UtcNow + RetryUnusableAfter;
                return;
            }
            if (got)
            {
                answered.Add((ttl, reply.From, reply.Ms));
                silent = 0;
            }
            else if (answered.Count > 0 && ++silent >= SilentHopsToStop) break;
        }

        Hop? hop = null;
        if (answered.Count > 0)
        {
            var last = answered[^1];
            // "Home" is the first public hop (the ISP's edge), not the LAN router or carrier NAT, which answer far sooner
            // than anything out on the internet and would make the ISP's own routers look far away.
            var home = answered.FirstOrDefault(h => IsPublic(h.Ip)) is { Ip: not null } firstPublic ? firstPublic.Ms : answered.Max(h => h.Ms);
            var hopRegion = _cloud.Find(last.Ip);
            var sameNetwork = serverRegion is { IsGlobal: false } && hopRegion?.Code == serverRegion.Code;
            var edge = serverRegion is { IsGlobal: true } && hopRegion is not null;
            if (sameNetwork || edge || last.Ms - home >= FarFromHome) hop = new Hop(last.Ip, last.Ttl, sameNetwork, edge);
            Log.Info($"Live ping: route to {server}: last answer {last.Ip} at hop {last.Ttl}, {last.Ms:0} ms"
                + (hop is null ? " (too close to home to say anything about the server)" : sameNetwork ? " (same AWS region)" : edge ? " (AWS edge)" : " (far from home)"));
        }
        lock (_gate)
        {
            state.Hop = hop;
            state.Misses = 0;
            if (hop is null) state.RetryAt = DateTimeOffset.UtcNow + RetryUnusableAfter;
        }
    }

    private static bool IsPublic(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && (b[1] & 0xC0) == 64));
    }

    // ---- IcmpSendEcho2 ----

    private const uint IpSuccess = 0, IpTtlExpiredTransit = 11013;
    private static readonly byte[] Payload = new byte[32];

    /// <summary>One echo request with this TTL: the reply's status, who sent it, and its round trip in ms.</summary>
    private unsafe (uint Status, IPAddress From, double Ms) Echo(IPAddress destination, int ttl, uint timeoutMs)
    {
        var options = new IpOptionInformation { Ttl = (byte)ttl };
        const int replySize = 256; // ICMP_ECHO_REPLY (40 bytes on x64) + the echoed payload + room for an ICMP error
        var reply = stackalloc byte[replySize];
        var address = BitConverter.ToUInt32(destination.GetAddressBytes());
        var count = IcmpSendEcho2(_icmp, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, address, Payload, (ushort)Payload.Length,
            ref options, (IntPtr)reply, replySize, timeoutMs);
        if (count == 0) return (uint.MaxValue, IPAddress.None, 0);
        // ICMP_ECHO_REPLY: Address u32, Status u32, RoundTripTime u32 (ms), ...
        return (*(uint*)(reply + 4), new IPAddress(*(uint*)reply), *(uint*)(reply + 8));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IpOptionInformation
    {
        public byte Ttl, Tos, Flags, OptionsSize;
        public IntPtr OptionsData;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll")]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2(IntPtr handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext, uint destination,
        byte[] requestData, ushort requestSize, ref IpOptionInformation options, IntPtr replyBuffer, uint replySize, uint timeout);

    public void Dispose()
    {
        if (_icmp != IntPtr.Zero && _icmp != new IntPtr(-1)) IcmpCloseHandle(_icmp);
    }
}

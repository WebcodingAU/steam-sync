using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SteamSync;

/// <summary>
/// Real latency from UDP game servers that ignore ping and every query: one byte to a closed port next to the game's.
/// When the server's firewall lets that port through, the server's own network stack answers "port unreachable"
/// (ICMP), which Windows reports on a connected UDP socket as a connection reset, so the time to it is the server's
/// true round trip. Found on WARDOGS (2026-10-25): game port 4192 on an AWS GLOBAL address that ignores ping, the
/// Steam query and tracert, but ports 4193 and 4194 answer unreachable in 19-33 ms (the Sydney GameLift beacon: 23-26).
/// Through the relay the reply comes back too: the relay's NAT passes ICMP errors for a flow it tracks.
/// <para>
/// Ports tried: the game port +1, +2, +3 and -1. The first that answers is kept; servers rate-limit these replies, so
/// misses count as loss and only a long run of them starts the search again. A server where none answers for 3 rounds
/// is left alone for 10 minutes. Sent from the routed adapter's address (Probe.RouteSource), like Probe.IcmpRouted.
/// </para>
/// </summary>
internal sealed class UnreachableProbe
{
    private static readonly int[] Offsets = [1, 2, 3, -1];
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan GiveUpFor = TimeSpan.FromMinutes(10);
    private const int DiscoveryRounds = 3;
    private const int MissesBeforeRediscovery = 10;

    private sealed class State
    {
        public int? Port;
        public int Rounds, Misses;
        public DateTime GaveUpUntil;
    }

    private readonly Dictionary<(IPAddress, int), State> _servers = [];

    public bool CanTry(IPAddress ip, int gamePort) =>
        !_servers.TryGetValue((ip, gamePort), out var s) || s.Port is not null || DateTime.UtcNow >= s.GaveUpUntil;

    /// <summary>The port that answers for this server, once one has.</summary>
    public int? KnownPort(IPAddress ip, int gamePort) => _servers.TryGetValue((ip, gamePort), out var s) ? s.Port : null;

    /// <summary>One round, waiting for it (for the relay's ping timer thread). Port is the answering port once one has
    /// answered (still set when this round missed, so a miss counts as loss rather than a switch to another method); null
    /// while none has.</summary>
    public (int? Port, double? Ms) Probe(IPAddress ip, int gamePort) => ProbeAsync(ip, gamePort).GetAwaiter().GetResult();

    private async Task<(int? Port, double? Ms)> ProbeAsync(IPAddress ip, int gamePort)
    {
        if (!_servers.TryGetValue((ip, gamePort), out var s)) _servers[(ip, gamePort)] = s = new State();
        var source = SteamSync.Probe.RouteSource(ip);

        if (s.Port is { } known)
        {
            var ms = await OnceAsync(ip, known, source);
            if (ms is not null) { s.Misses = 0; return (known, ms); }
            if (++s.Misses < MissesBeforeRediscovery) return (known, null);
            s.Port = null;
            s.Misses = 0;
            s.Rounds = 0;
        }

        if (DateTime.UtcNow < s.GaveUpUntil) return (null, null);
        // All at once (servers allow a short burst of these replies), so a server where none answer costs one round,
        // not four; the nearest port that answered wins.
        var ports = Offsets.Select(o => gamePort + o).Where(p => p is > 0 and <= 65535).ToArray();
        var results = await Task.WhenAll(ports.Select(async p => (Port: p, Ms: await OnceAsync(ip, p, source))));
        foreach (var (port, ms) in results)
        {
            if (ms is null) continue;
            s.Port = port;
            s.Rounds = 0;
            Log.Info($"Relay ping: {ip} answers 'port unreachable' on {port}");
            // Timed again on its own: the first round trip in a process carries socket setup (290 ms against 23 in testing).
            return (port, await OnceAsync(ip, port, source) ?? ms);
        }
        if (++s.Rounds >= DiscoveryRounds)
        {
            s.GaveUpUntil = DateTime.UtcNow + GiveUpFor;
            s.Rounds = 0;
        }
        return (null, null);
    }

    /// <summary>One byte to the port: the round trip to the server's "port unreachable" (or to any reply), else null.</summary>
    private static async Task<double?> OnceAsync(IPAddress ip, int port, IPAddress? source)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var watch = new Stopwatch();
        try
        {
            if (source is not null) socket.Bind(new IPEndPoint(source, 0));
            socket.Connect(ip, port);
            using var cancel = new CancellationTokenSource(Timeout);
            var buffer = new byte[64];
            watch.Start();
            await socket.SendAsync(new byte[] { 0 }, SocketFlags.None, cancel.Token);
            await socket.ReceiveAsync(buffer, SocketFlags.None, cancel.Token);
            return watch.Elapsed.TotalMilliseconds; // something listens there and answered: just as good
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return watch.Elapsed.TotalMilliseconds; // the ICMP "port unreachable"
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return null; }
    }
}

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
/// <para>
/// Ports tried: the game port +1, +2, +3 and -1. The first that answers is kept; servers rate-limit these replies, so
/// misses count as loss and only a long run of them starts the search again. A server where none answers for 3 rounds
/// is left alone for 10 minutes.
/// </para>
/// </summary>
internal sealed class UnreachableProbe
{
    private static readonly int[] Offsets = [1, 2, 3, -1];
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan GiveUpFor = TimeSpan.FromMinutes(10);
    private const int DiscoveryRounds = 3;
    private const int MissesBeforeRediscovery = 10;

    private sealed class State
    {
        public int? Port;
        public int Rounds, Misses;
        public DateTimeOffset GaveUpUntil;
    }

    private readonly Dictionary<(IPAddress, int), State> _servers = [];

    public bool CanTry(IPAddress ip, int gamePort) =>
        !_servers.TryGetValue((ip, gamePort), out var s) || s.Port is not null || DateTimeOffset.UtcNow >= s.GaveUpUntil;

    /// <summary>One round. Port is the answering port once one has answered (still set when this round missed, so a miss
    /// counts as loss rather than a switch to another method); null while none has.</summary>
    public async Task<(int? Port, double? Ms)> ProbeAsync(IPAddress ip, int gamePort)
    {
        if (!_servers.TryGetValue((ip, gamePort), out var s)) _servers[(ip, gamePort)] = s = new State();

        if (s.Port is { } known)
        {
            var ms = await OnceAsync(ip, known);
            if (ms is not null) { s.Misses = 0; return (known, ms); }
            if (++s.Misses < MissesBeforeRediscovery) return (known, null);
            s.Port = null;
            s.Misses = 0;
            s.Rounds = 0;
        }

        if (DateTimeOffset.UtcNow < s.GaveUpUntil) return (null, null);
        // All at once (servers allow a short burst of these replies), so a server where none answer costs one second,
        // not four; the nearest port that answered wins.
        var ports = Offsets.Select(o => gamePort + o).Where(p => p is > 0 and <= 65535).ToArray();
        var results = await Task.WhenAll(ports.Select(async p => (Port: p, Ms: await OnceAsync(ip, p))));
        foreach (var (port, ms) in results)
        {
            if (ms is null) continue;
            s.Port = port;
            s.Rounds = 0;
            Log.Info($"Live ping: {ip} answers 'port unreachable' on {port}");
            // Timed again on its own: the first round trip in a process carries socket setup (290 ms against 23 in testing).
            return (port, await OnceAsync(ip, port) ?? ms);
        }
        if (++s.Rounds >= DiscoveryRounds)
        {
            s.GaveUpUntil = DateTimeOffset.UtcNow + GiveUpFor;
            s.Rounds = 0;
        }
        return (null, null);
    }

    /// <summary>One byte to the port: the round trip to the server's "port unreachable" (or to any reply), else null.</summary>
    private static async Task<double?> OnceAsync(IPAddress ip, int port)
    {
        using var udp = new UdpClient(ip.AddressFamily);
        var watch = new Stopwatch();
        try
        {
            udp.Connect(ip, port);
            using var cancel = new CancellationTokenSource(Timeout);
            watch.Start();
            await udp.SendAsync(new byte[] { 0 }.AsMemory(), cancel.Token);
            await udp.ReceiveAsync(cancel.Token);
            return watch.Elapsed.TotalMilliseconds; // something listens there and answered: just as good
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return watch.Elapsed.TotalMilliseconds; // the ICMP "port unreachable"
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return null; }
    }
}

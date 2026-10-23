using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SteamSync;

/// <summary>
/// The Steam server query (A2S_INFO) as a ping for servers that ignore ICMP: the game server's own process answers
/// it, so the time to the reply is real latency. Many Steam game servers answer on the game port, the port after it,
/// or 27015. Others (EOS-only or custom netcode, like Hunger Playtest's) don't answer at all, so a server that
/// stays silent for a few rounds is left alone for 10 minutes.
/// <para>
/// Protocol: FF FF FF FF 'T' "Source Engine Query\0". The server answers 'I' (the info) or, since 2020, 'A' with a
/// 4-byte challenge to append and resend; then the resend's round trip is the one timed.
/// </para>
/// </summary>
internal sealed class SteamQuery
{
    private static readonly byte[] Request = [0xFF, 0xFF, 0xFF, 0xFF, 0x54, .. "Source Engine Query"u8, 0x00];
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan GiveUpFor = TimeSpan.FromMinutes(10);
    private const int DiscoveryRounds = 3;   // rounds of trying every port before giving up on a server
    private const int MissesBeforeRediscovery = 5;

    private sealed class State
    {
        public int? Port;                 // the port that answered
        public int Rounds;                // discovery rounds without any answer
        public int Misses;                // answers missed in a row on the known port
        public DateTimeOffset GaveUpUntil;
    }

    private readonly Dictionary<(IPAddress, int), State> _servers = [];

    /// <summary>False while a server is being left alone after not answering.</summary>
    public bool CanTry(IPAddress ip, int gamePort) =>
        !_servers.TryGetValue((ip, gamePort), out var s) || s.Port is not null || DateTimeOffset.UtcNow >= s.GaveUpUntil;

    /// <summary>One query round. Port is the query port once one has answered (still set when this round missed, so a
    /// missed reply counts as loss rather than a switch to another method); null while none has.</summary>
    public async Task<(int? Port, double? Ms)> QueryAsync(IPAddress ip, int gamePort)
    {
        if (!_servers.TryGetValue((ip, gamePort), out var s)) _servers[(ip, gamePort)] = s = new State();

        if (s.Port is { } known)
        {
            var ms = await QueryOnceAsync(ip, known);
            if (ms is not null) { s.Misses = 0; return (known, ms); }
            if (++s.Misses < MissesBeforeRediscovery) return (known, null);
            s.Port = null;   // stopped answering: look for it again
            s.Misses = 0;
            s.Rounds = 0;
        }

        if (DateTimeOffset.UtcNow < s.GaveUpUntil) return (null, null);
        int[] ports = [.. new[] { gamePort, gamePort + 1, 27015 }.Where(p => p is > 0 and <= 65535).Distinct()];
        var tries = ports.Select(async p => (Port: p, Ms: await QueryOnceAsync(ip, p))).ToList();
        foreach (var (port, ms) in await Task.WhenAll(tries))
        {
            if (ms is null) continue;
            s.Port = port;
            s.Rounds = 0;
            Log.Info($"Live ping: {ip} answers the Steam query on port {port}");
            return (port, ms);
        }
        if (++s.Rounds >= DiscoveryRounds)
        {
            s.GaveUpUntil = DateTimeOffset.UtcNow + GiveUpFor;
            s.Rounds = 0;
        }
        return (null, null);
    }

    /// <summary>A2S_INFO to one port: the round trip in ms, or null without an answer within a second.</summary>
    private static async Task<double?> QueryOnceAsync(IPAddress ip, int port)
    {
        using var udp = new UdpClient(ip.AddressFamily);
        try
        {
            udp.Connect(ip, port);
            using var cancel = new CancellationTokenSource(Timeout);
            var watch = Stopwatch.StartNew();
            await udp.SendAsync(Request, cancel.Token);
            var reply = (await udp.ReceiveAsync(cancel.Token)).Buffer;
            if (reply.Length >= 9 && reply[4] == 0x41) // 'A': resend with the challenge, and time that round trip
            {
                watch.Restart();
                byte[] withChallenge = [.. Request, .. reply.AsSpan(5, 4)];
                await udp.SendAsync(withChallenge.AsMemory(), cancel.Token);
                reply = (await udp.ReceiveAsync(cancel.Token)).Buffer;
            }
            var ms = watch.Elapsed.TotalMilliseconds;
            // 'I' is the info ('m' from old GoldSrc servers); FE FF FF FF starts a split reply, which still proves the
            // server answered.
            var single = reply.Length >= 5 && reply[0] == 0xFF && reply[1] == 0xFF && reply[2] == 0xFF && reply[3] == 0xFF && reply[4] is 0x49 or 0x6D;
            var split = reply.Length >= 4 && reply[0] == 0xFE && reply[1] == 0xFF && reply[2] == 0xFF && reply[3] == 0xFF;
            return single || split ? ms : null;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return null; }
    }
}

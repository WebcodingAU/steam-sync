using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SteamSync;

/// <summary>
/// AWS GameLift's region ping beacons: UDP echo servers on port 7770, one or more per region, that GameLift games
/// ping to choose the region to play in. Hunger Playtest pings four of them before every match (Sydney ~20 ms,
/// N. California ~160, N. Virginia ~225, São Paulo ~320 from Australia) and then joins a server on a region-less
/// AWS GLOBAL address that answers nothing. The matchmaker places players in their lowest-latency region, so the
/// nearest beacon the game itself used is the best estimate of that server's ping.
/// <para>
/// LivePing collects the beacons it sees the game send to (UDP to port 7770 on an internet address). Every 30 s all
/// of them are surveyed to pick the nearest; in between only that one is echoed, once a call.
/// </para>
/// </summary>
internal sealed class GameLiftBeacons
{
    public const int Port = 7770;
    private static readonly TimeSpan SurveyEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1000);

    private IPAddress? _nearest;
    private DateTimeOffset _surveyedAt = DateTimeOffset.MinValue;

    /// <summary>The nearest beacon and this call's echo time (null when this echo was lost); null when none answers.</summary>
    public async Task<(IPAddress Ip, double? Ms)?> MeasureAsync(IReadOnlyCollection<IPAddress> beacons)
    {
        if (beacons.Count == 0) return null;
        var now = DateTimeOffset.UtcNow;
        if (_nearest is null || !beacons.Contains(_nearest) || now - _surveyedAt > SurveyEvery)
        {
            _surveyedAt = now;
            var results = await Task.WhenAll(beacons.Select(async b => (Ip: b, Ms: await EchoAsync(b))));
            var best = results.Where(r => r.Ms is not null).OrderBy(r => r.Ms).FirstOrDefault();
            _nearest = best.Ip;
            return _nearest is null ? null : (best.Ip, best.Ms);
        }
        return (_nearest, await EchoAsync(_nearest));
    }

    /// <summary>One UDP echo: the round trip in ms, or null without the same bytes back within a second.</summary>
    private static async Task<double?> EchoAsync(IPAddress beacon)
    {
        using var udp = new UdpClient(beacon.AddressFamily);
        try
        {
            udp.Connect(beacon, Port);
            var payload = BitConverter.GetBytes(Random.Shared.NextInt64());
            using var cancel = new CancellationTokenSource(Timeout);
            var watch = Stopwatch.StartNew();
            await udp.SendAsync(payload.AsMemory(), cancel.Token);
            var reply = (await udp.ReceiveAsync(cancel.Token)).Buffer;
            return reply.AsSpan().SequenceEqual(payload) ? watch.Elapsed.TotalMilliseconds : null;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return null; }
    }
}

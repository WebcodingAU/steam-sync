using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace SteamSync;

/// <summary>
/// While a game runs (PlayTracker), measures the ping to the server it is talking to once a second and reports it to
/// the site (report_live_ping), for the live ping page. Testing for now, so every second goes out.
/// <para>
/// Finding the server: the ping helper (PingHelperClient) says which servers each process sent packets to in the last
/// second, TCP and UDP; the busiest one of the game's that isn't web or LAN traffic is the game server. Without the
/// helper only TCP is visible (the system TCP table), so UDP games show no server.
/// </para>
/// <para>
/// Measuring it: a TCP server uses Windows' own RTT for that connection (TcpStats), which is the game's real traffic;
/// a UDP server is pinged (ICMP). A server that drops ICMP (AWS does by default) is estimated instead after 3 missed
/// pings: the time to reach the public endpoint of the cloud region it is in (CloudRegions; method cloud_estimate, and
/// labelled as an estimate). One outside a known cloud shows as lost.
/// </para>
/// <para>
/// ExitLag: when it routes the game, the game talks to ExitLag's proxy on this PC and ExitLag forwards the traffic to
/// its own relay, so the system tables never show the real game server. The helper (1.1.0+) still sees where the
/// game's connection was headed before ExitLag redirected it (PingHelperClient.LatestRedirects); then the game server
/// itself is pinged by the normal internet route (method exitlag_server), with ExitLag's relay measured alongside.
/// Without that (older helper, or a connection made before the helper started) the ping is to the relay
/// (exitlag_relay): the first leg only, and the sample says so.
/// </para>
/// </summary>
internal sealed class LivePing : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private const int WindowSize = 60;
    // Web, DNS and LAN discovery: a game's launcher and overlay traffic, never its game server.
    private static readonly HashSet<int> NotGamePorts = [53, 80, 443, 1900, 3702, 5353, 5355];

    private readonly SupabaseApi? _api;
    private readonly PlayTracker _play;
    private readonly PingHelperClient _helper;
    private readonly System.Threading.Timer _timer;
    private readonly Ping _pinger = new();
    private readonly Ping _relayPinger = new();     // a Ping sends one at a time; the relay is pinged alongside the server
    private HelperRedirect? _realServer;            // the game server last picked behind ExitLag, kept unless another is busier
    private readonly CloudRegions _cloud = new();
    private readonly Dictionary<IPAddress, int> _icmpFailures = [];              // pings in a row a server didn't answer
    private readonly Dictionary<IPAddress, DateTimeOffset> _icmpDeadUntil = [];  // servers estimated instead, until then
    private readonly Queue<double?> _window = new();
    private string? _windowFor;
    private int _ticking, _uploading;
    private bool _live;                     // the site holds a sample from this PC, so a stop must be sent
    private DateTimeOffset _skipUntil = DateTimeOffset.MinValue;
    private int[] _exitLag = [];
    private string[] _exitLagNames = [];
    private DateTimeOffset _exitLagCheckedAt = DateTimeOffset.MinValue;

    public LivePing(AppConfig config, PlayTracker play, PingHelperClient helper)
    {
        if (config.IsConfigured) _api = new SupabaseApi(config.SupabaseUrl, config.SupabaseAnonKey);
        _play = play;
        _helper = helper;
        _timer = new(_ => _ = TickAsync());
    }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(4), Tick);

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_api is null || now < _skipUntil || DeviceToken.Load() is not { } token) return;

            var games = _play.Running;
            if (games.Count == 0)
            {
                // The game stopped: clear the PC's live row once.
                if (_live && await SendAsync(token, null)) _live = false;
                _windowFor = null;
                return;
            }

            var sample = await MeasureAsync(games, now);
            if (Interlocked.Exchange(ref _uploading, 1) == 1) return; // the last second's upload is still going: skip this one
            _ = Task.Run(async () =>
            {
                try { if (await SendAsync(token, sample)) _live = true; }
                finally { Volatile.Write(ref _uploading, 0); }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Live ping check failed", ex);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private async Task<bool> SendAsync(string token, JsonObject? sample)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _api!.ReportLivePingAsync(token, sample, timeout.Token);
            return true;
        }
        catch (SupabaseException ex) when (ex.IsUnknownSignature)
        {
            Log.Info("The server can't take live ping yet (migration not applied); trying again in an hour");
            _skipUntil = DateTimeOffset.UtcNow.AddHours(1);
        }
        catch (OperationCanceledException) { } // slow second: the next one replaces it
        catch (Exception ex)
        {
            Log.Error("Sending live ping failed", ex);
        }
        return false;
    }

    /// <summary>One sample, as the next tick would send it (SteamSync.exe --live-ping).</summary>
    public Task<JsonObject> MeasureNowAsync(IReadOnlyList<RunningGame> games) => MeasureAsync(games, DateTimeOffset.UtcNow);

    private async Task<JsonObject> MeasureAsync(IReadOnlyList<RunningGame> games, DateTimeOffset now)
    {
        var flows = _helper.Latest;
        var helper = flows is not null ? "running" : PingHelperClient.State switch
        {
            HelperState.NotBundled => "not_bundled",
            HelperState.NotInstalled => "not_installed",
            HelperState.Outdated => "outdated",
            _ => "not_running",
        };

        // With several games running, the one with the most traffic.
        var game = flows is null || games.Count == 1
            ? games[0]
            : games.MaxBy(g => flows.Where(f => g.Pids.Contains(f.Pid)).Sum(f => f.Packets))!;
        var pids = game.Pids.ToHashSet();

        var tcp = TcpStats.Rows();
        var exitLag = ExitLagPids(now);
        // ExitLag's own traffic to its relay: UDP for games it tunnels, TCP for games that reach it through its local
        // proxy (AION 2). Not its web traffic (443 and the like).
        var exitLagFlows = flows?.Where(f => exitLag.Contains(f.Pid) && IsInternet(f.Ip) && !NotGamePorts.Contains(f.RemotePort))
            .OrderByDescending(f => f.Packets).ToList() ?? [];
        var gameFlows = flows?.Where(f => pids.Contains(f.Pid) && IsInternet(f.Ip) && !NotGamePorts.Contains(f.RemotePort))
            .OrderByDescending(f => f.Packets).ToList() ?? [];

        // ExitLag routes the game when the game's TCP goes to a port ExitLag listens on (its local proxy). Its UDP
        // routing can't be seen the same way, so a game with no traffic of its own while ExitLag's relay is busy counts too.
        var exitLagPorts = tcp.Where(r => r.Listening && exitLag.Contains(r.Pid)).Select(r => r.LocalPort).ToHashSet();
        var viaExitLag = exitLag.Length > 0
            && (tcp.Any(r => pids.Contains(r.Pid) && !r.Listening && IPAddress.IsLoopback(r.Remote) && exitLagPorts.Contains(r.RemotePort))
                || (flows is not null && gameFlows.Count == 0 && exitLagFlows.Sum(f => f.Packets) >= 20));

        string method;
        IPAddress? ip = null;
        int port = 0, serverPid = 0;
        string? proto = null, note = null;
        double? ping = null;
        TcpRtt? rtt = null;
        HelperFlow? flow = null;
        Relay? relay = null;
        CloudRegion? estimate = null;

        if (viaExitLag)
        {
            // The relay (first leg) and, when the helper saw where the game's connection was really headed, the game
            // server itself, measured at the same time.
            var real = RealServer(pids, flows);
            var relayTask = MeasureRelayAsync(exitLagFlows, exitLag, tcp);
            var serverTask = real is null ? Task.FromResult<(double?, CloudRegion?)>((null, null)) : PingOrEstimateAsync(real.Ip);
            await Task.WhenAll(relayTask, serverTask);
            relay = relayTask.Result;

            if (real is not null)
            {
                flow = flows?.FirstOrDefault(f => f.Pid == real.Pid && f.LocalPort == real.LocalPort);
                (ip, port, proto, serverPid, method) = (real.Ip, real.Port, flow is { Udp: true } ? "udp" : "tcp", real.Pid, "exitlag_server");
                (ping, estimate) = serverTask.Result;
                note = "ExitLag is routing this game. This is the ping to the game server by your normal internet route; "
                    + "ExitLag's own route to it may be a little faster."
                    + (relay?.Ping is { } relayPing ? $" ExitLag's relay is {Math.Round(relayPing)} ms away." : "");
            }
            else if (relay is not null)
            {
                (ip, port, proto, serverPid, method, flow, rtt, ping) =
                    (relay.Ip, relay.Port, relay.Proto, relay.Pid, "exitlag_relay", relay.Flow, relay.Rtt, relay.Ping);
                note = "ExitLag is routing this game. This is the ping to ExitLag's relay (the first leg), not to the game server."
                    + (PingHelperClient.State == HelperState.Outdated ? " Update the ping helper (tray menu) to see the game server itself."
                        : flows is not null ? " Reconnect in the game (relog) so the ping helper can see which server it uses." : "");
            }
            else
            {
                method = "none";
                note = flows is null
                    ? "ExitLag is routing this game. Install the ping helper to see the relay it uses."
                    : "ExitLag is routing this game, but no relay traffic was seen this second.";
            }
        }
        else if (gameFlows.FirstOrDefault() is { } top)
        {
            flow = top;
            (ip, port, proto, serverPid) = (top.Ip, top.RemotePort, top.Udp ? "udp" : "tcp", top.Pid);
            if (!top.Udp && top.Ip.AddressFamily == AddressFamily.InterNetwork
                && tcp.FirstOrDefault(r => r.Pid == top.Pid && r.Remote.Equals(top.Ip) && r.RemotePort == top.RemotePort && r.LocalPort == top.LocalPort) is { } row)
                rtt = TcpStats.Rtt(row);
            if (rtt is not null) { method = "tcp_rtt"; ping = rtt.SmoothedMs; }
            else
            {
                (ping, estimate) = await PingOrEstimateAsync(top.Ip);
                method = estimate is null || estimate.IsGlobal ? "icmp" : "cloud_estimate";
            }
        }
        else if (tcp.Where(r => pids.Contains(r.Pid) && !r.Listening && IsInternet(r.Remote) && !NotGamePorts.Contains(r.RemotePort))
                     .FirstOrDefault() is { } row)
        {
            // No helper: the game's own TCP connections are all there is to go on.
            (ip, port, proto, serverPid) = (row.Remote, row.RemotePort, "tcp", row.Pid);
            rtt = TcpStats.Rtt(row);
            if (rtt is not null) { method = "tcp_rtt"; ping = rtt.SmoothedMs; }
            else
            {
                (ping, estimate) = await PingOrEstimateAsync(row.Remote);
                method = estimate is null || estimate.IsGlobal ? "icmp" : "cloud_estimate";
            }
        }
        else
        {
            method = "none";
            note = flows is null
                ? "No TCP game traffic seen. UDP games need the ping helper (Gaming Enjoyer tray menu)."
                : "No game traffic seen this second.";
        }

        if (estimate is { IsGlobal: true })
        {
            const string unknown = "This server doesn't answer ping, and its address is one AWS doesn't tie to a region (anycast or "
                + "accelerator style), so its ping can't be measured or estimated from here.";
            note = method == "exitlag_server" ? $"ExitLag is routing this game. {unknown}" : unknown;
        }
        else if (estimate is not null)
        {
            var where = $"{estimate.Provider} {estimate.Name ?? estimate.Code}";
            var estimated = $"This server doesn't answer ping, so this is an estimate: the time to reach {where} ({estimate.Code}), the cloud region the server is in.";
            note = method == "exitlag_server" ? $"ExitLag is routing this game. {estimated} By your normal route; ExitLag's own may be a little faster." : estimated;
        }

        // Stats over the last minute of the same target; a new server, or switching to an estimate, starts a new window.
        var windowFor = $"{game.Key}|{method}|{ip}|{port}|{estimate?.Code}";
        if (windowFor != _windowFor)
        {
            _window.Clear();
            _windowFor = windowFor;
        }
        if (method != "none")
        {
            _window.Enqueue(ping);
            while (_window.Count > WindowSize) _window.Dequeue();
        }
        var values = _window.Where(v => v is not null).Select(v => v!.Value).ToList();
        double? jitter = null;
        if (values.Count >= 2)
            jitter = values.Zip(values.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        var pinged = method is "icmp" or "exitlag_relay" or "exitlag_server" or "cloud_estimate";
        if (pinged && _window.Count >= 5 && _window.TakeLast(5).All(v => v is null))
            note ??= "This server doesn't answer ping (ICMP), so its ping can't be measured this way.";

        var gameTraffic = flows?.Where(f => pids.Contains(f.Pid) && IsInternet(f.Ip)).ToList();

        // When nothing could be measured, what was on the wire this second, so the next fix can be made from data:
        // the busiest flows by process, protocol and remote port (no addresses), and ExitLag's process names.
        JsonArray? topFlows = null;
        if (method == "none" && flows is not null)
        {
            topFlows = [];
            foreach (var f in flows.OrderByDescending(f => f.Packets).Take(8))
                topFlows.Add(new JsonObject
                {
                    ["process"] = ProcessName(f.Pid),
                    ["proto"] = f.Udp ? "udp" : "tcp",
                    ["port"] = f.RemotePort,
                    ["pps"] = f.Packets,
                    ["internet"] = IsInternet(f.Ip),
                });
        }

        return new JsonObject
        {
            ["top_flows"] = topFlows,
            ["exitlag_procs"] = method == "none" && _exitLagNames.Length > 0 ? new JsonArray(_exitLagNames.Select(n => (JsonNode?)n).ToArray()) : null,
            ["v"] = 1,
            ["at"] = now.ToString("O"),
            ["game_key"] = game.Key,
            ["game_name"] = game.Name,
            ["method"] = method,
            ["ping_ms"] = Round(ping),
            ["jitter_ms"] = Round(jitter),
            ["loss_pct"] = pinged && _window.Count > 0 ? Round(_window.Count(v => v is null) * 100.0 / _window.Count) : null,
            ["min_ms"] = values.Count > 0 ? Round(values.Min()) : null,
            ["avg_ms"] = values.Count > 0 ? Round(values.Average()) : null,
            ["max_ms"] = values.Count > 0 ? Round(values.Max()) : null,
            ["window"] = _window.Count,
            ["server"] = ip is null ? null : new JsonObject
            {
                ["ip"] = ip.ToString(),
                ["port"] = port,
                ["proto"] = proto,
                ["process"] = ProcessName(serverPid),
            },
            // The server doesn't answer ping: ping_ms is the time to its cloud region's public endpoint (method
            // cloud_estimate, or exitlag_server with this set).
            ["estimate"] = estimate is null ? null : new JsonObject
            {
                ["provider"] = estimate.Provider,
                ["region"] = estimate.Code,
                ["region_name"] = estimate.Name,
                ["endpoint"] = estimate.Endpoint,
            },
            // Behind ExitLag with the game server known: its relay too (the first leg of ExitLag's route).
            ["relay"] = method != "exitlag_server" || relay is null ? null : new JsonObject
            {
                ["ip"] = relay.Ip.ToString(),
                ["port"] = relay.Port,
                ["proto"] = relay.Proto,
                ["ping_ms"] = Round(relay.Ping),
                ["process"] = ProcessName(relay.Pid),
            },
            ["flow"] = flow is null ? null : Traffic([flow]),
            ["game_traffic"] = gameTraffic is null ? null : Traffic(gameTraffic),
            ["tcp_rtt"] = rtt is null ? null : new JsonObject
            {
                ["smoothed_ms"] = Round(rtt.SmoothedMs),
                ["min_ms"] = Round(rtt.MinMs),
                ["max_ms"] = Round(rtt.MaxMs),
                ["var_ms"] = Round(rtt.VarianceMs),
            },
            ["via_exitlag"] = viaExitLag,
            ["exitlag_running"] = exitLag.Length > 0,
            ["helper"] = helper,
            ["note"] = note,
            ["app_version"] = AppInfo.Version,
        };
    }

    private static JsonObject Traffic(IReadOnlyCollection<HelperFlow> flows) => new()
    {
        ["pps_out"] = flows.Sum(f => f.PacketsOut),
        ["pps_in"] = flows.Sum(f => f.PacketsIn),
        ["kbps_out"] = Round(flows.Sum(f => f.BytesOut) * 8 / 1000.0),
        ["kbps_in"] = Round(flows.Sum(f => f.BytesIn) * 8 / 1000.0),
    };

    /// <summary>ExitLag's relay: the first leg of its route, measured on its own.</summary>
    private sealed record Relay(HelperFlow? Flow, IPAddress Ip, int Port, string Proto, int Pid, TcpRtt? Rtt, double? Ping);

    /// <summary>ExitLag's busiest relay this second (helper), else one of its open TCP connections to a non-web port.</summary>
    private async Task<Relay?> MeasureRelayAsync(List<HelperFlow> exitLagFlows, int[] exitLag, List<TcpRow> tcp)
    {
        if (exitLagFlows.FirstOrDefault() is { } f)
        {
            // A TCP relay connection has Windows' own measured RTT, better than a ping (and relays may not answer one).
            TcpRtt? rtt = null;
            if (!f.Udp && f.Ip.AddressFamily == AddressFamily.InterNetwork
                && tcp.FirstOrDefault(r => r.Pid == f.Pid && r.Remote.Equals(f.Ip) && r.RemotePort == f.RemotePort && r.LocalPort == f.LocalPort) is { } row)
                rtt = TcpStats.Rtt(row);
            return new Relay(f, f.Ip, f.RemotePort, f.Udp ? "udp" : "tcp", f.Pid, rtt, rtt?.SmoothedMs ?? await IcmpAsync(f.Ip, _relayPinger));
        }
        if (tcp.FirstOrDefault(r => exitLag.Contains(r.Pid) && !r.Listening && IsInternet(r.Remote) && !NotGamePorts.Contains(r.RemotePort)) is { } open)
        {
            var rtt = TcpStats.Rtt(open);
            return new Relay(null, open.Remote, open.RemotePort, "tcp", open.Pid, rtt, rtt?.SmoothedMs ?? await IcmpAsync(open.Remote, _relayPinger));
        }
        return null;
    }

    /// <summary>
    /// The game server behind ExitLag: of the game's redirected connections (the helper saw it ask for an internet
    /// server and reach 127.0.0.1), the busiest this second, preferring game ports over web ones. The last pick is kept
    /// unless another is busier, so a quiet second doesn't flip between the login and the world server.
    /// </summary>
    private HelperRedirect? RealServer(HashSet<int> pids, IReadOnlyList<HelperFlow>? flows)
    {
        var redirects = _helper.LatestRedirects?.Where(r => pids.Contains(r.Pid) && IsInternet(r.Ip)).ToList();
        if (redirects is null || redirects.Count == 0) return _realServer = null;

        int Packets(HelperRedirect r) => flows?.Where(f => f.Pid == r.Pid && f.LocalPort == r.LocalPort).Sum(f => f.Packets) ?? 0;
        var best = redirects.OrderBy(r => NotGamePorts.Contains(r.Port) ? 1 : 0).ThenByDescending(Packets).First();
        if (_realServer is { } last && redirects.Contains(last) && NotGamePorts.Contains(last.Port) == NotGamePorts.Contains(best.Port)
            && Packets(best) <= Packets(last))
            best = last;
        return _realServer = best;
    }

    /// <summary>
    /// The server's ping, or, once it has missed 3 pings in a row, an estimate from its cloud region (CloudRegions),
    /// for a minute before ping is tried again. A server that misses pings but isn't in a known cloud stays unmeasured.
    /// </summary>
    private async Task<(double? Ping, CloudRegion? Estimate)> PingOrEstimateAsync(IPAddress ip)
    {
        var now = DateTimeOffset.UtcNow;
        if (!(_icmpDeadUntil.TryGetValue(ip, out var until) && until > now))
        {
            var icmp = await IcmpAsync(ip, _pinger);
            if (icmp is not null)
            {
                _icmpFailures.Remove(ip);
                _icmpDeadUntil.Remove(ip);
                return (icmp, null);
            }
            var misses = _icmpFailures.GetValueOrDefault(ip) + 1;
            if (misses < 3)
            {
                _icmpFailures[ip] = misses;
                return (null, null);
            }
            _icmpFailures.Remove(ip);
            _icmpDeadUntil[ip] = now.AddMinutes(1);
        }
        return _cloud.Find(ip) is { } region ? (await _cloud.ProbeAsync(region), region) : (null, null);
    }

    private async Task<double?> IcmpAsync(IPAddress ip, Ping? pinger = null)
    {
        try
        {
            var reply = await (pinger ?? _pinger).SendPingAsync(ip, 1000);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException) { return null; }
    }

    /// <summary>ExitLag's processes, looked up every few seconds rather than every tick.</summary>
    private int[] ExitLagPids(DateTimeOffset now)
    {
        if (now - _exitLagCheckedAt < TimeSpan.FromSeconds(5)) return _exitLag;
        _exitLagCheckedAt = now;
        // Every ExitLag process, not just ExitLag.exe: its tunnel can run in a helper of its own.
        var ids = new List<int>();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string name;
                try { name = p.ProcessName; } catch (InvalidOperationException) { continue; }
                if (!name.StartsWith("ExitLag", StringComparison.OrdinalIgnoreCase)) continue;
                ids.Add(p.Id);
                names.Add(name);
            }
        }
        _exitLag = ids.ToArray();
        _exitLagNames = names.ToArray();
        return _exitLag;
    }

    private static string? ProcessName(int pid)
    {
        if (pid <= 0) return null;
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>A public internet address: not loopback, LAN, link-local, carrier NAT, multicast or unspecified.</summary>
    private static bool IsInternet(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                     || (b[0] == 172 && (b[1] & 0xF0) == 16)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 100 && (b[1] & 0xC0) == 64));
        }
        return !(ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                 || (b[0] & 0xFE) == 0xFC);
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;

    public void Dispose()
    {
        _timer.Dispose();
        _pinger.Dispose();
        _relayPinger.Dispose();
        _cloud.Dispose();
        _api?.Dispose();
    }
}

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
/// Measuring it, real measurements only (the user's call, 2026-10-26: guesses read as "all over the place"):
/// a TCP server uses Windows' own RTT for that connection (TcpStats), the game's real traffic; a UDP server is pinged
/// (ICMP); a server that ignores ping gets the Steam server query or, for UDP, its "port unreachable" reply
/// (ProbeServerAsync). Where none of those reach the real server, a game profile (GameProfiles) that says where the
/// game's servers are gives profile_estimate; without one the sample says not_configured, with no figure, and names
/// the server's cloud region or CDN so a profile can be made. Estimates from routers, cloud regions or GameLift
/// beacons are gone.
/// </para>
/// <para>
/// ExitLag: when it routes the game, the game talks to ExitLag's proxy on this PC and ExitLag forwards the traffic to
/// its own relay, so the system tables never show the real game server. The helper (1.1.0+) still sees where the
/// game's connection was headed before ExitLag redirected it (PingHelperClient.LatestRedirects); then the game server
/// itself is measured by the normal internet route (method exitlag_server), with ExitLag's relay measured alongside.
/// Without that (older helper, or a connection made before the helper started), or when the game server it sees isn't
/// configured, ExitLag's relay is the figure (method exitlag_relay; probe not_configured when the server was seen):
/// the first leg of ExitLag's route, which the user preferred to a bare "Not configured".
/// </para>
/// <para>
/// Anycast/CDN servers (CdnNetworks: AION 2 is behind Cloudflare) answer from the nearest edge, so anything sent to them
/// measures the edge: only a profile (or the Steam query, which the CDN passes on) gives their ping. ExitLag's own
/// end-to-end figure isn't readable: ExitLag keeps it in its window only, which shows nothing to UI Automation even
/// when open, and it writes no log or live storage.
/// </para>
/// <para>
/// Each sample also carries the PC's load (SystemStats): CPU, GPU, memory, an approximate responsiveness and the
/// game's own CPU, so the page can show whether a lag spike was the network or the PC.
/// </para>
/// </summary>
internal sealed class LivePing : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private const int WindowSize = 60;
    // Web, DNS and LAN discovery: a game's launcher and overlay traffic, never its game server.
    // GameLift's region ping beacons (7770) too: a game surveys them before joining a server.
    private static readonly HashSet<int> NotGamePorts = [53, 80, 443, 1900, 3702, 5353, 5355, 7770];

    private readonly SupabaseApi? _api;
    private readonly PlayTracker _play;
    private readonly PingHelperClient _helper;
    private readonly System.Threading.Timer _timer;
    private readonly Ping _pinger = new();
    private readonly Ping _relayPinger = new();     // a Ping sends one at a time; the relay is pinged alongside the server
    private HelperRedirect? _realServer;            // the game server last picked behind ExitLag, kept unless another is busier
    private IPAddress? _carriedServer;              // a UDP server ExitLag was last seen carrying (below the socket), and until when
    private DateTimeOffset _carriedUntil;
    private readonly CloudRegions _cloud = new();
    private readonly SteamQuery _steamQuery = new();
    private readonly UnreachableProbe _unreachable = new();
    private readonly SystemStats _system = new();
    private readonly GameProfiles _profiles = new();
    private readonly CdnNetworks _cdn = new();
    private readonly Dictionary<IPAddress, int> _icmpFailures = [];              // pings in a row a server didn't answer
    private readonly Dictionary<IPAddress, DateTimeOffset> _icmpDeadUntil = [];  // servers measured another way, until then
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

    /// <summary>One measuring of a server, as text (SteamSync.exe --probe).</summary>
    public async Task<string> ProbeNowAsync(IPAddress ip, int port, bool steamGame, bool udp, bool skipPing = false)
    {
        if (skipPing) _icmpDeadUntil[ip] = DateTimeOffset.MaxValue;
        var p = await MeasureServerAsync("", ip, port, steamGame, udp); // no game: CDN servers show as their edge, not a profile
        return $"{DateTime.Now:HH:mm:ss} {p.Probe} {Round(p.Ms)?.ToString() ?? "-"} ms"
            + (p.Cdn is { } cdn ? $" cdn {cdn.Name} AS{cdn.Asn}" : "")
            + (p.Steam is { } q ? $" steam_query port {q.Port}" : "")
            + (p.Unreachable is { } u ? $" unreachable port {u.Port}" : "")
            + (p.Region is { } r ? $" region {r.Provider} {r.Code}" : "");
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
        // So does a UDP game whose packets ExitLag takes below the socket (its NDIS filter, ARC Raiders): the game still
        // seems to send straight to its server, but each packet is sent again through ExitLag's relay, so ExitLag's UDP
        // out keeps up with the game's in the same second (when it routes nothing, ExitLag sends a few packets a second).
        var exitLagPorts = tcp.Where(r => r.Listening && exitLag.Contains(r.Pid)).Select(r => r.LocalPort).ToHashSet();
        var busiest = gameFlows.FirstOrDefault();
        var udpCarried = exitLag.Length > 0 && busiest is { Udp: true, PacketsOut: >= 10 }
            && exitLagFlows.Where(f => f.Udp).Sum(f => f.PacketsOut) >= busiest.PacketsOut * 0.7;
        // Kept for a few seconds for the same server, so a second where ExitLag's count dips doesn't flip the method.
        if (udpCarried) (_carriedServer, _carriedUntil) = (busiest!.Ip, now.AddSeconds(10));
        else if (exitLag.Length > 0 && busiest is { Udp: true } && busiest.Ip.Equals(_carriedServer) && now < _carriedUntil) udpCarried = true;
        var viaExitLag = exitLag.Length > 0
            && (tcp.Any(r => pids.Contains(r.Pid) && !r.Listening && IPAddress.IsLoopback(r.Remote) && exitLagPorts.Contains(r.RemotePort))
                || (flows is not null && gameFlows.Count == 0 && exitLagFlows.Sum(f => f.Packets) >= 20)
                || udpCarried);

        string method;
        IPAddress? ip = null;
        int port = 0, serverPid = 0;
        string? proto = null, note = null;
        double? ping = null;
        TcpRtt? rtt = null;
        HelperFlow? flow = null;
        Relay? relay = null;
        ServerProbe? probe = null;
        var steamGame = game.Key.StartsWith("steam:", StringComparison.Ordinal);

        if (viaExitLag)
        {
            // The relay (first leg) and, when the helper saw where the game's connection was really headed (or, for UDP
            // ExitLag carries below the socket, where the game sends it), the game server itself, measured at the same time.
            var real = RealServer(pids, flows) ?? (udpCarried ? new HelperRedirect(busiest!.Pid, busiest.LocalPort, busiest.Ip, busiest.RemotePort) : null);
            var relayTask = MeasureRelayAsync(exitLagFlows, exitLag, tcp);
            var realUdp = real is not null && flows?.FirstOrDefault(f => f.Pid == real.Pid && f.LocalPort == real.LocalPort) is { Udp: true };
            var serverTask = real is null ? null : MeasureServerAsync(game.Key, real.Ip, real.Port, steamGame, realUdp);
            await Task.WhenAll(relayTask, serverTask ?? Task.CompletedTask);
            relay = relayTask.Result;

            if (real is not null)
            {
                flow = flows?.FirstOrDefault(f => f.Pid == real.Pid && f.LocalPort == real.LocalPort);
                probe = serverTask!.Result;
                (ip, port, proto, serverPid, ping) = (real.Ip, real.Port, flow is { Udp: true } ? "udp" : "tcp", real.Pid, probe.Ms);
                method = "exitlag_server";
                // The game server isn't configured: ExitLag's relay is the figure instead (the user's call), the first leg
                // of ExitLag's route. A relay that misses a ping counts as loss rather than dropping to not_configured.
                if (probe.Probe == "not_configured")
                    (method, ping) = relay is null ? ("not_configured", null) : ("exitlag_relay", relay.Ping);
                note = "ExitLag is routing this game. This is the ping to the game server by your normal internet route; "
                    + "ExitLag's own route to it may be a little faster."
                    + (relay?.Ping is { } relayPing ? $" ExitLag's relay is {Math.Round(relayPing)} ms away." : "");
            }
            else if (relay is not null)
            {
                // Only ExitLag's first leg is known: its relay is the figure (the user's call), like an unconfigured server.
                (method, flow, ping) = ("exitlag_relay", relay.Flow, relay.Ping);
                note = "ExitLag is routing this game and the game server behind it can't be seen, so this is the ping to ExitLag's "
                    + "relay (the first leg of its route)."
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
            // A CDN edge ends the game's TCP itself, so its RTT is the edge's too: a profile, or not configured.
            if (rtt is not null && !EdgeOrProfile(game.Key, top.Ip)) { method = "tcp_rtt"; ping = rtt.SmoothedMs; }
            else
            {
                probe = await MeasureServerAsync(game.Key, top.Ip, top.RemotePort, steamGame, top.Udp);
                (method, ping) = (probe.Probe, probe.Ms);
            }
        }
        else if (tcp.Where(r => pids.Contains(r.Pid) && !r.Listening && IsInternet(r.Remote) && !NotGamePorts.Contains(r.RemotePort))
                     .FirstOrDefault() is { } row)
        {
            // No helper: the game's own TCP connections are all there is to go on.
            (ip, port, proto, serverPid) = (row.Remote, row.RemotePort, "tcp", row.Pid);
            rtt = TcpStats.Rtt(row);
            if (rtt is not null && !EdgeOrProfile(game.Key, row.Remote)) { method = "tcp_rtt"; ping = rtt.SmoothedMs; }
            else
            {
                probe = await MeasureServerAsync(game.Key, row.Remote, row.RemotePort, steamGame, udp: false);
                (method, ping) = (probe.Probe, probe.Ms);
            }
        }
        else
        {
            method = "none";
            note = flows is null
                ? "No TCP game traffic seen. UDP games need the ping helper (Gaming Enjoyer tray menu)."
                : "No game traffic seen this second.";
        }

        // How a server that ignores ping was measured instead, or why it can't be, said in the note.
        var how = probe switch
        {
            { Probe: "profile_estimate", Profile: { } pr } =>
                (pr.Note ?? $"This game's servers can't be pinged from here, so this is the time to {pr.Target}, in {pr.Region}.")
                + (pr.Target == "region" && probe.Region is { } pr2 ? $" Server region: {pr2.Provider} {pr2.Name ?? pr2.Code} ({pr2.Code})." : "")
                + $" An estimate, from the {pr.Name} game profile.",
            { Probe: "udp_unreachable", Unreachable: { } u } =>
                $"This server ignores ping, so this is the time its system takes to answer a packet sent to a closed port next to "
                + $"the game's (port {u.Port}): the server's real round trip.",
            { Probe: "steam_query", Steam: { } q } =>
                $"This server doesn't answer ping, so this is the Steam server query (port {q.Port}), answered by the game server itself.",
            { Probe: "not_configured", Cdn: { } c } =>
                $"Not configured: this server's address belongs to {c.Name} (AS{c.Asn}), which answers from its nearest edge, so nothing "
                + "sent to it reaches the game server. A game profile saying where the servers are would give its ping.",
            { Probe: "not_configured", Region: { IsGlobal: false } r } =>
                $"Not configured: this server doesn't answer ping. It's in {r.Provider} {r.Name ?? r.Code} ({r.Code}); a game profile "
                + "for that region would give its ping.",
            { Probe: "not_configured" } =>
                "Not configured: this server doesn't answer ping, and there's no game profile for it.",
            _ => null,
        };
        if (how is not null)
            note = viaExitLag
                ? $"ExitLag is routing this game. {how}" + (probe!.Probe switch
                {
                    "not_configured" when method == "exitlag_relay" => " Until then, this is the ping to ExitLag's relay (the first leg of its route).",
                    "icmp" or "not_configured" => "",
                    "profile_estimate" => " ExitLag's own route there may be a little faster.",
                    _ => " By your normal route; ExitLag's own may be a little faster.",
                })
                : how;

        // Stats over the last minute of the same target; a new server, or a new way of measuring it, starts a new window.
        var windowFor = $"{game.Key}|{method}|{ip}|{port}|{probe?.Probe}";
        if (windowFor != _windowFor)
        {
            _window.Clear();
            _windowFor = windowFor;
        }
        if (method is not ("none" or "not_configured"))
        {
            _window.Enqueue(ping);
            while (_window.Count > WindowSize) _window.Dequeue();
        }
        var values = _window.Where(v => v is not null).Select(v => v!.Value).ToList();
        double? jitter = null;
        if (values.Count >= 2)
            jitter = values.Zip(values.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        var pinged = method is "icmp" or "exitlag_server" or "exitlag_relay" or "steam_query" or "udp_unreachable" or "profile_estimate";
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
            // How the server itself was measured (icmp, steam_query, udp_unreachable, profile_estimate, not_configured): the
            // same as method for a game played directly, and the way behind method exitlag_server. Null for tcp_rtt.
            ["probe"] = probe?.Probe,
            ["steam_query"] = probe?.Steam is { } sq ? new JsonObject { ["port"] = sq.Port, ["ms"] = Round(sq.Ms) } : null,
            ["unreachable"] = probe?.Unreachable is { } ur ? new JsonObject { ["port"] = ur.Port, ["ms"] = Round(ur.Ms) } : null,
            // With profile_estimate, ping_ms is the time to the profile's target in the servers' region.
            ["profile"] = probe?.Profile is { } profile ? new JsonObject
            {
                ["name"] = profile.Name,
                // A "region" profile names the server's own region.
                ["region"] = profile.Target == "region" ? probe.Region?.Name ?? probe.Region?.Code : profile.Region,
                ["target"] = profile.Target == "region" ? probe.Region?.Endpoint : profile.Target,
            } : null,
            ["cdn"] = (probe?.Cdn ?? (ip is null ? null : _cdn.Find(ip))) is { } cdn ? new JsonObject
            {
                ["asn"] = cdn.Asn,
                ["name"] = cdn.Name,
            } : null,
            // The server's cloud region, when known (looked up once it stops answering ping): what a profile for it needs.
            ["region"] = probe?.Region is { } region ? new JsonObject
            {
                ["provider"] = region.Provider,
                ["code"] = region.Code,
                ["name"] = region.Name,
            } : null,
            // Behind ExitLag: its relay (the first leg of ExitLag's route), whether or not the game server is known.
            ["relay"] = relay is null ? null : new JsonObject
            {
                ["ip"] = relay.Ip.ToString(),
                ["port"] = relay.Port,
                ["proto"] = relay.Proto,
                ["ping_ms"] = Round(relay.Ping),
                ["process"] = ProcessName(relay.Pid),
            },
            ["flow"] = flow is null ? null : Traffic([flow]),
            ["game_traffic"] = gameTraffic is null ? null : Traffic(gameTraffic),
            ["exitlag_traffic"] = exitLagFlows.Count == 0 ? null : Traffic(exitLagFlows),
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
            ["system"] = _system.Sample(game.Pids),
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

    /// <summary>How a server was measured: Probe is icmp, steam_query, udp_unreachable, profile_estimate or
    /// not_configured (no figure). Region is its cloud region when known (looked up once ping fails), Cdn its CDN.</summary>
    private sealed record ServerProbe(string Probe, double? Ms, CloudRegion? Region = null, (int Port, double? Ms)? Steam = null,
        (int Port, double? Ms)? Unreachable = null, GameProfile? Profile = null, CdnNetwork? Cdn = null);

    /// <summary>Whether the server's own numbers can't be trusted: a game profile covers it, or it's a CDN edge.</summary>
    private bool EdgeOrProfile(string gameKey, IPAddress ip) => _profiles.Find(gameKey, ip) is not null || _cdn.Find(ip) is not null;

    /// <summary>
    /// A game server's ping, the profile_estimate way when a profile covers it, else ProbeServerAsync's:
    /// <list type="bullet">
    /// <item>profile_estimate: a game profile (GameProfiles) says where the server really is; its target in that region
    /// is timed instead (a TCP handshake, or a gcping request for an https target).</item>
    /// <item>A CDN edge (CdnNetworks) without a profile: the Steam server query (the CDN passes it through to the game
    /// server, so it's real latency when the game answers one), else not_configured: the edge answers everything else.</item>
    /// </list>
    /// </summary>
    private async Task<ServerProbe> MeasureServerAsync(string gameKey, IPAddress ip, int port, bool steamGame, bool udp)
    {
        if (_profiles.Find(gameKey, ip) is { } profile)
        {
            if (profile.Target != "region")
            {
                var target = new CloudRegion("profile", profile.Region, profile.Region, profile.Target);
                return new ServerProbe("profile_estimate", await _cloud.ProbeAsync(target), Profile: profile, Cdn: _cdn.Find(ip));
            }
            // The server's own cloud region (unknown for a moment while the ranges load, or not in a known cloud).
            var region = _cloud.Find(ip);
            return region is { IsGlobal: false, Endpoint: not null }
                ? new ServerProbe("profile_estimate", await _cloud.ProbeAsync(region), region, Profile: profile, Cdn: _cdn.Find(ip))
                : new ServerProbe("not_configured", null, region, Cdn: _cdn.Find(ip));
        }
        if (_cdn.Find(ip) is { } cdn)
        {
            if (steamGame && port > 0 && _steamQuery.CanTry(ip, port))
            {
                var (queryPort, ms) = await _steamQuery.QueryAsync(ip, port);
                if (queryPort is { } p) return new ServerProbe("steam_query", ms, Steam: (p, ms), Cdn: cdn);
            }
            return new ServerProbe("not_configured", null, Cdn: cdn);
        }
        return await ProbeServerAsync(ip, port, steamGame, udp);
    }

    /// <summary>
    /// A server's ping. ICMP while it answers; once it misses 3 in a row, for a minute (then ICMP is tried again), the
    /// first of these that works, both real latency:
    /// <list type="number">
    /// <item>steam_query: the Steam server query (Steam games), answered by the game server itself.</item>
    /// <item>udp_unreachable: UDP servers: the server's "port unreachable" for a byte to a closed port next to the game's
    /// (UnreachableProbe).</item>
    /// </list>
    /// Else not_configured, with the server's cloud region when known, for a profile to be made from. The Steam query
    /// keeps its method while it has an answering port, so a missed reply counts as loss rather than flipping methods.
    /// </summary>
    private async Task<ServerProbe> ProbeServerAsync(IPAddress ip, int port, bool steamGame, bool udp)
    {
        var now = DateTimeOffset.UtcNow;
        if (!(_icmpDeadUntil.TryGetValue(ip, out var until) && until > now))
        {
            var icmp = await IcmpAsync(ip, _pinger);
            if (icmp is not null)
            {
                _icmpFailures.Remove(ip);
                _icmpDeadUntil.Remove(ip);
                return new ServerProbe("icmp", icmp);
            }
            var misses = _icmpFailures.GetValueOrDefault(ip) + 1;
            if (misses < 3)
            {
                _icmpFailures[ip] = misses;
                return new ServerProbe("icmp", null);
            }
            _icmpFailures.Remove(ip);
            _icmpDeadUntil[ip] = now.AddMinutes(1);
        }

        var region = _cloud.Find(ip);
        if (steamGame && port > 0 && _steamQuery.CanTry(ip, port))
        {
            var (queryPort, ms) = await _steamQuery.QueryAsync(ip, port);
            if (queryPort is { } p) return new ServerProbe("steam_query", ms, region, Steam: (p, ms));
        }
        if (udp && port > 0 && _unreachable.CanTry(ip, port))
        {
            var (closedPort, ms) = await _unreachable.ProbeAsync(ip, port);
            if (closedPort is { } c) return new ServerProbe("udp_unreachable", ms, region, Unreachable: (c, ms));
        }
        return new ServerProbe("not_configured", null, region);
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
        _system.Dispose();
        _api?.Dispose();
    }
}

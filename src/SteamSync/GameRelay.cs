using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SteamSync;

/// <summary>
/// Game-only routing through your own WireGuard relay (a VPS you run), like ExitLag but with one route you control:
/// only the game servers' addresses go through the tunnel, everything else stays on the normal connection.
/// <para>
/// The tunnel is the relay helper (src/RelayHelper, GamingEnjoyerRelay.exe): a small service that ships in this app's
/// package and runs WireGuard's own wireguard.dll, so no WireGuard app is needed. Setting the relay up installs it
/// into Program Files with the relay's client config (one UAC prompt); from then on this class only talks to it over
/// \\.\pipe\GamingEnjoyer.Relay (RelayClient): "up" with the ranges to route, "down", "status".
/// </para>
/// <para>
/// What goes through it: every game's ranges, except the games unticked in the tray's Game relay menu. Ranges are learned:
/// while a game runs, every public IPv4 server its own processes hold a TCP connection to for 20 seconds or more (a
/// minute when switching mid-game is off; game sessions, not short web requests) adds that address's /24. Web and push
/// ports (NotLearnedPorts) aren't learned: those are the game's web, login, store and notification connections, and mostly
/// go to shared networks (Cloudflare, Akamai, CloudFront, Google), whose /24s also carry unrelated sites, this app's own
/// sync to Supabase among them. Neither are launchers' connections (BsgLauncher's Cloudflare and Google push channels
/// were Tarkov's only learned "servers"). UDP game servers are learned too (UdpPeers: a steady flow both ways for as long),
/// such as Tarkov's raid servers; a range found that way joins the relay only once its flow has ended (the next raid),
/// since moving a live UDP flow would end it. Ranges can
/// also be typed into relay.json by hand (UDP-only games, whose peers can't be seen without admin rights).
/// </para>
/// <para>
/// When changes apply: moving a live connection between the direct path and the relay changes the address the server
/// sees, which drops it. With "Switch mid-game" on (the default, RelaySettings.SwitchMidGame) a newly learned server goes
/// through the relay straight away, like ExitLag: the game loses that connection once and, if it reconnects by itself,
/// carries on through the relay. Off, changes wait until no game is running, so a game's first session stays direct.
/// </para>
/// <para>
/// While the tunnel is up its WireGuard handshake is watched (the helper's status; WireGuard renews it every two
/// minutes while keepalives flow) and the relay is pinged for the menu. With no handshake for 3 minutes and no game
/// running the tunnel is taken down, so nothing is sent into a dead route, and tried again 5 minutes later.
/// </para>
/// </summary>
internal sealed class GameRelay : IDisposable
{
    private const string HelperService = "GamingEnjoyerRelay";
    private const string HelperExeName = "GamingEnjoyerRelay.exe";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LearnAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LearnAfterMidGame = TimeSpan.FromSeconds(20); // switching mid-game: switch early in the session
    private static readonly TimeSpan HealthEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleHandshake = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RetryDownAfter = TimeSpan.FromMinutes(5);
    private const int MaxRangesPerGame = 64;

    public static string SettingsFile { get; } = Path.Combine(AppInfo.DataDir, "relay.json");
    private static string PackagedHelper => Path.Combine(AppContext.BaseDirectory, HelperExeName);
    private static string InstalledHelper => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Gaming Enjoyer", HelperExeName);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly PlayTracker _play;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private readonly RelaySettings _settings;
    private readonly Dictionary<(int Pid, uint Remote, int Port), DateTimeOffset> _firstSeen = [];
    private readonly UdpPeers _udp = new();      // the games' UDP servers (raids), which the TCP table can't show
    private HashSet<string> _liveUdp = [];        // /24s the running games have a live UDP flow with, as of the last Learn
    private List<string>? _applied;          // the ranges last sent with "up"; null = down or not known yet
    private DateTimeOffset _upSince, _lastHealth = DateTimeOffset.MinValue, _downUntil = DateTimeOffset.MinValue;
    private int _ticking;

    /// <summary>One line for the tray: what the relay is doing.</summary>
    public string Status { get; private set; } = "Relay: starting";

    /// <summary>A change made while a game runs (switched on or off, ranges learned) that waits for it to close.</summary>
    public bool Pending { get; private set; }

    /// <summary>The installed helper's version, from its last status reply.</summary>
    public string? HelperVersion { get; private set; }

    /// <summary>The relay config has packet duplication set up ([GamingEnjoyer] section), from the helper's replies.</summary>
    public bool DuplicateAvailable { get; private set; }

    /// <summary>"Use phone as backup": every packet over every connection (the helper's Duplicator).</summary>
    public bool Duplicate => _settings.Duplicate;

    /// <summary>"Switch mid-game": a newly learned server goes through the relay at once (one brief disconnect), rather
    /// than when the game closes.</summary>
    public bool SwitchMidGame => _settings.SwitchMidGame;

    /// <summary>While duplication is on, one line per connection for the menu: name, and when it last brought a reply.</summary>
    public IReadOnlyList<string> PathLines { get; private set; } = [];

    private bool? _helperDuplicate;          // what the helper last said; null = not asked yet

    // Live ping of the relayed game (PingTick), sent with the PC load sample (PingSample).
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(1);
    private const int PingWindow = 30;
    private readonly System.Threading.Timer _pingTimer;
    private readonly Dictionary<string, PingState> _pingStates = []; // per running game (key)
    private static readonly TimeSpan IcmpQuietFor = TimeSpan.FromSeconds(30), IcmpHold = TimeSpan.FromSeconds(3);
    private readonly UnreachableProbe _unreachable = new(); // UDP servers that ignore ICMP (WARDOGS)
    private readonly CloudRegions _cloud = new();          // silent servers' cloud regions (cloud_estimate)
    private readonly object _homeGate = new();
    private HashSet<uint> _homeHops = [];                  // /24s of this PC's own ISP routers (hop_estimate skips them)
    private DateTime _homeTracedAt = DateTime.MinValue;
    private readonly GameProfiles _profiles = new();
    private (string Host, IPAddress? Address, DateTime ResolvedAt) _profileTarget = ("", null, DateTime.MinValue);
    private static readonly TimeSpan EstimateEvery = TimeSpan.FromSeconds(5), EstimateHold = TimeSpan.FromSeconds(30);
    private const int PathWindow = 60;                   // seconds of per-connection pings (the site's connection table)
    private readonly Dictionary<string, Queue<int?>> _pathPings = new(StringComparer.Ordinal); // per connection; null = lost
    private readonly Queue<int?> _bothPings = new();     // the faster answer of the connections each second
    private (string Name, bool Phone, long? Sent, long? Received, long? QuietS)[] _paths = []; // the helper's last status
    private volatile JsonObject? _ping;
    private volatile string? _pingGame;                  // the game key the sample's top-level ping is for
    private int _pingTicking;

    /// <summary>One game's ping windows, reset when its server changes.</summary>
    private sealed class PingState
    {
        public string? For;                              // the server (address:port) the windows belong to
        public readonly Queue<double> Pings = new();     // the ping figures of the last PingWindow seconds
        public readonly Queue<bool> Answers = new();     // whether the server answered each ICMP echo (or udp_unreachable), same window
        public (int Ms, DateTime At)? LastIcmp;          // the server's last ICMP answer: udp_unreachable only when it's old
        public (IPAddress Ip, byte Ttl)? Hop;            // hop_estimate's router (under lock: traced in the background)
        public DateTime? HopTracedAt;
        public Task? HopTracing;
        public (double Ms, DateTime At)? Estimate;       // the profile target's last answer (under lock: set in the background)
        public bool EstimateNew;                         // Estimate came in since the last tick read it
        public Task? Measuring;                          // the profile target's measurement in progress
        public DateTime EstimateNext = DateTime.MinValue;
    }
    private string? _relayTunnelAddress, _relayEndpoint; // from the helper's replies
    private long _pathsOk;

    public GameRelay(PlayTracker play)
    {
        _play = play;
        _settings = LoadSettings();
        _timer = new(_ => _ = TickAsync());
        _pingTimer = new(_ => PingTick());
    }

    /// <summary>The relay helper service is installed (the relay has been set up on this PC).</summary>
    public static bool IsSetUp
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + HelperService);
            return key is not null;
        }
    }

    /// <summary>The tray offers the setup in beta builds that carry the helper, until it has run.</summary>
    public static bool CanSetUp => UpdateChannel.IsBetaBuild && File.Exists(PackagedHelper) && !IsSetUp;

    /// <summary>This app carries a newer helper than the installed one: the tray offers to update it.</summary>
    public bool HelperOutdated =>
        HelperVersion is { } installed && File.Exists(PackagedHelper)
        && FileVersionInfo.GetVersionInfo(PackagedHelper).ProductVersion?.Split('+')[0] is { } packaged
        && Version.TryParse(packaged, out var p) && Version.TryParse(installed, out var i) && p > i;

    public void Start()
    {
        if (!IsSetUp) return;
        _timer.Change(TimeSpan.FromSeconds(3), Tick);
        _pingTimer.Change(PingEvery, PingEvery);
    }

    /// <summary>
    /// The running games' live ping, for the PC load sample (the site's ping pill): ping_ms, jitter_ms, loss_pct, window,
    /// ping_method, server {ip, port, proto}, route and relay {routed, endpoint, ms, backup, paths_ok, paths, both} for
    /// the main game (PingGame), and games: one entry per running game with a server seen (game_key, game_name, routed
    /// and the same per-game fields). relay.routed is false when the main game's server is timed on the direct path
    /// because it doesn't go through the relay (yet). Null when no game is running or no game server is seen.
    /// </summary>
    public JsonObject? PingSample => _ping?.DeepClone() as JsonObject;

    /// <summary>The game PingSample's top-level ping is for: the first running game with a relayed server, else the first
    /// with any server. Null without one.</summary>
    public string? PingGame => _pingGame;

    /// <summary>
    /// Once a second while relayed games run with the tunnel up, for each game at once (MeasureGame): its longest-standing TCP server inside the
    /// relayed ranges, timed by Windows' own RTT for that connection (tcp_rtt; the game's real traffic through the
    /// relay), or by ICMP when there's no RTT sample (icmp; the echo takes the same route), or for a UDP server that
    /// ignores ICMP by its "port unreachable" next to the game port (udp_unreachable, UnreachableProbe). Loss comes from ICMP when the
    /// server answers it at all, jitter from how much successive figures differ. The relay leg (this PC to the relay)
    /// is pinged too.
    /// </summary>
    private void PingTick()
    {
        if (Interlocked.Exchange(ref _pingTicking, 1) == 1) return;
        try
        {
            var applied = _applied;
            // Every enabled game that's running: a launcher left open (LOTRO's, next to Last Epoch) counts as a running
            // game but has no game server, so the game timed is the first with a relayed server, else the first with any.
            var games = _play.Running.Where(g => Enabled(g.Key)).ToList();
            foreach (var gone in _pingStates.Keys.Where(k => games.All(g => g.Key != k)).ToList()) _pingStates.Remove(gone);
            if (games.Count == 0) { Clear(); return; }

            var carried = (applied ?? []).Select(r => Cidr.TryParse(r, out var c) ? c : (Cidr?)null).OfType<Cidr>().ToList();
            var tcp = TcpTable.Established();
            var allPeers = _udp.Read(games.SelectMany(g => g.Pids).ToHashSet());
            var found = games.Select(g => (Game: g, Server: FindServer(g))).Where(f => f.Server.Remote != 0).ToList();
            if (found.Count == 0) { Clear(); return; }

            // The relay leg first (a profile's estimate for a silent server builds on it), then every game at once (a server
            // that ignores ping costs up to a second), and the connections' pings meanwhile.
            var relayMs = _relayTunnelAddress is { } relay ? Probe.IcmpRouted(IPAddress.Parse(relay), timeoutMs: 500) : null;
            var measuring = found.Select(f =>
            {
                if (!_pingStates.TryGetValue(f.Game.Key, out var state)) _pingStates[f.Game.Key] = state = new PingState();
                return Task.Run(() => MeasureGame(f.Game, f.Server, state, relayMs));
            }).ToArray();
            var paths = _settings.Duplicate && _helperDuplicate == true ? PathPings() : null;
            Task.WaitAll(measuring);

            var rows = new JsonArray();
            JsonObject? main = null;
            foreach (var (task, (game, server)) in measuring.Zip(found))
            {
                var (row, measured) = task.Result;
                row["route"] = Route(server.Routed, measured.Profile, measured.RouteEdge, relayMs, measured.Hidden, measured.Estimate, measured.EstimateLeg, measured.Where);
                if (main is null && server.Routed) main = row;
                rows.Add(row);
            }
            main ??= (JsonObject)rows[0]!;

            // The main game's figures at the top level, as before there was a list.
            var sample = (JsonObject)main.DeepClone();
            var mainRouted = sample["routed"]!.GetValue<bool>();
            _pingGame = sample["game_key"]!.GetValue<string>();
            foreach (var name in new[] { "game_key", "game_name", "routed" }) sample.Remove(name);
            sample["relay"] = new JsonObject
            {
                // False when the main game's server is timed on the direct path (not relayed, or held direct for now).
                ["routed"] = mainRouted,
                ["endpoint"] = _relayEndpoint,
                ["ms"] = relayMs,
                ["backup"] = _settings.Duplicate && _helperDuplicate == true,
                ["paths_ok"] = _settings.Duplicate ? Interlocked.Read(ref _pathsOk) : null,
                // With "Use phone as backup": each connection's ping to the relay server, and "both" (see PathPings).
                ["paths"] = paths?.Paths,
                ["both"] = paths?.Both,
            };
            sample["games"] = rows;
            _ping = sample;

            // One game's server: through the relay if it has one there, else its live server on the direct path.
            (TcpTable.Row Row, uint Remote, int Port, string Proto, bool Routed) FindServer(RunningGame game)
            {
                var pids = game.Pids.ToHashSet();
                var udpPeers = allPeers.Where(p => pids.Contains(p.Pid)).ToList();
                // A live real-time UDP stream through the relay (2+ batches a second each way) is the game's own traffic,
                // ahead of any TCP connection: Marathon plays over UDP via Valve's Sydney relay (18-28 ms) while it also
                // holds a TCP connection to Bungie's backend in the US, which showed as a 200 ms ping.
                // The same when it goes direct: Dark and Darker's match server (15.129.1.229:7787) is learned during the
                // match but held off the relay until it ends, while its lobby's TCP (52.223.44.23:20202) is relayed and
                // showed as the ping.
                var streams = udpPeers.Where(p => p.Quiet < TimeSpan.FromSeconds(5) && IsGameFlow(p)).ToList();
                if (streams.FirstOrDefault(p => carried.Any(c => c.Contains(p.Remote))) is { Remote: not 0 } stream)
                    return (default, stream.Remote, stream.Port, "udp", true);
                if (streams.FirstOrDefault() is { Remote: not 0 } directStream)
                    return (default, directStream.Remote, directStream.Port, "udp", false);
                // Game servers rather than its web requests: not 80/443 when there's a choice. A game whose server is UDP
                // (Tarkov's raids) has none of those: then its busiest live UDP server through the relay, timed by ICMP.
                var server = tcp.Where(r => pids.Contains(r.Pid) && carried.Any(c => c.Contains(r.Remote)))
                    .OrderBy(r => r.RemotePort is 80 or 443).FirstOrDefault();
                var (remote, port, proto) = (server.Remote, server.RemotePort, "tcp");
                if (remote == 0 && udpPeers.FirstOrDefault(p => p.Quiet < TimeSpan.FromSeconds(5) && carried.Any(c => c.Contains(p.Remote))) is { Remote: not 0 } udp)
                    (remote, port, proto) = (udp.Remote, udp.Port, "udp");
                // Not through the relay (not learned yet, or a raid server held direct until its raid ends): the game's live
                // server is still timed, on the direct path, so the site shows its ping marked "direct". Its UDP stream
                // comes first here too.
                var routed = remote != 0;
                if (!routed && udpPeers.FirstOrDefault(p => p.Quiet < TimeSpan.FromSeconds(5) && IsGameFlow(p)) is { Remote: not 0 } direct)
                    (remote, port, proto) = (direct.Remote, direct.Port, "udp");
                if (!routed && remote == 0 && tcp.FirstOrDefault(r => pids.Contains(r.Pid) && IsPublic(r.Remote) && !NotLearnedPorts.Contains(r.RemotePort) && !IsLauncher(r.Pid)) is { Remote: not 0 } row)
                    (server, remote, port, proto) = (row, row.Remote, row.RemotePort, "tcp");
                return (server, remote, port, proto, routed);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Relay ping failed", ex);
            Clear();
        }
        finally
        {
            Volatile.Write(ref _pingTicking, 0);
        }

        void Clear()
        {
            _ping = null;
            _pingGame = null;
        }
    }

    /// <summary>The route's inputs from MeasureGame (the route itself needs the relay leg, timed alongside).</summary>
    private sealed record Measured(GameProfile? Profile, double? RouteEdge, (double Ms, string From, DateTime? At) Hidden, double? Estimate,
        double? EstimateLeg, string? Where);

    /// <summary>One game's ping this second: its server timed (tcp_rtt, icmp or udp_unreachable; profile_estimate behind
    /// a CDN), as its entry in the sample's games list, without the route.</summary>
    private (JsonObject Row, Measured Measured) MeasureGame(RunningGame game, (TcpTable.Row Row, uint Remote, int Port, string Proto, bool Routed) server, PingState state, int? relayMs)
    {
        var (row, remote, port, proto, routed) = server;
        var address = new IPAddress(remote);
        var key = $"{address}:{port}";
        if (state.For != key)
        {
            state.For = key;
            state.Pings.Clear();
            state.Answers.Clear();
            state.LastIcmp = null;
            state.HopTracedAt = null;
            lock (state) { state.Estimate = null; state.EstimateNew = false; state.Hop = null; }
            state.EstimateNext = DateTime.MinValue;
        }

        var now = DateTime.UtcNow;
        var rtt = proto == "tcp" ? TcpTable.Rtt(row) : null;
        // ICMP every second, always. A UDP server that hasn't answered it for a while (WARDOGS never does) is also timed,
        // at the same time, by the "port unreachable" its own host sends for a byte to a closed port next to the game's.
        // ICMP used to stop for good once such a port answered before ICMP had: a Tarkov raid server (CDN77, which
        // drops some pings) missed its first ping, port +1 (another game instance) answered now and then, and the ping
        // showed "–" for the whole raid.
        var icmpLately = state.LastIcmp is { } last && now - last.At < IcmpQuietFor;
        var unreachableTask = proto == "udp" && !icmpLately && _unreachable.CanTry(address, port)
            ? Task.Run(() => _unreachable.Probe(address, port)) : null;
        var icmpNow = Probe.IcmpRouted(address);
        if (icmpNow is { } answered) state.LastIcmp = (answered, now);
        (int? Port, double? Ms) unreachable = unreachableTask?.GetAwaiter().GetResult() ?? default;
        if (icmpNow is not null) unreachable = default; // ICMP answered: that's the figure
        Push(state.Answers, icmpNow is not null || unreachable.Ms is not null);
        // A server that drops some pings (rate limits) keeps its last answer for a few seconds rather than flickering
        // to "–"; the miss still counts as loss.
        var icmp = icmpNow ?? (state.LastIcmp is { } held && now - held.At < IcmpHold ? held.Ms : null);
        // Behind a CDN (a game profile covers the server) both only reach the CDN's edge: the profile's target in
        // the servers' real region is the figure then, and the edge's stays alongside.
        var edge = rtt ?? icmp ?? unreachable.Ms;
        // For the route, ICMP first: a quiet TCP connection's RTT runs high (delayed acks), and the edge answers ping.
        var routeEdge = icmp ?? rtt ?? unreachable.Ms;
        var profile = _profiles.Find(game.Key, address);
        // A profile with an edge → region leg: the live path to the edge plus that leg (calibrated every few minutes,
        // else the profile's fixed figure), every second: the route's total. Without one, the time to the profile's target.
        var hidden = profile?.EdgeToServerMs is { } fixedMs ? HiddenLeg(profile, fixedMs) : default;
        // A silent server through the relay (LOTRO): the live relay leg plus the relay server's traced time to it.
        var (estimate, fresh) = profile is null ? (null, false)
            : profile.EdgeToServerMs is not null ? (routeEdge + hidden.Ms, true)
            : profile.RelayToServerMs is { } relayLeg && routed ? (relayMs + relayLeg, true)
            : ProfileEstimate(profile, state);
        // A server that answers nothing and has no profile (ARC Raiders on Google Cloud: no ping, no port unreachable):
        // the last router on the direct route that answers (hop_estimate), a lower bound.
        var silent = profile is null && edge is null && !icmpLately && unreachable.Port is null;
        var hop = silent ? HopEstimate(address, routed, relayMs, state) : default;
        // No router of the server's own network answers (Dark and Darker on AWS: silent right after Starlink): the
        // server's cloud region (cloud_estimate).
        var cloud = silent && hop.Ms is null ? CloudEstimate(address, routed, relayMs) : default;
        var ping = profile is null ? edge ?? hop.Ms ?? cloud.Ms : estimate;
        // A held estimate repeats between measurements; only new ones go into the jitter window.
        if (ping is { } p && (profile is null || fresh)) Push(state.Pings, p);

        var values = state.Pings.ToArray();
        double? jitter = values.Length >= 2 ? values.Zip(values.Skip(1), (a, b) => Math.Abs(b - a)).Average() : null;
        return (new JsonObject
        {
            ["game_key"] = game.Key,
            ["game_name"] = game.Name,
            // False when this game's server is timed on the direct path (not relayed, or held direct for now).
            ["routed"] = routed,
            ["ping_ms"] = ping is { } v ? Math.Round(v, 1) : null,
            ["jitter_ms"] = jitter is { } j ? Math.Round(j, 1) : null,
            ["loss_pct"] = state.Answers.Any(a => a) ? Math.Round(state.Answers.Count(a => !a) * 100.0 / state.Answers.Count, 1) : null,
            ["window"] = values.Length,
            ["ping_method"] = profile is not null ? (estimate is not null ? "profile_estimate" : null)
                : rtt is not null ? "tcp_rtt" : icmp is not null ? "icmp" : unreachable.Ms is not null ? "udp_unreachable"
                : hop.Ms is not null ? "hop_estimate" : cloud.Ms is not null ? "cloud_estimate" : null,
            // service: set when the address is a relay service in front of the real server (its ping is only to there).
            ["server"] = new JsonObject { ["ip"] = address.ToString(), ["port"] = port, ["proto"] = proto,
                ["service"] = SteamRelay.Any(c => c.Contains(remote)) ? "Steam Datagram Relay" : null },
            // With udp_unreachable: the closed port that answers and this second's time (null: a miss, counted as loss).
            ["unreachable"] = unreachable.Port is { } up ? new JsonObject { ["port"] = up, ["ms"] = unreachable.Ms is { } um ? Math.Round(um, 1) : null } : null,
            // With profile_estimate: the CDN edge's own figure (tcp_rtt or icmp), and where the servers really are.
            ["edge_ms"] = profile is not null && edge is { } e ? Math.Round(e, 1) : null,
            ["profile"] = profile is null ? null : new JsonObject { ["name"] = profile.Name, ["region"] = profile.Region, ["target"] = profile.Target },
            // With hop_estimate: the router timed instead (its hop count on the direct route) and its own round trip.
            ["hop"] = hop.Ip is null ? null : new JsonObject { ["ip"] = hop.Ip.ToString(), ["ttl"] = hop.Ttl, ["ms"] = hop.HopMs is { } hm ? Math.Round(hm, 1) : null },
            // With cloud_estimate: the server's cloud region and the time to its endpoint (on the direct path).
            ["cloud"] = cloud.Region is not { } cr ? null : new JsonObject
            {
                ["provider"] = cr.Provider, ["region"] = cr.Code, ["name"] = cr.Name, ["ms"] = cloud.RegionMs is { } cm ? Math.Round(cm, 1) : null,
            },
        }, new Measured(profile, routeEdge, hidden, estimate, hop.Ms is not null ? hop.Leg : cloud.Leg,
            cloud.Ms is not null && hop.Ms is null ? cloud.Region?.Name ?? cloud.Region?.Code : null));
    }

    /// <summary>Valve's networks (AS32590), where Steam Datagram Relay's relays sit: a game played through them (Marathon)
    /// talks to the nearest relay, which carries the traffic on to the real server over Valve's own network, so the ping
    /// is only to the relay.</summary>
    private static readonly Cidr[] SteamRelay = new[] { "103.10.124.0/23", "103.28.54.0/23", "146.66.152.0/21", "155.133.224.0/19",
        "162.254.192.0/21", "185.25.180.0/22", "192.69.96.0/22", "205.196.6.0/24", "208.64.200.0/22", "208.78.164.0/22", "45.121.184.0/22" }
        .Select(r => Cidr.TryParse(r, out var c) ? c : default).ToArray();

    private static readonly TimeSpan HopRetrace = TimeSpan.FromMinutes(3);

    /// <summary>
    /// For a server that answers nothing: the last router on the direct route that does (traced tracert style every 3
    /// minutes in the background, then timed each second by ping, or by the expired echo when it ignores ping). ARC
    /// Raiders' Google Cloud Sydney server: Google's edge 72.14.213.44 at hop 6, 15-19 ms, with nothing answering past
    /// it. Direct: the ping is that router's time, a lower bound. Through the relay: the live relay leg plus relay →
    /// server, taken as the router's time minus the relay server's, both on the direct path from here (as AION's hidden
    /// leg), since a trace through the tunnel gets no answers past the relay. Leg is the route's last leg.
    /// </summary>
    private (double? Ms, double? Leg, IPAddress? Ip, int Ttl, double? HopMs) HopEstimate(IPAddress server, bool routed, int? relayMs, PingState state)
    {
        var now = DateTime.UtcNow;
        if (Probe.DefaultSource() is not { } direct) return default;
        if ((state.HopTracedAt is not { } at || now - at > HopRetrace) && state.HopTracing is null or { IsCompleted: true })
        {
            state.HopTracedAt = now;
            state.HopTracing = Task.Run(() =>
            {
                var (hops, reached) = TraceHops(server, direct);
                // The last router must be on the way to the server, not this PC's own ISP's: Dark and Darker's AWS server
                // went silent right after Starlink's ground station (206.224.66.40), which would read the same for a
                // server anywhere in the world. Then there's no hop, and the cloud region is timed instead.
                (IPAddress Ip, byte Ttl)? last = reached || hops.Count == 0 ? null : hops[^1];
                if (last is { } l && IsHomeHop(l.Ip, direct)) last = null;
                lock (state) state.Hop = last;
            });
        }
        (IPAddress Ip, byte Ttl)? traced;
        lock (state) traced = state.Hop;
        if (traced is not { } h) return default;
        double? hopMs = Probe.Icmp(h.Ip, direct, tries: 1, timeoutMs: 700) ?? Probe.IcmpTtl(server, direct, h.Ttl).Ms;
        if (hopMs is not { } ms) return (null, null, h.Ip, h.Ttl, null);
        var (total, leg) = ThroughRelay(ms, routed, relayMs, direct);
        return (total, leg, h.Ip, h.Ttl, ms);
    }

    /// <summary>
    /// A time measured on the direct path, as the game's ping: on its own when the game goes direct; through the relay,
    /// the live relay leg plus max(0, that time − the relay server's on the direct path) as the last leg (as AION's
    /// hidden leg). Nulls when the relay server can't be timed.
    /// </summary>
    private (double? Total, double? Leg) ThroughRelay(double directMs, bool routed, int? relayMs, IPAddress direct)
    {
        if (!routed) return (directMs, directMs);
        if (relayMs is null || !IPEndPoint.TryParse(_relayEndpoint ?? "", out var endpoint) || IPAddress.IsLoopback(endpoint.Address)
            || Probe.Icmp(endpoint.Address, direct, tries: 1, timeoutMs: 500) is not { } relayDirect)
            return (null, null);
        var leg = Math.Max(0, directMs - relayDirect);
        return (relayMs + leg, leg);
    }

    /// <summary>
    /// For a silent server without a usable router before it: the time to a public endpoint in its cloud region (AWS's
    /// API endpoint, Google Cloud's gcping service), from AWS's and Google's published address lists (CloudRegions).
    /// Dark and Darker's match server 15.129.1.229 is AWS Sydney (ap-southeast-2). Null for other hosts.
    /// </summary>
    private (double? Ms, double? Leg, CloudRegion? Region, double? RegionMs) CloudEstimate(IPAddress server, bool routed, int? relayMs)
    {
        if (_cloud.Find(server) is not { IsGlobal: false, Endpoint: not null } region || Probe.DefaultSource() is not { } direct) return default;
        if (_cloud.ProbeAsync(region).GetAwaiter().GetResult() is not { } ms) return (null, null, region, null);
        var (total, leg) = ThroughRelay(ms, routed, relayMs, direct);
        return (total, leg, region, ms);
    }

    /// <summary>Whether the router is this PC's own ISP's: its /24 is on the route to 1.1.1.1 too (traced every 10 minutes).</summary>
    private bool IsHomeHop(IPAddress hop, IPAddress source)
    {
        lock (_homeGate)
        {
            if (DateTime.UtcNow - _homeTracedAt > TimeSpan.FromMinutes(10))
            {
                _homeHops = TraceHops(IPAddress.Parse("1.1.1.1"), source).Hops.Select(h => Slash24Key(h.Ip)).ToHashSet();
                _homeTracedAt = DateTime.UtcNow;
            }
            return _homeHops.Contains(Slash24Key(hop));
        }

        static uint Slash24Key(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes()) & 0x00FFFFFF;
    }

    /// <summary>Tracert from this source: the public routers that answer on the way (4 silent hops in a row ends it), and
    /// whether the target itself answered.</summary>
    private static (List<(IPAddress Ip, byte Ttl)> Hops, bool Reached) TraceHops(IPAddress target, IPAddress source)
    {
        var hops = new List<(IPAddress Ip, byte Ttl)>();
        var silent = 0;
        for (byte ttl = 1; ttl <= 24 && silent < 4; ttl++)
        {
            var (from, _, reached) = Probe.IcmpTtl(target, source, ttl);
            if (reached) return (hops, true);
            if (from is null) { silent++; continue; }
            silent = 0;
            if (IsPublic(BitConverter.ToUInt32(from.GetAddressBytes()))) hops.Add((from, ttl));
        }
        return (hops, false);
    }

    /// <summary>
    /// With "Use phone as backup", for the site's connection page: this second's ping to the relay server over each
    /// connection (ICMP from that adapter's address, so it leaves through it), and over the last minute each one's
    /// average, median, worst and lost pings. "Both" is the faster answer each second, which is what duplication gets:
    /// a ping lost on one connection still counts when the other answered. Null without the helper's connection list.
    /// </summary>
    private (JsonArray Paths, JsonObject Both)? PathPings()
    {
        var paths = _paths;
        if (paths.Length == 0 || !IPEndPoint.TryParse(_relayEndpoint ?? "", out var endpoint) || IPAddress.IsLoopback(endpoint.Address)) return null;

        var probes = paths.Select(p => Task.Run(() => AdapterAddress(p.Name) is { } source
            ? Probe.Icmp(endpoint.Address, source, tries: 1, timeoutMs: 900) : null)).ToArray();
        Task.WaitAll(probes);
        var rows = new JsonArray();
        int? best = null;
        for (var i = 0; i < paths.Length; i++)
        {
            var (name, phone, sent, received, quiet) = paths[i];
            var ms = probes[i].Result;
            if (ms is { } m && (best is null || m < best)) best = m;
            if (!_pathPings.TryGetValue(name, out var window)) _pathPings[name] = window = new();
            PushPath(window, ms);
            var row = PathSummary(window);
            row["name"] = name;
            row["phone"] = phone;
            row["ms"] = ms;
            row["sent"] = sent;
            row["received"] = received;
            row["quiet_s"] = quiet;
            rows.Add(row);
        }
        PushPath(_bothPings, best);
        var both = PathSummary(_bothPings);
        both["ms"] = best;
        return (rows, both);
    }

    /// <summary>
    /// The game connection leg by leg, for the site's connection page: PC → relay (live, through the tunnel; the
    /// connections under it are relay.paths), relay → game server (the server's ping through the relay minus the relay's),
    /// or PC → game server when it goes direct. Behind a CDN (a game profile) the measured legs end at the CDN's edge and
    /// a last leg, edge → the servers' region, is the profile's fixed estimate (edgeToServerMs), so the total moves with
    /// the live legs. A server that answers nothing (relayToServerMs, LOTRO): PC → relay live and relay → server the
    /// profile's traced estimate, or on the direct path PC → server the time to the profile's target. total_ms is null
    /// when a leg is missing this second.
    /// </summary>
    private static JsonObject Route(bool routed, GameProfile? profile, double? edge, int? relayMs, (double Ms, string From, DateTime? At) hidden,
        double? estimate, double? estimateLeg, string? where)
    {
        var legs = new JsonArray();
        // A silent server timed by the last router before it (hop_estimate) or its cloud region (cloud_estimate, where
        // names the region): the last leg is that estimate.
        if (profile is null && edge is null && estimateLeg is { } viaHop)
        {
            if (routed) legs.Add(Leg("PC", "Relay", relayMs, "live"));
            legs.Add(Leg(routed ? "Relay" : "PC", where is null ? "Game server" : $"Game server ({where})", viaHop, "estimate"));
            var hopTotal = routed ? relayMs + viaHop : viaHop;
            return new JsonObject { ["legs"] = legs, ["total_ms"] = hopTotal is { } ht ? Math.Round(ht, 1) : null };
        }
        if (profile?.RelayToServerMs is { } relayLeg)
        {
            var server = $"Game server ({profile.Region})";
            if (routed)
            {
                legs.Add(Leg("PC", "Relay", relayMs, "live"));
                var leg = Leg("Relay", server, relayLeg, "estimate");
                leg["calibrated"] = "fixed";
                legs.Add(leg);
            }
            else legs.Add(Leg("PC", server, estimate, "estimate"));
            return new JsonObject { ["legs"] = legs, ["total_ms"] = estimate is { } est ? Math.Round(est, 1) : null };
        }

        var end = profile is null ? "Game server" : "CDN edge";
        if (routed)
        {
            legs.Add(Leg("PC", "Relay", relayMs, "live"));
            legs.Add(Leg("Relay", end, edge is { } e && relayMs is { } r ? Math.Max(0, e - r) : null, "measured"));
        }
        else legs.Add(Leg("PC", end, edge, "live"));
        var total = edge;
        if (profile?.EdgeToServerMs is not null)
        {
            var leg = Leg("CDN edge", $"Game server ({profile.Region})", hidden.Ms, "estimate");
            leg["calibrated"] = hidden.From; // "pc" (the region minus Sydney, from here) or "fixed"
            leg["calibrated_at"] = hidden.At?.ToString("o");
            legs.Add(leg);
            total = edge + hidden.Ms;
        }
        return new JsonObject { ["legs"] = legs, ["total_ms"] = total is { } t ? Math.Round(t, 1) : null };

        static JsonObject Leg(string from, string to, double? ms, string kind) =>
            new() { ["from"] = from, ["to"] = to, ["ms"] = ms is { } m ? Math.Round(m, 1) : null, ["kind"] = kind };
    }

    private static readonly TimeSpan CalibrateEvery = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, (double Ms, string From, DateTime At)> _hiddenLegs = []; // per profile name
    private DateTime _calibrateNext = DateTime.MinValue;

    /// <summary>
    /// The hidden leg of a profile's route (CDN edge → the servers' region), recalibrated every 2 minutes from this PC: 5
    /// pings to the profile's target interleaved with 5 to the relay server's public address (Sydney, near the edge),
    /// median minus median, both on the normal path. Not from the relay: the Linode's own transit to the US is far slower
    /// than the CDN's backbone (Phoenix 327-334 ms from the relay vs ~135 from Sydney over Starlink), and a CDN-fronted
    /// game's traffic leaves the relay at the CDN's Sydney edge. A reading outside 0.6-1.5x the profile's fixed figure
    /// (a phone with a reset metric once made it 351) is ignored. Until one is kept, the fixed figure.
    /// </summary>
    private (double Ms, string From, DateTime? At) HiddenLeg(GameProfile profile, int fixedMs)
    {
        lock (_hiddenLegs) return HiddenLegLocked(profile, fixedMs); // games are measured in parallel
    }

    private (double Ms, string From, DateTime? At) HiddenLegLocked(GameProfile profile, int fixedMs)
    {
        var now = DateTime.UtcNow;
        if (now >= _calibrateNext && ProfileTarget(profile.Target) is { } target && IPEndPoint.TryParse(_relayEndpoint ?? "", out var sydney))
        {
            _calibrateNext = now + CalibrateEvery;
            var far = new List<int>(); var near = new List<int>();
            for (var i = 0; i < 5; i++)
            {
                if (Probe.IcmpRouted(target) is { } f) far.Add(f);
                if (Probe.IcmpRouted(sydney.Address, timeoutMs: 500) is { } n) near.Add(n);
            }
            if (far.Count >= 2 && near.Count >= 2)
            {
                var ms = Math.Max(0, far.Order().ElementAt(far.Count / 2) - near.Order().ElementAt(near.Count / 2));
                if (ms < fixedMs * 0.6 || ms > fixedMs * 1.5)
                    Log.Info($"Relay: ignored {profile.Name}'s hidden leg reading of {ms} ms (expected about {fixedMs})");
                else
                {
                    if (!_hiddenLegs.TryGetValue(profile.Name, out var old) || Math.Abs(old.Ms - ms) >= 10)
                        Log.Info($"Relay: {profile.Name}'s hidden leg ({profile.Region}) calibrated to {ms} ms");
                    _hiddenLegs[profile.Name] = (ms, "pc", now);
                }
            }
        }
        return _hiddenLegs.TryGetValue(profile.Name, out var leg) ? (leg.Ms, leg.From, leg.At) : (fixedMs, "fixed", null);
    }

    private static void PushPath(Queue<int?> window, int? ms)
    {
        window.Enqueue(ms);
        while (window.Count > PathWindow) window.Dequeue();
    }

    /// <summary>A connection's last minute: avg_ms, median_ms, worst_ms of the answered pings, lost of count.</summary>
    private static JsonObject PathSummary(Queue<int?> window)
    {
        var answered = window.OfType<int>().Order().ToArray();
        return new JsonObject
        {
            ["avg_ms"] = answered.Length > 0 ? Math.Round(answered.Average(), 1) : null,
            ["median_ms"] = answered.Length > 0 ? answered[answered.Length / 2] : null,
            ["worst_ms"] = answered.Length > 0 ? answered[^1] : null,
            ["lost"] = window.Count(ms => ms is null),
            ["count"] = window.Count,
        };
    }

    /// <summary>The IPv4 address of the network adapter with this name (the helper names connections by adapter), or null.</summary>
    private static IPAddress? AdapterAddress(string name) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name && n.OperationalStatus == OperationalStatus.Up)
            ?.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;

    /// <summary>
    /// The time to a game profile's target: measured every 5 seconds (ICMP, then the faster of two TCP handshakes on 443
    /// when the ping isn't answered) and held for up to 30 seconds. Cloud hosts rate-limit ping: Oracle's Phoenix servers drop every
    /// third ping at one a second, and a tray app pinging every second stopped getting answers at all (0.4.1-beta.11).
    /// Fresh is true when a new figure came in since the last call.
    /// </summary>
    private (double? Ms, bool Fresh) ProfileEstimate(GameProfile profile, PingState state)
    {
        var now = DateTime.UtcNow;
        // In the background: a target that misses its ping falls back to two TCP handshakes of up to 2 s each, which
        // would hold up every game's ping this second (LOTRO's router target: 5 s ticks).
        if (now >= state.EstimateNext && state.Measuring is null or { IsCompleted: true })
        {
            state.EstimateNext = now + EstimateEvery;
            state.Measuring = Task.Run(() =>
            {
                if (ProfileTarget(profile.Target) is { } target && (Probe.IcmpRouted(target) ?? Probe.TcpHandshake(target, 443)) is { } ms)
                    lock (state) { state.Estimate = (ms, DateTime.UtcNow); state.EstimateNew = true; }
            });
        }
        lock (state)
        {
            var fresh = state.EstimateNew;
            state.EstimateNew = false;
            return (state.Estimate is { } e && now - e.At < EstimateHold ? e.Ms : null, fresh);
        }
    }

    /// <summary>A game profile's target host as an IPv4 address, looked up every 10 minutes (a minute after a failed lookup).</summary>
    private IPAddress? ProfileTarget(string host)
    {
        lock (_profiles) return ProfileTargetLocked(host); // games are measured in parallel
    }

    private IPAddress? ProfileTargetLocked(string host)
    {
        var (cached, address, at) = _profileTarget;
        if (cached == host && DateTime.UtcNow - at < (address is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10))) return address;
        try
        {
            // Keep the same address while DNS still lists it (round robin), so successive measurements compare.
            var found = Dns.GetHostAddresses(host).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
            address = cached == host && address is not null && found.Contains(address) ? address : found.FirstOrDefault();
        }
        catch (SocketException) { address = null; }
        _profileTarget = (host, address, DateTime.UtcNow);
        return address;
    }

    private static void Push<T>(Queue<T> window, T value)
    {
        window.Enqueue(value);
        while (window.Count > PingWindow) window.Dequeue();
    }

    /// <summary>The games the tray lists: running ones first, then every game with relay settings.</summary>
    public IReadOnlyList<(string Key, string Name, bool Enabled, int Ranges, bool Running)> Games()
    {
        lock (_gate)
        {
            var running = _play.Running;
            var list = running.Select(g => (g.Key, g.Name, Enabled(g.Key), RangeCount(g.Key), true)).ToList();
            foreach (var (key, game) in _settings.Games.OrderBy(g => g.Value.Name))
                if (!running.Any(r => r.Key == key)) list.Add((key, game.Name, game.Enabled, RangeCount(key), false));
            return list;
        }
    }

    // Every game goes through the relay unless it's been unticked (excluded) in the menu.
    private bool Enabled(string key) => !_settings.Games.TryGetValue(key, out var g) || g.Enabled;
    private int RangeCount(string key) => _settings.Games.TryGetValue(key, out var g) ? g.Ranges.Union(g.Learned).Count() : 0;

    public void SetEnabled(string key, string name, bool enabled)
    {
        lock (_gate)
        {
            Game(key, name).Excluded = !enabled;
            SaveSettings();
            Log.Info($"Relay {(enabled ? "on" : "off")} for {name}");
        }
        _ = TickAsync(); // applies now if no game is running
    }

    private RelayGame Game(string key, string name)
    {
        if (!_settings.Games.TryGetValue(key, out var game)) _settings.Games[key] = game = new RelayGame();
        game.Name = name;
        return game;
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var running = _play.Running;
            Learn(running);
            if (running.Count == 0 || _settings.SwitchMidGame) await ApplyAsync();
            Pending = running.Count > 0 && !_settings.SwitchMidGame && !DesiredRanges().SequenceEqual(_applied ?? []);
            await SyncDuplicateAsync();
            await CheckHealthAsync(running.Count > 0);
        }
        catch (Exception ex)
        {
            Log.Error("Relay check failed", ex);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>Web (80, 443, Cloudflare's other HTTP/HTTPS ports, 8080/8443) and Google push (5228-5230): never game servers.</summary>
    private static readonly HashSet<int> NotLearnedPorts = [80, 443, 2052, 2053, 2082, 2083, 2086, 2087, 2095, 2096, 8080, 8443, 8880, 5228, 5229, 5230];

    /// <summary>Whether a UDP flow looks like a game server's: public, not a service or launcher, and at least 2 batches a
    /// second both ways since it was first seen.</summary>
    private bool IsGameFlow(UdpPeers.Peer peer)
    {
        var seconds = Math.Max(1, peer.Age.TotalSeconds);
        return IsPublic(peer.Remote) && !NotLearnedPorts.Contains(peer.Port) && !UdpServicePorts.Contains(peer.Port) && !IsLauncher(peer.Pid)
            && peer.Sent >= 2 * seconds && peer.Received >= 2 * seconds;
    }

    /// <summary>UDP services that are never a game server: DNS, NTP, NetBIOS, SSDP, STUN, mDNS, LLMNR.</summary>
    private static readonly HashSet<int> UdpServicePorts = [53, 123, 137, 138, 1900, 3478, 3479, 5353, 5355, 19302];

    private readonly Dictionary<int, bool> _launchers = [];

    /// <summary>Whether the process is a game launcher or store client (counted in the game's session for play time, but
    /// its connections are notifications and web, not the game). Remembered per pid until no game runs.</summary>
    private bool IsLauncher(int pid)
    {
        lock (_launchers) // the ping timer asks too
        {
            if (_launchers.TryGetValue(pid, out var known)) return known;
            string name;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
            return _launchers[pid] = name.EndsWith("Launcher", StringComparison.OrdinalIgnoreCase)
                || name is "Battle.net" or "Agent" or "RiotClientServices" or "EADesktop" or "Origin" or "UbisoftConnect" or "upc"
                    or "steam" or "steamwebhelper" or "GalaxyClient";
        }
    }

    /// <summary>Adds the /24 of every public server the running games have held a TCP connection to for a minute.</summary>
    private void Learn(IReadOnlyList<RunningGame> running)
    {
        var now = DateTimeOffset.UtcNow;
        var seen = new HashSet<(int, uint, int)>();
        var changed = false;
        if (running.Count > 0)
        {
            var byPid = new Dictionary<int, RunningGame>();
            foreach (var g in running) foreach (var pid in g.Pids) byPid.TryAdd(pid, g);
            foreach (var row in TcpTable.Established())
            {
                if (!byPid.TryGetValue(row.Pid, out var game) || !IsPublic(row.Remote) || NotLearnedPorts.Contains(row.RemotePort) || IsLauncher(row.Pid)) continue;
                var key = (row.Pid, row.Remote, row.RemotePort);
                seen.Add(key);
                if (!_firstSeen.TryGetValue(key, out var first)) _firstSeen[key] = first = now;
                if (now - first < (_settings.SwitchMidGame ? LearnAfterMidGame : LearnAfter)) continue;
                lock (_gate)
                {
                    var g = Game(game.Key, game.Name);
                    var range = Slash24(row.Remote);
                    if (g.Learned.Contains(range) || g.Ranges.Contains(range) || g.Learned.Count >= MaxRangesPerGame) continue;
                    g.Learned.Add(range);
                    changed = true;
                    Log.Info($"Relay: learned {range} for {game.Name} (port {row.RemotePort})");
                }
            }

            // UDP game servers (Tarkov's raids): a flow with traffic both ways, at least 2 batches a second each way, for as
            // long as a TCP server has to be held. Short lookups (DNS, STUN, mDNS, time) never get there.
            var live = new HashSet<string>();
            foreach (var peer in _udp.Read(byPid.Keys))
            {
                if (!byPid.TryGetValue(peer.Pid, out var game) || !IsPublic(peer.Remote) || NotLearnedPorts.Contains(peer.Port)
                    || UdpServicePorts.Contains(peer.Port) || IsLauncher(peer.Pid)) continue;
                var range = Slash24(peer.Remote);
                if (peer.Quiet < TimeSpan.FromSeconds(10)) live.Add(range);
                var seconds = peer.Age.TotalSeconds;
                if (peer.Age < (_settings.SwitchMidGame ? LearnAfterMidGame : LearnAfter) || peer.Quiet > TimeSpan.FromSeconds(5)
                    || peer.Sent < 2 * seconds || peer.Received < 2 * seconds) continue;
                lock (_gate)
                {
                    var g = Game(game.Key, game.Name);
                    if (g.Learned.Contains(range) || g.Ranges.Contains(range) || g.Learned.Count >= MaxRangesPerGame) continue;
                    g.Learned.Add(range);
                    changed = true;
                    Log.Info($"Relay: learned {range} for {game.Name} (UDP port {peer.Port}; joins the relay once that flow ends)");
                }
            }
            _liveUdp = live;
        }
        else _liveUdp = [];
        foreach (var gone in _firstSeen.Keys.Where(k => !seen.Contains(k)).ToList()) _firstSeen.Remove(gone);
        if (running.Count == 0) lock (_launchers) _launchers.Clear(); // pids get reused
        if (changed) lock (_gate) SaveSettings();
    }

    /// <summary>The relayed games' ranges, sorted.</summary>
    private List<string> DesiredRanges()
    {
        lock (_gate)
        {
            return _settings.Games.Values.Where(g => g.Enabled).SelectMany(g => g.Ranges.Union(g.Learned))
                .Where(r => Cidr.TryParse(r, out _)).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Makes the helper route exactly the relayed games' ranges, or takes the tunnel down when no relayed
    /// game has servers learned yet. Called while no game runs, or at any time with "Switch mid-game" on.</summary>
    private async Task ApplyAsync()
    {
        // A live UDP flow can't move onto or off the relay without breaking (it would end a Tarkov raid, which has no
        // reconnect like a TCP session): a range with one keeps its current path until that flow goes quiet.
        var live = _liveUdp;
        var desired = DesiredRanges();
        var ranges = desired.Where(r => !live.Contains(r) || (_applied?.Contains(r) ?? false))
            .Union((_applied ?? []).Where(live.Contains)).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        if (ranges.Count == 0)
        {
            if (_applied is not null || Status.StartsWith("Relay: starting"))
            {
                var down = await RelayClient.SendAsync(new JsonObject { ["cmd"] = "down" });
                if (down is not null) { _applied = null; Remember(down); }
            }
            SetStatus(desired.Count > 0 ? "Relay: off until the game's current server is done (next raid or match)" : "Relay: off (no game servers learned yet)");
            return;
        }
        if (DateTimeOffset.UtcNow < _downUntil || (_applied is not null && _applied.SequenceEqual(ranges))) return;

        var reply = await RelayClient.SendAsync(new JsonObject { ["cmd"] = "up", ["ranges"] = new JsonArray(ranges.Select(r => (JsonNode)r).ToArray()) });
        if (reply is null) { SetStatus("Relay: the relay helper isn't running"); return; }
        Remember(reply);
        if ((string?)reply["error"] is { } error) Log.Error($"Relay: the helper reported: {error}");
        if (!(bool)(reply["up"] ?? false)) return;
        _applied = ranges;
        _upSince = DateTimeOffset.UtcNow;
        _lastHealth = DateTimeOffset.MinValue; // check the relay straight away
        Log.Info($"Relay: up, {ranges.Count} range(s) go through the tunnel");
    }

    private async Task CheckHealthAsync(bool gameRunning)
    {
        if (_applied is null)
        {
            if (DesiredRanges().Count > 0)
                SetStatus(DateTimeOffset.UtcNow < _downUntil ? "Relay: the relay isn't answering; trying again in a few minutes"
                    : gameRunning && !_settings.SwitchMidGame ? "Relay: off until your game closes" : Status);
            return;
        }
        if (DateTimeOffset.UtcNow - _lastHealth < HealthEvery) return;
        _lastHealth = DateTimeOffset.UtcNow;

        var reply = await RelayClient.SendAsync(new JsonObject { ["cmd"] = "status" });
        if (reply is null) { SetStatus("Relay: the relay helper isn't running"); return; }
        Remember(reply);
        long? age = (long?)reply["handshake_age_s"];
        var fresh = age is { } a && a < StaleHandshake.TotalSeconds;
        string games;
        lock (_gate)
        {
            var on = _settings.Games.Values.Where(g => g.Enabled && g.Ranges.Union(g.Learned).Any()).Select(g => g.Name).ToList();
            games = on.Count <= 3 ? string.Join(", ", on) : $"{on.Count} games";
        }
        if (fresh || DateTimeOffset.UtcNow - _upSince < StaleHandshake)
        {
            var relay = (string?)reply["relay"];
            var rtt = relay is null ? null : await Task.Run(() => Probe.IcmpRouted(IPAddress.Parse(relay), tries: 3, timeoutMs: 1500));
            SetStatus($"Relay: on for {games}{(rtt is { } ms ? $" · {ms} ms to the relay" : fresh ? "" : " · connecting")}");
            return;
        }
        SetStatus("Relay: the relay isn't answering");
        if (!gameRunning)
        {
            await RelayClient.SendAsync(new JsonObject { ["cmd"] = "down" });
            _applied = null;
            _downUntil = DateTimeOffset.UtcNow + RetryDownAfter;
            Log.Info("Relay: no handshake for 3 minutes; tunnel down, trying again in 5 minutes");
        }
    }

    private void Remember(JsonObject reply)
    {
        HelperVersion = (string?)reply["version"] ?? HelperVersion;
        DuplicateAvailable = (bool?)reply["duplicate_available"] ?? false;
        _helperDuplicate = (bool?)reply["duplicate"];
        _relayTunnelAddress = (string?)reply["relay"] ?? _relayTunnelAddress;
        _relayEndpoint = (string?)reply["endpoint"] ?? _relayEndpoint;
        Interlocked.Exchange(ref _pathsOk, (reply["paths"] as JsonArray ?? []).OfType<JsonObject>().Count(p => (long?)p["quiet_s"] is <= 30));
        _paths = (reply["paths"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => ((string?)p["name"] ?? "?", (bool?)p["phone"] == true, (long?)p["sent"], (long?)p["received"], (long?)p["quiet_s"])).ToArray();
        PathLines = (reply["paths"] as JsonArray ?? []).OfType<JsonObject>().Select(p =>
        {
            var name = (string?)p["name"] ?? "?";
            var quiet = (long?)p["quiet_s"];
            var state = quiet is null ? "no reply yet" : quiet <= 30 ? $"✓ reply {quiet} s ago" : $"⚠ no reply for {quiet} s";
            return $"{((bool?)p["phone"] == true ? "📱 " : "")}{name}: {state}";
        }).ToList();
    }

    /// <summary>Switches "use phone as backup" now (it's safe mid-game: WireGuard just roams to the new path, the
    /// connections inside the tunnel don't change).</summary>
    public void SetSwitchMidGame(bool on)
    {
        lock (_gate)
        {
            _settings.SwitchMidGame = on;
            SaveSettings();
        }
        Log.Info($"Relay: switch mid-game {(on ? "on" : "off")}");
        _ = TickAsync(); // on: anything waiting for the game to close applies now
    }

    public void SetDuplicate(bool on)
    {
        lock (_gate)
        {
            _settings.Duplicate = on;
            SaveSettings();
        }
        Log.Info($"Relay: phone backup {(on ? "on" : "off")}");
        _ = TickAsync();
    }

    /// <summary>Tells the helper about "use phone as backup" when it differs from what the helper last said.</summary>
    private async Task SyncDuplicateAsync()
    {
        if (_helperDuplicate == _settings.Duplicate || (_settings.Duplicate && _helperDuplicate is not null && !DuplicateAvailable)) return;
        var reply = await RelayClient.SendAsync(new JsonObject { ["cmd"] = "duplicate", ["on"] = _settings.Duplicate });
        if (reply is null) return;
        Remember(reply);
        if ((string?)reply["error"] is { } error) Log.Error($"Relay: phone backup: {error}");
        else Log.Info($"Relay: phone backup is {(_settings.Duplicate ? "on" : "off")} in the helper");
    }

    /// <summary>
    /// Ping to the running game's servers through the relay and directly, for the tray's "Compare ping". Through the
    /// relay only works for ranges the tunnel carries now; the direct figure is forced onto the normal connection by
    /// sending from its own address. ICMP first; servers that drop it are timed by a TCP handshake to the game's own
    /// port (best of 3).
    /// </summary>
    public async Task<string> CompareAsync()
    {
        var running = _play.Running;
        if (running.Count == 0) return "No game is running.";
        var game = running[0];
        var pids = game.Pids.ToHashSet();
        var servers = TcpTable.Established().Where(r => pids.Contains(r.Pid) && IsPublic(r.Remote))
            .GroupBy(r => r.Remote).Select(g => g.First()).Take(4).ToList();
        if (servers.Count == 0) return $"{game.Name} has no server connection that can be seen (UDP games can't be compared).";

        var carried = (_applied ?? []).Select(r => Cidr.TryParse(r, out var c) ? c : (Cidr?)null).OfType<Cidr>().ToList();
        var direct = Probe.DefaultSource();
        var lines = new List<string> { game.Name };
        foreach (var s in servers)
        {
            var address = new IPAddress(s.Remote);
            var viaRelay = carried.Any(c => c.Contains(s.Remote));
            var relayMs = viaRelay ? await Probe.TimeAsync(address, s.RemotePort, source: null) : null;
            var directMs = await Probe.TimeAsync(address, s.RemotePort, direct);
            lines.Add($"{address}:{s.RemotePort}  relay {(relayMs is { } r ? $"{r} ms" : viaRelay ? "no reply" : "not routed")}  ·  direct {(directMs is { } d ? $"{d} ms" : "no reply")}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void SetStatus(string status) => Status = status;

    /// <summary>Public unicast IPv4 (network byte order as the TCP table gives it): not private, loopback,
    /// link-local, CGNAT or multicast.</summary>
    private static bool IsPublic(uint raw)
    {
        byte a = (byte)raw, b = (byte)(raw >> 8);
        return !(a is 0 or 10 or 127 or >= 224
            || (a == 172 && b is >= 16 and <= 31)
            || (a == 192 && b == 168)
            || (a == 169 && b == 254)
            || (a == 100 && b is >= 64 and <= 127));
    }

    private static string Slash24(uint raw) => $"{(byte)raw}.{(byte)(raw >> 8)}.{(byte)(raw >> 16)}.0/24";

    private static RelaySettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new();
            var settings = JsonSerializer.Deserialize<RelaySettings>(File.ReadAllText(SettingsFile), Json) ?? new();
            if (settings.LearnedVersion < RelaySettings.CurrentLearnedVersion)
            {
                // Learned before web, push and launcher connections were skipped: mostly content networks' and Google's
                // /24s. Forget them once; each game's real servers are learned again within its first 20 seconds of play.
                // Typed-in ranges stay.
                foreach (var g in settings.Games.Values) g.Learned.Clear();
                settings.LearnedVersion = RelaySettings.CurrentLearnedVersion;
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, Json));
                Log.Info("Relay: cleared learned ranges once (web, push and launcher connections are no longer learned)");
            }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Error("Couldn't read relay.json; starting with no relay games", ex);
        }
        return new();
    }

    private void SaveSettings()
    {
        try { File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings, Json)); }
        catch (IOException ex) { Log.Error("Couldn't save relay.json", ex); }
    }

    public void Dispose()
    {
        _cloud.Dispose();
        _udp.Dispose();
        _timer.Dispose();
        _pingTimer.Dispose();
    }

    // ---- installing, updating and removing the helper (each one UAC prompt) ----

    /// <summary>Installs the packaged helper with the relay's client config. Null on success, "" when the UAC prompt
    /// was declined, else what went wrong.</summary>
    public static Task<string?> SetUpAsync(string clientConfig) => RunHelperElevatedAsync(PackagedHelper, ["install", clientConfig]);

    /// <summary>Re-installs the packaged (newer) helper, keeping the stored config.</summary>
    public static Task<string?> UpdateHelperAsync() => RunHelperElevatedAsync(PackagedHelper, ["install"]);

    /// <summary>Removes the helper, its files and the stored config, running the installed helper's own uninstall
    /// from a temporary copy (so it can delete its own file).</summary>
    public static async Task<string?> RemoveAsync()
    {
        var source = File.Exists(InstalledHelper) ? InstalledHelper : PackagedHelper;
        var copy = Path.Combine(Path.GetTempPath(), $"GamingEnjoyerRelay-uninstall-{Guid.NewGuid():N}.exe");
        try
        {
            File.Copy(source, copy);
            return await RunHelperElevatedAsync(copy, ["uninstall"]);
        }
        finally
        {
            try { File.Delete(copy); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<string?> RunHelperElevatedAsync(string exe, string[] args)
    {
        if (!File.Exists(exe)) return "This build of Gaming Enjoyer doesn't include the relay helper.";
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var process = Process.Start(start);
            if (process is null) return "The relay helper didn't start.";
            await process.WaitForExitAsync();
            return process.ExitCode switch
            {
                0 => null,
                1 when args[0] == "install" => "The relay helper couldn't be installed. Check the relay config (it needs PrivateKey, Address, PublicKey and Endpoint); details are in Windows' Application event log.",
                _ => $"The relay helper stopped with code {process.ExitCode}.",
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ""; // UAC prompt declined: nothing to say
        }
    }
}

/// <summary>The tray app's side of the relay helper's pipe: one JSON line out, one back. Null when the helper
/// isn't there or doesn't answer within a few seconds.</summary>
internal static class RelayClient
{
    private const string PipeName = "GamingEnjoyer.Relay";

    public static async Task<JsonObject?> SendAsync(JsonObject request)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // "up" runs netsh once per new route
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000, timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(request.ToJsonString().AsMemory(), timeout.Token);
            var line = await reader.ReadLineAsync(timeout.Token);
            return line is null ? null : JsonNode.Parse(line) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

internal sealed class RelaySettings
{
    public Dictionary<string, RelayGame> Games { get; set; } = new(StringComparer.Ordinal);
    public bool Duplicate { get; set; }
    public bool SwitchMidGame { get; set; } = true; // relay.json from before 0.4.1-beta.8 has none: on
    /// <summary>The learning rules learned ranges were kept under (LoadSettings clears them when older): 2 skipped web
    /// ports (0.4.1-beta.11), 3 also push ports and launchers. Older relay.json has none (0).</summary>
    public int LearnedVersion { get; set; }
    public const int CurrentLearnedVersion = 3;
}

/// <summary>One game's relay settings in relay.json: kept off the relay or not, ranges typed in by hand, ranges learned.
/// Games are relayed by default; betas before 0.4.1-beta.7 stored "enabled" instead, which is ignored now (every game
/// got an entry with enabled false as soon as it ran, so false never meant the user switched it off).</summary>
internal sealed class RelayGame
{
    public string Name { get; set; } = "";
    public bool Excluded { get; set; }
    [JsonIgnore] public bool Enabled => !Excluded;
    public List<string> Ranges { get; set; } = [];
    public List<string> Learned { get; set; } = [];
}

/// <summary>An IPv4 range like 66.40.178.0/24, compared against addresses in network byte order.</summary>
internal readonly record struct Cidr(uint Network, uint Mask)
{
    public static bool TryParse(string text, out Cidr cidr)
    {
        cidr = default;
        var parts = text.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out var bits) || bits is < 0 or > 32) return false;
        var host = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(ip.GetAddressBytes()));
        var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
        cidr = new Cidr(host & mask, mask);
        return true;
    }

    public bool Contains(uint networkOrder)
    {
        var host = (uint)IPAddress.NetworkToHostOrder((int)networkOrder);
        return (host & Mask) == Network;
    }
}

/// <summary>
/// Established IPv4 TCP connections with their owning process (GetExtendedTcpTable), and Windows' own round-trip time
/// for one of them (GetPerTcpConnectionEStats, FineRtt). Neither needs admin rights: Windows already collects FineRtt
/// for every connection, so reading it works for any user. The RTT is the game's own connection, updated whenever it
/// carries data.
/// </summary>
internal static class TcpTable
{
    public readonly record struct Row(int Pid, uint Remote, int RemotePort)
    {
        internal uint RawLocalAddr { get; init; }
        internal uint RawLocalPort { get; init; }
        internal uint RawRemotePort { get; init; }
    }

    private const int AfInet = 2, TcpTableOwnerPidConnections = 4, StateEstablished = 5, EstatsFineRtt = 8;

    /// <summary>The connection's smoothed RTT in milliseconds, or null when Windows has no sample for it (yet).</summary>
    public static unsafe double? Rtt(Row row)
    {
        var r = new MibTcpRow { State = StateEstablished, LocalAddr = row.RawLocalAddr, LocalPort = row.RawLocalPort, RemoteAddr = row.Remote, RemotePort = row.RawRemotePort };
        byte enabled = 0;
        FineRttRod rod = default;
        var error = GetPerTcpConnectionEStats(ref r, EstatsFineRtt, (IntPtr)(&enabled), 0, 1, IntPtr.Zero, 0, 0, (IntPtr)(&rod), 0, (uint)sizeof(FineRttRod));
        if (error != 0 || enabled == 0 || rod.MaxRtt == 0) return null;
        // SumRtt reads as the current smoothed RTT on Windows 11 (it moves with the connection, it doesn't grow), in µs.
        return rod.SumRtt / 1000.0;
    }

    public static List<Row> Established()
    {
        var rows = new List<Row>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections, 0) != 0) continue; // grew: retry with the new size
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (var i = 0; i < count; i++)
                {
                    var r = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buffer + 4 + i * rowSize);
                    if (r.State == StateEstablished)
                        rows.Add(new Row((int)r.Pid, r.RemoteAddr, (int)(((r.RemotePort & 0xFF) << 8) | ((r.RemotePort >> 8) & 0xFF)))
                        {
                            RawLocalAddr = r.LocalAddr, RawLocalPort = r.LocalPort, RawRemotePort = r.RemotePort,
                        });
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return rows;
    }

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

/// <summary>
/// Round-trip times for the relay's ping comparison. Giving a source address pins the probe to that address's
/// network adapter (Windows sends from the strong-host model), which is how "direct" avoids the tunnel's routes.
/// </summary>
internal static class Probe
{
    /// <summary>The address Windows sends ordinary internet traffic from (the default route's adapter).</summary>
    public static IPAddress? DefaultSource()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53)); // no packet is sent: this only picks the route
            return (s.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException) { return null; }
    }

    /// <summary>
    /// The source address Windows would use for traffic to this target (a connected UDP socket's local address; no packet
    /// is sent): the source to give Icmp. Left to choose by itself, IcmpSendEcho2Ex in the installed tray app got no
    /// answers from anything reached over Wi-Fi (Tarkov's raid server, Oracle's Phoenix host) while the same calls with
    /// an adapter's address as the source worked in the same process (0.4.1-beta.16). GetBestInterface isn't used: it
    /// ignores interface metrics and picked the tethered phone (metric 9000) for Phoenix, 360 ms over mobile data.
    /// </summary>
    public static IPAddress? RouteSource(IPAddress target)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(target, 9));
            return (s.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException) { return null; }
    }

    /// <summary>ICMP from the routed adapter's address (RouteSource), the best of a few tries.</summary>
    public static int? IcmpRouted(IPAddress target, int tries = 1, uint timeoutMs = 900) => Icmp(target, RouteSource(target), tries, timeoutMs);

    /// <summary>ICMP first, then the best of 3 TCP handshakes to the port. Milliseconds, or null.</summary>
    public static async Task<int?> TimeAsync(IPAddress target, int port, IPAddress? source)
    {
        if (await Task.Run(() => Icmp(target, source)) is { } ms) return ms;
        int? best = null;
        for (var i = 0; i < 3; i++)
            if (await TcpHandshakeAsync(target, port, source) is { } t && (best is null || t < best)) best = t;
        return best;
    }

    /// <summary>The faster of two TCP handshakes in milliseconds, or null, waiting for them (for the relay's ping timer
    /// thread). One alone can read high: the first handshake to an address is slow (AION 2's Phoenix target: 463 ms vs ~180).</summary>
    public static int? TcpHandshake(IPAddress target, int port)
    {
        int? best = null;
        for (var i = 0; i < 2; i++)
            if (TcpHandshakeAsync(target, port, source: null).GetAwaiter().GetResult() is { } t && (best is null || t < best)) best = t;
        return best;
    }

    private static async Task<int?> TcpHandshakeAsync(IPAddress target, int port, IPAddress? source)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (source is not null) socket.Bind(new IPEndPoint(source, 0));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var watch = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(target, port), timeout.Token);
            return (int)Math.Round(watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return null; }
    }

    /// <summary>ICMP echo (IcmpSendEcho2Ex), the best of a few tries, from the given source address or the routed one.</summary>
    public static unsafe int? Icmp(IPAddress target, IPAddress? source, int tries = 3, uint timeoutMs = 1500)
    {
        var handle = IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
        try
        {
            var dest = BitConverter.ToUInt32(target.GetAddressBytes());
            var src = source is null ? 0u : BitConverter.ToUInt32(source.GetAddressBytes());
            var data = stackalloc byte[32];
            var reply = stackalloc byte[256];
            int? best = null;
            for (var i = 0; i < tries; i++)
            {
                if (IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, src, dest, data, 32, IntPtr.Zero, reply, 256, timeoutMs) == 0) continue;
                // ICMP_ECHO_REPLY: Address, Status, RoundTripTime, ... A reply from a router on the way (TTL expired,
                // unreachable) isn't the target answering.
                if (*(uint*)(reply + 4) != 0 || *(uint*)reply != dest) continue;
                var ms = (int)*(uint*)(reply + 8);
                if (best is null || ms < best) best = ms;
            }
            return best;
        }
        finally { IcmpCloseHandle(handle); }
    }

    /// <summary>One echo to the target that may only cross ttl routers (tracert style): who answered (the router where it
    /// expired, or the target itself) and the round trip; From null when nothing answered.</summary>
    public static unsafe (IPAddress? From, int? Ms, bool Target) IcmpTtl(IPAddress target, IPAddress? source, byte ttl, uint timeoutMs = 700)
    {
        var handle = IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return (null, null, false);
        try
        {
            var dest = BitConverter.ToUInt32(target.GetAddressBytes());
            var src = source is null ? 0u : BitConverter.ToUInt32(source.GetAddressBytes());
            var data = stackalloc byte[32];
            var reply = stackalloc byte[256];
            new Span<byte>(reply, 256).Clear();
            // IP_OPTION_INFORMATION (x64): Ttl, Tos, Flags, OptionsSize, then the options pointer.
            var options = stackalloc byte[16];
            new Span<byte>(options, 16).Clear();
            options[0] = ttl;
            IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, src, dest, data, 32, (IntPtr)options, reply, 256, timeoutMs);
            uint address = *(uint*)reply, status = *(uint*)(reply + 4);
            var ms = (int)*(uint*)(reply + 8);
            if (status == 0 && address == dest) return (target, ms, true);
            if (status == 11013 /* IP_TTL_EXPIRED_TRANSIT */ && address != 0) return (new IPAddress(address), ms, false);
            return (null, null, false);
        }
        finally { IcmpCloseHandle(handle); }
    }

    [DllImport("iphlpapi.dll")]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll")]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll")]
    private static extern unsafe uint IcmpSendEcho2Ex(IntPtr handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext, uint source,
        uint destination, byte* requestData, ushort requestSize, IntPtr requestOptions, byte* replyBuffer, uint replySize, uint timeout);
}

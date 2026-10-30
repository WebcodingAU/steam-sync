using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// What goes through it: for each game switched on in the tray's Game relay menu, its ranges. Ranges are learned:
/// while a game runs, every public IPv4 server its own processes hold a TCP connection to for a minute or more (game
/// sessions, not short web requests) adds that address's /24. Ranges can also be typed into relay.json by hand
/// (UDP-only games, whose peers can't be seen without admin rights). Changes are applied only while no game is
/// running: moving a live connection between the direct path and the relay changes the address the server sees,
/// which drops it.
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
    private List<string>? _applied;          // the ranges last sent with "up"; null = down or not known yet
    private DateTimeOffset _upSince, _lastHealth = DateTimeOffset.MinValue, _downUntil = DateTimeOffset.MinValue;
    private int _ticking;

    /// <summary>One line for the tray: what the relay is doing.</summary>
    public string Status { get; private set; } = "Relay: starting";

    /// <summary>A change made while a game runs (switched on or off, ranges learned) that waits for it to close.</summary>
    public bool Pending { get; private set; }

    /// <summary>The installed helper's version, from its last status reply.</summary>
    public string? HelperVersion { get; private set; }

    public GameRelay(PlayTracker play)
    {
        _play = play;
        _settings = LoadSettings();
        _timer = new(_ => _ = TickAsync());
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
        if (IsSetUp) _timer.Change(TimeSpan.FromSeconds(3), Tick);
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

    private bool Enabled(string key) => _settings.Games.TryGetValue(key, out var g) && g.Enabled;
    private int RangeCount(string key) => _settings.Games.TryGetValue(key, out var g) ? g.Ranges.Union(g.Learned).Count() : 0;

    public void SetEnabled(string key, string name, bool enabled)
    {
        lock (_gate)
        {
            Game(key, name).Enabled = enabled;
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
            if (running.Count == 0) await ApplyAsync();
            Pending = running.Count > 0 && !DesiredRanges().SequenceEqual(_applied ?? []);
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
                if (!byPid.TryGetValue(row.Pid, out var game) || !IsPublic(row.Remote)) continue;
                var key = (row.Pid, row.Remote, row.RemotePort);
                seen.Add(key);
                if (!_firstSeen.TryGetValue(key, out var first)) _firstSeen[key] = first = now;
                if (now - first < LearnAfter) continue;
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
        }
        foreach (var gone in _firstSeen.Keys.Where(k => !seen.Contains(k)).ToList()) _firstSeen.Remove(gone);
        if (changed) lock (_gate) SaveSettings();
    }

    /// <summary>The switched-on games' ranges, sorted.</summary>
    private List<string> DesiredRanges()
    {
        lock (_gate)
            return _settings.Games.Values.Where(g => g.Enabled).SelectMany(g => g.Ranges.Union(g.Learned))
                .Where(r => Cidr.TryParse(r, out _)).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
    }

    /// <summary>Makes the helper route exactly the switched-on games' ranges, or takes the tunnel down when no game
    /// is switched on. Only called while no game runs.</summary>
    private async Task ApplyAsync()
    {
        var ranges = DesiredRanges();
        if (ranges.Count == 0)
        {
            if (_applied is not null || Status.StartsWith("Relay: starting"))
            {
                var down = await RelayClient.SendAsync(new JsonObject { ["cmd"] = "down" });
                if (down is not null) { _applied = null; Remember(down); }
            }
            SetStatus("Relay: off (no game switched on)");
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
                    : gameRunning ? "Relay: off until your game closes" : Status);
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
        lock (_gate) games = string.Join(", ", _settings.Games.Values.Where(g => g.Enabled).Select(g => g.Name));
        if (fresh || DateTimeOffset.UtcNow - _upSince < StaleHandshake)
        {
            var relay = (string?)reply["relay"];
            var rtt = relay is null ? null : await Task.Run(() => Probe.Icmp(IPAddress.Parse(relay), source: null));
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

    private void Remember(JsonObject reply) => HelperVersion = (string?)reply["version"] ?? HelperVersion;

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
            if (File.Exists(SettingsFile)) return JsonSerializer.Deserialize<RelaySettings>(File.ReadAllText(SettingsFile), Json) ?? new();
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

    public void Dispose() => _timer.Dispose();

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
}

/// <summary>One game's relay settings in relay.json: switched on or not, ranges typed in by hand, ranges learned.</summary>
internal sealed class RelayGame
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
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

/// <summary>Established IPv4 TCP connections with their owning process (GetExtendedTcpTable; no admin needed).</summary>
internal static class TcpTable
{
    public readonly record struct Row(int Pid, uint Remote, int RemotePort);

    private const int AfInet = 2, TcpTableOwnerPidConnections = 4, StateEstablished = 5;

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
                        rows.Add(new Row((int)r.Pid, r.RemoteAddr, (int)(((r.RemotePort & 0xFF) << 8) | ((r.RemotePort >> 8) & 0xFF))));
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return rows;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, Pid; }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);
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

    /// <summary>ICMP first, then the best of 3 TCP handshakes to the port. Milliseconds, or null.</summary>
    public static async Task<int?> TimeAsync(IPAddress target, int port, IPAddress? source)
    {
        if (await Task.Run(() => Icmp(target, source)) is { } ms) return ms;
        int? best = null;
        for (var i = 0; i < 3; i++)
            if (await TcpHandshakeAsync(target, port, source) is { } t && (best is null || t < best)) best = t;
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

    /// <summary>One ICMP echo (IcmpSendEcho2Ex, best of 3), from the given source address or the routed one.</summary>
    public static unsafe int? Icmp(IPAddress target, IPAddress? source)
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
            for (var i = 0; i < 3; i++)
            {
                if (IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, src, dest, data, 32, IntPtr.Zero, reply, 256, 1500) == 0) continue;
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

    [DllImport("iphlpapi.dll")]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll")]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll")]
    private static extern unsafe uint IcmpSendEcho2Ex(IntPtr handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext, uint source,
        uint destination, byte* requestData, ushort requestSize, IntPtr requestOptions, byte* replyBuffer, uint replySize, uint timeout);
}

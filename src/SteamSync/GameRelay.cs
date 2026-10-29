using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamSync;

/// <summary>
/// Game-only routing through your own WireGuard relay (a VPS you run), like ExitLag but with one route you control:
/// only the game servers' addresses go through the tunnel, everything else stays on the normal connection.
/// <para>
/// The tunnel itself is WireGuard for Windows (signed, maintained, its own driver), as a tunnel service named
/// "ge-relay" whose config lives in %ProgramData%\GamingEnjoyer\relay. A one-off elevated setup (--relay-setup, see
/// Setup) installs that service and lets this Windows user start and stop it and rewrite its config, so the tray app
/// never needs admin rights afterwards.
/// </para>
/// <para>
/// What goes through it is the config's AllowedIPs: the relay's own tunnel address plus, for each game switched on
/// in the tray's Game relay menu, its ranges. Ranges are learned: while a game runs, every public IPv4 server its own
/// processes hold a TCP connection to for a minute or more (game sessions, not short web requests) adds that
/// address's /24. Ranges can also be typed into relay.json by hand (UDP-only games, whose peers can't be seen without
/// admin rights). Changes are applied only while no game is running: moving a live connection between the direct
/// path and the relay changes the address the server sees, which drops it.
/// </para>
/// <para>
/// While the tunnel is up the relay is pinged every 30 s (its tunnel address, through the tunnel); after 3 misses
/// with no game running the tunnel is stopped, so nothing is sent into a dead route, and tried again 5 minutes later.
/// </para>
/// </summary>
internal sealed class GameRelay : IDisposable
{
    public const string TunnelName = "ge-relay";
    private const string ServiceName = "WireGuardTunnel$" + TunnelName;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LearnAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HealthEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDownAfter = TimeSpan.FromMinutes(5);
    private const int MaxRangesPerGame = 64;

    public static string ConfigDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GamingEnjoyer", "relay");
    public static string TunnelConfig { get; } = Path.Combine(ConfigDir, TunnelName + ".conf");
    public static string WireGuardExe { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe");
    public static string SettingsFile { get; } = Path.Combine(AppInfo.DataDir, "relay.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly PlayTracker _play;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private RelaySettings _settings;
    private readonly Dictionary<(int Pid, uint Remote, int Port), DateTimeOffset> _firstSeen = [];
    private int _ticking;
    private DateTimeOffset _lastHealth = DateTimeOffset.MinValue, _downUntil = DateTimeOffset.MinValue;
    private int _misses;

    /// <summary>One line for the tray: what the relay is doing.</summary>
    public string Status { get; private set; } = "Relay: starting";
    public event Action? Changed;

    public GameRelay(PlayTracker play)
    {
        _play = play;
        _settings = LoadSettings();
        _timer = new(_ => _ = TickAsync());
    }

    /// <summary>The tunnel service is installed (setup has been run on this PC).</summary>
    public static bool IsSetUp => File.Exists(TunnelConfig) && Service() is { } s && Dispose(s);

    /// <summary>WireGuard for Windows is installed but the relay isn't set up yet: the tray offers the setup.</summary>
    public static bool CanSetUp => File.Exists(WireGuardExe) && !IsSetUp;

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
            var game = Game(key, name);
            game.Enabled = enabled;
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

    /// <summary>A change made while a game runs (switched on or off, ranges learned) that waits for it to close.</summary>
    public bool Pending { get; private set; }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var running = _play.Running;
            Learn(running);
            if (running.Count == 0) await ApplyAsync();
            Pending = running.Count > 0 && !DesiredMatchesConfig();
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

    /// <summary>Makes the tunnel carry exactly the switched-on games' ranges: rewrites AllowedIPs and restarts the
    /// service when they changed, stops it when no game is switched on. Only called while no game runs.</summary>
    private async Task ApplyAsync()
    {
        var ranges = DesiredRanges();
        using var service = Service();
        if (service is null) { SetStatus("Relay: not set up (run the setup again)"); return; }

        if (ranges.Count == 0)
        {
            if (service.Status != ServiceControllerStatus.Stopped) await StopAsync(service, "no game is switched on");
            SetStatus("Relay: off (no game switched on)");
            return;
        }
        if (DateTimeOffset.UtcNow < _downUntil) return; // the relay didn't answer: leave the tunnel down for now

        var config = File.ReadAllText(TunnelConfig);
        var relay = RelayAddress(config);
        var allowed = string.Join(", ", (relay is null ? Array.Empty<string>() : [relay + "/32"]).Concat(ranges));
        var updated = Regex.Replace(config, @"(?m)^(AllowedIPs\s*=).*$", $"$1 {allowed}");
        var restart = updated != config || service.Status != ServiceControllerStatus.Running;
        if (updated != config)
        {
            File.WriteAllText(TunnelConfig, updated);
            Log.Info($"Relay: {ranges.Count} range(s) now go through the tunnel");
        }
        if (!restart) return;

        await Task.Run(() =>
        {
            service.Refresh();
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        });
        _misses = 0;
        _lastHealth = DateTimeOffset.MinValue; // check the relay straight away
        Log.Info("Relay: tunnel started");
    }

    /// <summary>The switched-on games' ranges, sorted, as AllowedIPs lists them after the relay's own address.</summary>
    private List<string> DesiredRanges()
    {
        lock (_gate)
            return _settings.Games.Values.Where(g => g.Enabled).SelectMany(g => g.Ranges.Union(g.Learned))
                .Where(r => Cidr.TryParse(r, out _)).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
    }

    private bool DesiredMatchesConfig()
    {
        try
        {
            var carried = AllowedRanges(File.ReadAllText(TunnelConfig)).Skip(1); // the first is the relay's own address
            var desired = DesiredRanges();
            using var service = Service();
            var running = service?.Status == ServiceControllerStatus.Running;
            return desired.Count == 0 ? !running : running && carried.SequenceEqual(desired);
        }
        catch (IOException) { return true; }
    }

    private async Task StopAsync(ServiceController service, string why)
    {
        await Task.Run(() =>
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        });
        Log.Info($"Relay: tunnel stopped ({why})");
    }

    private async Task CheckHealthAsync(bool gameRunning)
    {
        using var service = Service();
        if (service is null || service.Status != ServiceControllerStatus.Running)
        {
            SetStatus(DesiredRanges().Count == 0 ? "Relay: off (no game switched on)"
                : DateTimeOffset.UtcNow < _downUntil ? "Relay: the relay isn't answering; trying again in a few minutes"
                : "Relay: off until your game closes");
            return;
        }
        if (DateTimeOffset.UtcNow - _lastHealth < HealthEvery) return;
        _lastHealth = DateTimeOffset.UtcNow;

        var relay = RelayAddress(File.ReadAllText(TunnelConfig));
        var rtt = relay is null ? null : await Task.Run(() => Probe.Icmp(IPAddress.Parse(relay), source: null));
        string games;
        lock (_gate) games = string.Join(", ", _settings.Games.Values.Where(g => g.Enabled).Select(g => g.Name));
        if (rtt is { } ms)
        {
            _misses = 0;
            SetStatus($"Relay: on for {games} · {ms} ms to the relay");
            return;
        }
        if (++_misses < 3) return;
        SetStatus("Relay: the relay isn't answering");
        if (!gameRunning)
        {
            _downUntil = DateTimeOffset.UtcNow + RetryDownAfter;
            await StopAsync(service, "the relay didn't answer 3 pings; trying again in 5 minutes");
        }
    }

    /// <summary>
    /// Ping to the running game's servers through the relay and directly, for the tray's "Compare ping". Through the
    /// relay only works for addresses the tunnel already carries; the direct figure is forced onto the normal
    /// connection by sending from its own address. ICMP first; servers that drop it are timed by a TCP handshake to
    /// the game's own port (best of 3).
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

        string[] carried;
        try { carried = AllowedRanges(File.ReadAllText(TunnelConfig)); }
        catch (IOException) { carried = []; }
        var direct = Probe.DefaultSource();
        using var service = Service();
        var tunnelUp = service?.Status == ServiceControllerStatus.Running;

        var lines = new List<string> { game.Name };
        foreach (var s in servers)
        {
            var address = new IPAddress(s.Remote);
            var viaRelay = tunnelUp && carried.Any(c => Cidr.TryParse(c, out var cidr) && cidr.Contains(s.Remote));
            var relayMs = viaRelay ? await Probe.TimeAsync(address, s.RemotePort, source: null) : null;
            var directMs = await Probe.TimeAsync(address, s.RemotePort, direct);
            lines.Add($"{address}:{s.RemotePort}  relay {(relayMs is { } r ? $"{r} ms" : viaRelay ? "no reply" : "not routed")}  ·  direct {(directMs is { } d ? $"{d} ms" : "no reply")}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        Changed?.Invoke();
    }

    // ---- config helpers ----

    /// <summary>The relay's tunnel address: the first host of the [Interface] Address (10.66.66.2/32 → 10.66.66.1).</summary>
    private static string? RelayAddress(string config)
    {
        var m = Regex.Match(config, @"(?m)^Address\s*=\s*(\d+)\.(\d+)\.(\d+)\.(\d+)");
        return m.Success ? $"{m.Groups[1]}.{m.Groups[2]}.{m.Groups[3]}.1" : null;
    }

    private static string[] AllowedRanges(string config)
    {
        var m = Regex.Match(config, @"(?m)^AllowedIPs\s*=\s*(.*)$");
        return m.Success ? m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
    }

    private static ServiceController? Service()
    {
        var service = new ServiceController(ServiceName);
        try
        {
            _ = service.Status; // throws when the service doesn't exist
            return service;
        }
        catch (InvalidOperationException)
        {
            service.Dispose();
            return null;
        }
    }

    private static bool Dispose(IDisposable d)
    {
        d.Dispose();
        return true;
    }

    /// <summary>Public unicast IPv4 (network byte order as the TCP table gives it): not private, loopback,
    /// link-local, CGNAT, multicast or the relay's own tunnel range.</summary>
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

    // ---- setup (elevated) ----

    /// <summary>
    /// SteamSync.exe --relay-setup &lt;client.conf&gt;, run elevated: copies the relay's client config to
    /// %ProgramData%\GamingEnjoyer\relay (readable only by SYSTEM, Administrators and this user, since it holds the
    /// private key), installs it as WireGuard's tunnel service "ge-relay" set to start on demand, and lets this
    /// user start, stop and query the service. Returns null on success, else what went wrong.
    /// </summary>
    public static string? Setup(string clientConfig, string userSid)
    {
        if (!File.Exists(WireGuardExe)) return "Install WireGuard for Windows first (wireguard.com/install), then run the setup again.";
        if (!File.Exists(clientConfig)) return $"{clientConfig} doesn't exist.";
        var text = File.ReadAllText(clientConfig);
        if (!Regex.IsMatch(text, @"(?m)^PrivateKey\s*=") || !Regex.IsMatch(text, @"(?m)^Endpoint\s*=") || !Regex.IsMatch(text, @"(?m)^AllowedIPs\s*="))
            return "That file isn't a WireGuard client config (it needs PrivateKey, Endpoint and AllowedIPs).";
        if (Regex.IsMatch(text, @"(?m)^AllowedIPs\s*=.*\b0\.0\.0\.0/0"))
            return "The config routes everything (0.0.0.0/0). The game relay only routes game servers: set AllowedIPs to the relay's tunnel address.";

        var user = new SecurityIdentifier(userSid);
        Directory.CreateDirectory(ConfigDir);
        using (var existing = Service())
        {
            if (existing is not null && Run(WireGuardExe, $"/uninstalltunnelservice {TunnelName}") is { } removeError)
                return $"Couldn't remove the old tunnel: {removeError}";
        }
        File.WriteAllText(TunnelConfig, text);
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify, AccessControlType.Allow));
        new FileInfo(TunnelConfig).SetAccessControl(acl);

        if (Run(WireGuardExe, $"/installtunnelservice \"{TunnelConfig}\"") is { } installError) return $"WireGuard couldn't install the tunnel: {installError}";
        for (var i = 0; i < 20 && Service() is not { } _; i++) Thread.Sleep(250);
        if (Run("sc.exe", $"config {ServiceName} start= demand") is { } startError) return $"Couldn't set the tunnel to start on demand: {startError}";

        // Let this user start, stop and query the tunnel service: add an ACE to its DACL (before the SACL, if any).
        var sddl = RunOutput("sc.exe", $"sdshow {ServiceName}")?.Trim();
        if (string.IsNullOrEmpty(sddl) || !sddl.StartsWith("D:")) return "Couldn't read the tunnel service's permissions.";
        var ace = $"(A;;CCLCSWRPWPDTLOCRRC;;;{user.Value})";
        if (!sddl.Contains(ace))
        {
            var at = sddl.IndexOf("S:", StringComparison.Ordinal);
            sddl = at < 0 ? sddl + ace : sddl[..at] + ace + sddl[at..];
            if (Run("sc.exe", $"sdset {ServiceName} {sddl}") is { } aclError) return $"Couldn't let this user control the tunnel: {aclError}";
        }

        // Installed tunnels start straight away; the tray app starts it again once a game is switched on.
        using (var service = Service())
        {
            if (service?.Status == ServiceControllerStatus.Running)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        }
        return null;
    }

    /// <summary>Starts the setup elevated (a UAC prompt) for this user and waits for it. Null on success.</summary>
    public static async Task<string?> RunSetupElevatedAsync(string clientConfig)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var result = Path.Combine(Path.GetTempPath(), $"ge-relay-setup-{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--relay-setup \"{clientConfig}\" {sid} \"{result}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null) return "The setup didn't start.";
            await process.WaitForExitAsync();
            return File.Exists(result) ? File.ReadAllText(result) is { Length: > 0 } problem ? problem : null : "The setup didn't finish.";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ""; // UAC prompt declined: nothing to say
        }
        finally
        {
            try { File.Delete(result); } catch (IOException) { }
        }
    }

    private static string? Run(string exe, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? null : (output.Trim() is { Length: > 0 } o ? o : $"exit code {p.ExitCode}");
    }

    private static string? RunOutput(string exe, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : null;
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

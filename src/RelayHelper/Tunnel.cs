using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RelayHelper;

/// <summary>
/// The relay's WireGuard client config, read from %ProgramData%\GamingEnjoyer\relay\relay.conf (the usual
/// wg-quick format). Its AllowedIPs only names the relay's own tunnel address: which game ranges go through is
/// decided by the tray app at run time (Tunnel.Up). An extra [GamingEnjoyer] section, which only this helper reads,
/// turns on packet duplication (Duplicator): DuplicateEndpoint (the relay's ge-relay-dup front-end) and
/// DuplicateToken (16 bytes, hex).
/// </summary>
internal sealed record RelayConfig(byte[] PrivateKey, IPAddress Address, int Mtu, byte[] PeerPublicKey, byte[]? PresharedKey,
    IPEndPoint Endpoint, string EndpointText, ushort Keepalive, IPAddress RelayAddress,
    IPEndPoint? DuplicateEndpoint = null, byte[]? DuplicateToken = null)
{
    public static RelayConfig Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[')) { section = line.Trim('[', ']').ToLowerInvariant(); continue; }
            var eq = line.IndexOf('=');
            if (eq > 0) values.TryAdd($"{section}.{line[..eq].Trim()}", line[(eq + 1)..].Trim());
        }
        string Need(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new FormatException($"The relay config has no {key.Replace('.', ' ')}.");

        var address = Regex.Match(Need("interface.Address"), @"\d+\.\d+\.\d+\.\d+");
        if (!address.Success) throw new FormatException("The relay config's Address needs an IPv4 address.");
        var tunnelAddress = IPAddress.Parse(address.Value);

        var endpointText = Need("peer.Endpoint");
        var colon = endpointText.LastIndexOf(':');
        if (colon <= 0 || !ushort.TryParse(endpointText[(colon + 1)..], out var port)) throw new FormatException("The relay config's Endpoint needs host:port.");
        var host = endpointText[..colon].Trim('[', ']');
        var endpointIp = IPAddress.TryParse(host, out var ip) ? ip
            : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
              ?? throw new FormatException($"Couldn't find {host}.");

        // The relay's tunnel address: the first /32 in AllowedIPs (10.66.66.1/32), else .1 of the client's /24.
        var relay = values.TryGetValue("peer.AllowedIPs", out var allowed)
            ? allowed.Split(',').Select(a => a.Trim()).Where(a => a.EndsWith("/32")).Select(a => IPAddress.TryParse(a[..^3], out var r) ? r : null).FirstOrDefault(r => r is not null)
            : null;
        if (relay is null)
        {
            var b = tunnelAddress.GetAddressBytes();
            relay = new IPAddress([b[0], b[1], b[2], 1]);
        }

        IPEndPoint? duplicateEndpoint = null;
        byte[]? duplicateToken = null;
        if (values.TryGetValue("gamingenjoyer.DuplicateEndpoint", out var dup))
        {
            if (!IPEndPoint.TryParse(dup, out duplicateEndpoint) || duplicateEndpoint.Port == 0 || duplicateEndpoint.AddressFamily != AddressFamily.InterNetwork)
                throw new FormatException("The relay config's DuplicateEndpoint needs an IPv4 address and port.");
            try { duplicateToken = Convert.FromHexString(Need("gamingenjoyer.DuplicateToken")); }
            catch (FormatException) { duplicateToken = null; }
            if (duplicateToken is not { Length: 16 }) throw new FormatException("The relay config's DuplicateToken needs 16 bytes in hex.");
        }

        return new RelayConfig(
            Key(Need("interface.PrivateKey"), "PrivateKey"),
            tunnelAddress,
            values.TryGetValue("interface.MTU", out var mtu) && int.TryParse(mtu, out var m) ? m : 1420,
            Key(Need("peer.PublicKey"), "PublicKey"),
            values.TryGetValue("peer.PresharedKey", out var psk) ? Key(psk, "PresharedKey") : null,
            new IPEndPoint(endpointIp, port),
            endpointText,
            values.TryGetValue("peer.PersistentKeepalive", out var ka) && ushort.TryParse(ka, out var k) ? k : (ushort)25,
            relay,
            duplicateEndpoint,
            duplicateToken);
    }

    private static byte[] Key(string base64, string name)
    {
        try
        {
            var key = Convert.FromBase64String(base64);
            if (key.Length == 32) return key;
        }
        catch (FormatException) { }
        throw new FormatException($"The relay config's {name} isn't a WireGuard key.");
    }
}

/// <summary>
/// The relay's WireGuard adapter, through wireguard-nt (wireguard.dll next to this exe, WireGuard LLC's signed driver
/// and library; the API is its wireguard.h). Created when the service starts and removed when it stops (wireguard-nt
/// removes it by itself if this process dies). It starts down; Up gives the peer exactly the relay's address plus the
/// tray app's game ranges as AllowedIPs and routes those ranges to the adapter, Down takes the routes away again.
/// The adapter's address, MTU and routes are set with netsh by interface index, all "store=active", so nothing of it
/// outlives a reboot.
/// </summary>
internal sealed unsafe class Tunnel : IDisposable
{
    private const string AdapterName = "Gaming Enjoyer relay";
    private const string TunnelType = "GamingEnjoyer";
    // Fixed, so Windows sees the same network adapter every time instead of a new one per start.
    private static readonly Guid AdapterGuid = new("5e0f7c2a-91d4-4f3b-8a6e-3c2b7d9e1f40");
    public const int MaxRanges = 1024;

    private readonly RelayConfig _config;
    private readonly object _gate = new();
    private readonly SortedSet<string> _routes = new(StringComparer.Ordinal);
    private IntPtr _library, _adapter;
    private uint _ifIndex;
    private bool _up;

    // wireguard.h
    private delegate* unmanaged[Stdcall]<char*, char*, Guid*, IntPtr> _create;
    private delegate* unmanaged[Stdcall]<IntPtr, void> _close;
    private delegate* unmanaged[Stdcall]<IntPtr, ulong*, void> _getLuid;
    private delegate* unmanaged[Stdcall]<IntPtr, int, int> _setState;
    private delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, int> _setConfig;
    private delegate* unmanaged[Stdcall]<IntPtr, byte*, uint*, int> _getConfig;

    public Tunnel(RelayConfig config) => _config = config;

    public void Start()
    {
        _library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "wireguard.dll"));
        _create = (delegate* unmanaged[Stdcall]<char*, char*, Guid*, IntPtr>)NativeLibrary.GetExport(_library, "WireGuardCreateAdapter");
        _close = (delegate* unmanaged[Stdcall]<IntPtr, void>)NativeLibrary.GetExport(_library, "WireGuardCloseAdapter");
        _getLuid = (delegate* unmanaged[Stdcall]<IntPtr, ulong*, void>)NativeLibrary.GetExport(_library, "WireGuardGetAdapterLUID");
        _setState = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)NativeLibrary.GetExport(_library, "WireGuardSetAdapterState");
        _setConfig = (delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, int>)NativeLibrary.GetExport(_library, "WireGuardSetConfiguration");
        _getConfig = (delegate* unmanaged[Stdcall]<IntPtr, byte*, uint*, int>)NativeLibrary.GetExport(_library, "WireGuardGetConfiguration");
        var setLogger = (delegate* unmanaged[Stdcall]<delegate* unmanaged[Stdcall]<int, ulong, char*, void>, void>)NativeLibrary.GetExport(_library, "WireGuardSetLogger");
        setLogger(&OnDriverLog);

        var guid = AdapterGuid;
        fixed (char* name = AdapterName)
        fixed (char* type = TunnelType)
            _adapter = _create(name, type, &guid);
        if (_adapter == IntPtr.Zero) throw new InvalidOperationException($"WireGuardCreateAdapter failed ({Marshal.GetLastSystemError()})");

        ulong luid;
        _getLuid(_adapter, &luid);
        if (ConvertInterfaceLuidToIndex(&luid, out _ifIndex) != 0) throw new InvalidOperationException("No interface index for the adapter");

        Configure(interfaceToo: true, [_config.RelayAddress.ToString() + "/32"]);
        // The new interface can take a moment to show up for netsh.
        Netsh(retries: 20, "interface", "ipv4", "add", "address", $"name={_ifIndex}", $"address={_config.Address}/32", "store=active");
        Netsh(retries: 5, "interface", "ipv4", "set", "subinterface", $"{_ifIndex}", $"mtu={_config.Mtu}", "store=active");
        _setState(_adapter, 0); // down until the tray app asks
        Log.Write($"Adapter ready (interface {_ifIndex}), relay {_config.EndpointText}");
    }

    /// <summary>Routes exactly these ranges (plus the relay's own address) through the relay and brings it up.
    /// Returns null, or what went wrong with which ranges.</summary>
    public string? Up(IReadOnlyCollection<string> ranges)
    {
        lock (_gate)
        {
            var desired = new SortedSet<string>(ranges, StringComparer.Ordinal) { _config.RelayAddress + "/32" };
            Configure(interfaceToo: false, desired);
            if (_setState(_adapter, 1) == 0) return $"Couldn't bring the adapter up ({Marshal.GetLastSystemError()})";
            _up = true;
            var problems = new List<string>();
            foreach (var gone in _routes.Where(r => !desired.Contains(r)).ToList())
            {
                Netsh(retries: 0, "interface", "ipv4", "delete", "route", $"prefix={gone}", $"interface={_ifIndex}", "store=active");
                _routes.Remove(gone);
            }
            foreach (var range in desired.Where(r => !_routes.Contains(r)))
            {
                if (Netsh(retries: 2, "interface", "ipv4", "add", "route", $"prefix={range}", $"interface={_ifIndex}", "store=active") is { } error)
                    problems.Add($"{range}: {error}");
                else _routes.Add(range);
            }
            Log.Write($"Up: {_routes.Count} route(s){(problems.Count > 0 ? $", {problems.Count} failed" : "")}");
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }
    }

    public void Down()
    {
        lock (_gate)
        {
            foreach (var route in _routes)
                Netsh(retries: 0, "interface", "ipv4", "delete", "route", $"prefix={route}", $"interface={_ifIndex}", "store=active");
            _routes.Clear();
            if (_adapter != IntPtr.Zero) _setState(_adapter, 0);
            if (_up) Log.Write("Down");
            _up = false;
        }
    }

    /// <summary>Up or down, how many ranges it routes, and the peer's last handshake and traffic.</summary>
    public (bool Up, int Routes, DateTimeOffset? Handshake, ulong Rx, ulong Tx) Status()
    {
        lock (_gate)
        {
            uint size = 4096;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = new byte[size];
                fixed (byte* p = buffer)
                {
                    if (_getConfig(_adapter, p, &size) == 0)
                    {
                        if (Marshal.GetLastSystemError() == 234) continue; // ERROR_MORE_DATA: size now says how much
                        break;
                    }
                    if (*(uint*)(p + InterfacePeersCount) == 0) break;
                    var peer = p + InterfaceSize;
                    var handshake = *(ulong*)(peer + PeerLastHandshake);
                    return (_up, Math.Max(0, _routes.Count - 1),
                        handshake == 0 ? null : DateTimeOffset.FromFileTime((long)handshake),
                        *(ulong*)(peer + PeerRxBytes), *(ulong*)(peer + PeerTxBytes));
                }
            }
            return (_up, Math.Max(0, _routes.Count - 1), null, 0, 0);
        }
    }

    /// <summary>
    /// WIREGUARD_INTERFACE, then one WIREGUARD_PEER, then its WIREGUARD_ALLOWED_IPs (x64 layouts from wireguard.h, all
    /// ALIGNED(8)). The first call sets the private key and the peer; later ones only replace the peer's AllowedIPs, so
    /// the session with the relay carries on.
    /// </summary>
    private void Configure(bool interfaceToo, IReadOnlyCollection<string> allowed)
    {
        var size = InterfaceSize + PeerSize + AllowedIpSize * allowed.Count;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (interfaceToo)
            {
                *(uint*)(p + InterfaceFlags) = InterfaceHasPrivateKey | InterfaceReplacePeers;
                Marshal.Copy(_config.PrivateKey, 0, (IntPtr)(p + InterfacePrivateKey), 32);
            }
            *(uint*)(p + InterfacePeersCount) = 1;

            var peer = p + InterfaceSize;
            var flags = PeerHasPublicKey | PeerReplaceAllowedIps;
            if (interfaceToo)
            {
                flags |= PeerHasEndpoint | PeerHasPersistentKeepalive;
                if (_config.PresharedKey is { } psk)
                {
                    flags |= PeerHasPresharedKey;
                    Marshal.Copy(psk, 0, (IntPtr)(peer + PeerPresharedKey), 32);
                }
                *(ushort*)(peer + PeerKeepalive) = _config.Keepalive;
                // SOCKADDR_IN: family, port (network order), address.
                var endpoint = peer + PeerEndpoint;
                *(ushort*)endpoint = 2; // AF_INET
                BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(endpoint + 2, 2), (ushort)_config.Endpoint.Port);
                _config.Endpoint.Address.GetAddressBytes().CopyTo(new Span<byte>(endpoint + 4, 4));
            }
            else flags |= PeerUpdateOnly;
            *(uint*)(peer + PeerFlags) = flags;
            Marshal.Copy(_config.PeerPublicKey, 0, (IntPtr)(peer + PeerPublicKey), 32);
            *(uint*)(peer + PeerAllowedIpsCount) = (uint)allowed.Count;

            var entry = peer + PeerSize;
            foreach (var range in allowed)
            {
                var slash = range.IndexOf('/');
                IPAddress.Parse(range[..slash]).GetAddressBytes().CopyTo(new Span<byte>(entry, 4));
                *(ushort*)(entry + AllowedIpFamily) = 2; // AF_INET
                *(entry + AllowedIpCidr) = byte.Parse(range[(slash + 1)..]);
                entry += AllowedIpSize;
            }

            if (_setConfig(_adapter, p, (uint)size) == 0)
                throw new InvalidOperationException($"WireGuardSetConfiguration failed ({Marshal.GetLastSystemError()})");
        }
    }

    /// <summary>
    /// Points the peer at another endpoint without touching its keys or AllowedIPs: the relay itself, or the
    /// Duplicator's local socket while duplication is on. The session carries on (WireGuard roams); connections
    /// inside the tunnel don't notice.
    /// </summary>
    public void SetEndpoint(IPEndPoint target)
    {
        lock (_gate)
        {
            var buffer = new byte[InterfaceSize + PeerSize];
            fixed (byte* p = buffer)
            {
                *(uint*)(p + InterfacePeersCount) = 1;
                var peer = p + InterfaceSize;
                *(uint*)(peer + PeerFlags) = PeerHasPublicKey | PeerHasEndpoint | PeerUpdateOnly;
                Marshal.Copy(_config.PeerPublicKey, 0, (IntPtr)(peer + PeerPublicKey), 32);
                var endpoint = peer + PeerEndpoint;
                *(ushort*)endpoint = 2; // AF_INET
                BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(endpoint + 2, 2), (ushort)target.Port);
                target.Address.GetAddressBytes().CopyTo(new Span<byte>(endpoint + 4, 4));
                if (_setConfig(_adapter, p, (uint)buffer.Length) == 0)
                    throw new InvalidOperationException($"WireGuardSetConfiguration (endpoint) failed ({Marshal.GetLastSystemError()})");
            }
            Log.Write($"Endpoint now {target}");
        }
    }

    /// <summary>Runs netsh (from System32), retrying while it fails. Null on success, else its output.</summary>
    internal static string? Netsh(int retries, params string[] args)
    {
        for (var attempt = 0; ; attempt++)
        {
            var code = Program.Run(Path.Combine(Environment.SystemDirectory, "netsh.exe"), args, out var output);
            if (code == 0) return null;
            // Already there (a route left from before a restart of the service) counts as done.
            if (output.Contains("already exists", StringComparison.OrdinalIgnoreCase) || output.Contains("object already exists", StringComparison.OrdinalIgnoreCase)) return null;
            if (attempt >= retries)
            {
                var message = output.Trim();
                Log.Write($"netsh {string.Join(' ', args)} failed: {message}");
                return message.Length > 0 ? message : $"exit code {code}";
            }
            Thread.Sleep(300);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnDriverLog(int level, ulong timestamp, char* message)
    {
        if (level >= 1) Log.Write($"wireguard: {new string(message)}"); // warnings and errors only
    }

    public void Dispose()
    {
        try { Down(); } catch { }
        if (_adapter != IntPtr.Zero) _close(_adapter); // removes the adapter
        _adapter = IntPtr.Zero;
    }

    // ---- wireguard.h layouts (x64) ----

    private const int InterfaceFlags = 0, InterfacePrivateKey = 6, InterfacePeersCount = 72, InterfaceSize = 80;
    private const int PeerFlags = 0, PeerPublicKey = 8, PeerPresharedKey = 40, PeerKeepalive = 72, PeerEndpoint = 76,
        PeerTxBytes = 104, PeerRxBytes = 112, PeerLastHandshake = 120, PeerAllowedIpsCount = 128, PeerSize = 136;
    private const int AllowedIpFamily = 16, AllowedIpCidr = 18, AllowedIpSize = 24;
    private const uint InterfaceHasPrivateKey = 1 << 1, InterfaceReplacePeers = 1 << 3;
    private const uint PeerHasPublicKey = 1 << 0, PeerHasPresharedKey = 1 << 1, PeerHasPersistentKeepalive = 1 << 2,
        PeerHasEndpoint = 1 << 3, PeerReplaceAllowedIps = 1 << 5, PeerUpdateOnly = 1 << 7;

    [DllImport("iphlpapi.dll")]
    private static extern uint ConvertInterfaceLuidToIndex(ulong* luid, out uint index);
}

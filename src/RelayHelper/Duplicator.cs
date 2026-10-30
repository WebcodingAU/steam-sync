using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RelayHelper;

/// <summary>
/// Packet duplication: every WireGuard packet goes to the relay over each internet connection this PC has (Starlink
/// Wi-Fi and a USB-tethered phone, say), so a packet one connection drops still arrives over the other.
/// <para>
/// While it's on, WireGuard's endpoint is this class's local socket (127.0.0.1, Tunnel.SetEndpoint). Each packet from
/// WireGuard gets the 16-byte token in front and is sent to the relay's front-end (ge-relay-dup on the server) from one
/// socket per connection, each pinned to its adapter (bound to the adapter's address plus IP_UNICAST_IF, so it leaves
/// that way even when another adapter has the default route). The front-end sends WireGuard's replies back over every
/// connection; each reply with the right token is passed to WireGuard. Duplicates are no problem: WireGuard drops
/// a packet whose counter it has already seen, in both directions.
/// </para>
/// <para>
/// Connections are found every 3 s: adapters that are up with an IPv4 address and gateway, except virtual ones (VPNs,
/// VMs, this relay's own adapter). A tethered phone (Apple Mobile Device Ethernet, Android's Remote NDIS) gets interface
/// metric 9000 (store=active), so Windows keeps everything else on the main connection and only these copies use
/// mobile data.
/// </para>
/// </summary>
internal sealed class Duplicator : IDisposable
{
    private const int TokenSize = 16;
    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(3);
    private static readonly string[] Virtual = ["virtual", "hyper-v", "vmware", "virtualbox", "tap-", "tap adapter", "wintun", "wireguard",
        "exitlag", "zerotier", "tailscale", "hamachi", "vpn", "loopback", "bluetooth", "npcap", "gaming enjoyer"];
    private static readonly string[] Phone = ["apple mobile device ethernet", "remote ndis", "rndis"];

    private readonly IPEndPoint _relay;
    private readonly byte[] _token;
    private readonly object _gate = new();
    private readonly Dictionary<int, Path> _paths = [];
    private readonly HashSet<int> _metricSet = [];
    private readonly Socket _local;
    private readonly System.Threading.Timer _scanTimer;
    private Path[] _snapshot = [];
    private EndPoint? _wireGuard;               // where wireguard-nt sends from, learned from its first packet
    private volatile bool _stopped;

    private sealed class Path(int ifIndex, string name, IPAddress local, bool phone, Socket socket)
    {
        public int IfIndex { get; } = ifIndex;
        public string Name { get; } = name;
        public IPAddress Local { get; } = local;
        public bool Phone { get; } = phone;
        public Socket Socket { get; } = socket;
        public long Sent, Received, LastReceived; // LastReceived: Environment.TickCount64
        public volatile bool Closed;              // set before the socket is closed, so its thread just ends
    }

    public Duplicator(IPEndPoint relay, byte[] token)
    {
        _relay = relay;
        _token = token;
        _local = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        IgnoreConnectionResets(_local);
        _local.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _scanTimer = new(_ => Scan());
    }

    /// <summary>WireGuard's endpoint while duplication is on.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_local.LocalEndPoint!;

    public void Start()
    {
        new Thread(FromWireGuard) { IsBackground = true, Name = "duplicator: WireGuard", Priority = ThreadPriority.AboveNormal }.Start();
        Scan();
        _scanTimer.Change(ScanEvery, ScanEvery);
        Log.Write($"Duplication on: {string.Join(", ", _snapshot.Select(p => p.Name))}");
    }

    /// <summary>Each connection: its name, whether it's a phone, packets sent and received, seconds since the last reply.</summary>
    public IReadOnlyList<(string Name, bool Phone, long Sent, long Received, long? QuietSeconds)> Paths()
    {
        var now = Environment.TickCount64;
        return _snapshot.Select(p => (p.Name, p.Phone, Interlocked.Read(ref p.Sent), Interlocked.Read(ref p.Received),
            Interlocked.Read(ref p.LastReceived) is var last and > 0 ? (now - last) / 1000 : (long?)null)).ToList();
    }

    /// <summary>WireGuard → every connection, with the token in front.</summary>
    private void FromWireGuard()
    {
        var buffer = new byte[TokenSize + 65536];
        _token.CopyTo(buffer, 0);
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_stopped)
        {
            int n;
            try { n = _local.ReceiveFrom(buffer, TokenSize, buffer.Length - TokenSize, SocketFlags.None, ref from); }
            catch (SocketException)
            {
                if (_stopped) return;
                Thread.Sleep(50);
                continue;
            }
            catch (ObjectDisposedException) { return; }
            if (from is not IPEndPoint { Address: var address } || !IPAddress.IsLoopback(address)) continue;
            _wireGuard = from;
            foreach (var path in _snapshot)
            {
                try
                {
                    path.Socket.SendTo(buffer, 0, TokenSize + n, SocketFlags.None, _relay);
                    Interlocked.Increment(ref path.Sent);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { } // that connection is gone or down
            }
        }
    }

    /// <summary>One connection → WireGuard, for replies from the relay with the right token.</summary>
    private void FromRelay(Path path)
    {
        var buffer = new byte[TokenSize + 65536];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_stopped)
        {
            int n;
            try { n = path.Socket.ReceiveFrom(buffer, ref from); }
            catch (SocketException)
            {
                if (path.Closed || _stopped) return;
                Thread.Sleep(50); // a connection going down: don't spin
                continue;
            }
            catch (ObjectDisposedException) { return; }
            if (n <= TokenSize || !from.Equals(_relay) || !CryptographicOperations.FixedTimeEquals(buffer.AsSpan(0, TokenSize), _token)) continue;
            Interlocked.Increment(ref path.Received);
            Interlocked.Exchange(ref path.LastReceived, Environment.TickCount64);
            if (_wireGuard is { } wg)
            {
                try { _local.SendTo(buffer, TokenSize, n - TokenSize, SocketFlags.None, wg); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            }
        }
    }

    /// <summary>Adds sockets for connections that appeared, closes those for connections that went away.</summary>
    private void Scan()
    {
        lock (_gate)
        {
            if (_stopped) return;
            var found = new Dictionary<int, (string Name, IPAddress Local, bool Phone)>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    var description = $"{ni.Name} {ni.Description}".ToLowerInvariant();
                    if (Virtual.Any(description.Contains)) continue;
                    var props = ni.GetIPProperties();
                    if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                    var local = props.UnicastAddresses.Select(u => u.Address)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."));
                    if (local is null) continue;
                    int index;
                    try { index = props.GetIPv4Properties().Index; }
                    catch (NetworkInformationException) { continue; }
                    found[index] = (ni.Name, local, Phone.Any(description.Contains));
                }
            }
            catch (NetworkInformationException) { return; }

            foreach (var (index, path) in _paths.ToList())
            {
                if (found.TryGetValue(index, out var now) && now.Local.Equals(path.Local)) continue;
                path.Closed = true;
                path.Socket.Dispose(); // its thread ends
                _paths.Remove(index);
                Log.Write($"Duplication: {path.Name} gone");
            }
            foreach (var (index, (name, local, phone)) in found)
            {
                if (_paths.ContainsKey(index)) continue;
                if (phone && _metricSet.Add(index))
                    Tunnel.Netsh(retries: 1, "interface", "ipv4", "set", "interface", $"{index}", "metric=9000", "store=active");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    IgnoreConnectionResets(socket);
                    socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31 /* IP_UNICAST_IF */, IPAddress.HostToNetworkOrder(index));
                    socket.Bind(new IPEndPoint(local, 0));
                }
                catch (SocketException ex)
                {
                    socket.Dispose();
                    Log.Write($"Duplication: can't use {name} ({ex.SocketErrorCode})");
                    continue;
                }
                var path = new Path(index, name, local, phone, socket);
                _paths[index] = path;
                new Thread(() => FromRelay(path)) { IsBackground = true, Name = $"duplicator: {name}", Priority = ThreadPriority.AboveNormal }.Start();
                Log.Write($"Duplication: sending over {name} ({local}{(phone ? ", phone" : "")})");
            }
            _snapshot = _paths.Values.ToArray();
        }
    }

    /// <summary>Without this, an ICMP "port unreachable" for one packet makes the socket's next receive fail.</summary>
    private static void IgnoreConnectionResets(Socket socket) =>
        socket.IOControl(unchecked((int)0x9800000C) /* SIO_UDP_CONNRESET */, [0, 0, 0, 0], null);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            foreach (var path in _paths.Values)
            {
                path.Closed = true;
                path.Socket.Dispose();
            }
            _paths.Clear();
            _snapshot = [];
        }
        _scanTimer.Dispose();
        _local.Dispose();
        Log.Write("Duplication off");
    }
}

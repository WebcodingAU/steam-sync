using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SteamSync;

/// <summary>A cloud region a server sits in, and the public endpoint in that region that LivePing times instead.
/// Code "GLOBAL" (no endpoint) is an AWS address AWS doesn't tie to a region (anycast or accelerator style), so there
/// is nothing in its region to time: Hunger Playtest's servers (54.115.0.0/16) are in such a block.</summary>
internal sealed record CloudRegion(string Provider, string Code, string? Name, string? Endpoint)
{
    public bool IsGlobal => Code == "GLOBAL";
}

/// <summary>
/// For game servers that don't answer ping (AWS blocks ICMP by default, e.g. Hunger Playtest's servers): which cloud
/// region the server is in, and how long a TCP handshake to that region's public API endpoint takes. That endpoint
/// sits in the same region as the server, so the handshake time (one round trip) is a fair estimate of the server's
/// ping. LivePing labels it as an estimate.
/// <para>
/// AWS only for now: its address ranges are public (ip-ranges.json, with a region per prefix). The file is about
/// 3 MB, so it is fetched only the first time a server stops answering ping, and kept in
/// %LocalAppData%\SteamSync\aws-ip-ranges.json for a week.
/// </para>
/// </summary>
internal sealed class CloudRegions : IDisposable
{
    private const string AwsRangesUrl = "https://ip-ranges.amazonaws.com/ip-ranges.json";
    // GLOBAL prefixes are kept: an address only in one is reported as such (CloudRegion.IsGlobal), not as unknown.
    private static readonly string AwsCache = Path.Combine(AppInfo.DataDir, "aws-ip-ranges.json");
    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryLoadAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(2);   // one handshake every 2 s is plenty
    private static readonly TimeSpan ResolveEvery = TimeSpan.FromHours(1);

    // AWS region codes people won't recognise, by city. A region missing here shows by its code.
    private static readonly Dictionary<string, string> AwsNames = new()
    {
        ["us-east-1"] = "N. Virginia", ["us-east-2"] = "Ohio", ["us-west-1"] = "N. California", ["us-west-2"] = "Oregon",
        ["ca-central-1"] = "Montreal", ["ca-west-1"] = "Calgary", ["mx-central-1"] = "Mexico", ["sa-east-1"] = "São Paulo",
        ["eu-west-1"] = "Ireland", ["eu-west-2"] = "London", ["eu-west-3"] = "Paris", ["eu-central-1"] = "Frankfurt",
        ["eu-central-2"] = "Zurich", ["eu-north-1"] = "Stockholm", ["eu-south-1"] = "Milan", ["eu-south-2"] = "Spain",
        ["me-south-1"] = "Bahrain", ["me-central-1"] = "UAE", ["il-central-1"] = "Tel Aviv", ["af-south-1"] = "Cape Town",
        ["ap-south-1"] = "Mumbai", ["ap-south-2"] = "Hyderabad", ["ap-east-1"] = "Hong Kong", ["ap-northeast-1"] = "Tokyo",
        ["ap-northeast-2"] = "Seoul", ["ap-northeast-3"] = "Osaka", ["ap-southeast-1"] = "Singapore", ["ap-southeast-2"] = "Sydney",
        ["ap-southeast-3"] = "Jakarta", ["ap-southeast-4"] = "Melbourne", ["ap-southeast-5"] = "Malaysia", ["ap-southeast-7"] = "Thailand",
        ["cn-north-1"] = "Beijing", ["cn-northwest-1"] = "Ningxia",
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly object _gate = new();
    private List<(uint Net, uint Mask, int Bits, string Region)>? _v4;
    private List<(UInt128 Net, UInt128 Mask, int Bits, string Region)>? _v6;
    private Task? _loading;
    private DateTimeOffset _loadFailedAt = DateTimeOffset.MinValue;
    private readonly Dictionary<IPAddress, CloudRegion?> _found = [];
    private readonly Dictionary<string, (IPAddress Ip, DateTimeOffset At)> _endpoints = [];
    private readonly Dictionary<string, (double? Ms, DateTimeOffset At)> _probes = [];

    /// <summary>The cloud region the address is in, or null: not a known cloud, or the ranges are still loading
    /// (the first call starts loading them).</summary>
    public CloudRegion? Find(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        lock (_gate)
        {
            if (_v4 is null)
            {
                if ((_loading is null || _loading.IsCompleted) && DateTimeOffset.UtcNow - _loadFailedAt > RetryLoadAfter)
                    _loading = Task.Run(LoadAwsAsync);
                return null;
            }
            if (_found.TryGetValue(ip, out var known)) return known;
            var region = AwsRegion(ip);
            var found = region switch
            {
                null => null,
                "GLOBAL" => new CloudRegion("AWS", region, null, null),
                _ => new CloudRegion("AWS", region, AwsNames.GetValueOrDefault(region),
                    region.StartsWith("cn-", StringComparison.Ordinal) ? $"ec2.{region}.amazonaws.com.cn" : $"ec2.{region}.amazonaws.com"),
            };
            _found[ip] = found;
            return found;
        }
    }

    /// <summary>The region endpoint's TCP handshake time in ms (null when it didn't answer in 2 s). Done at most every
    /// 2 seconds per region; calls in between get the last result.</summary>
    public async Task<double?> ProbeAsync(CloudRegion region)
    {
        if (region.Endpoint is null) return null;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_probes.TryGetValue(region.Endpoint, out var last) && now - last.At < ProbeEvery) return last.Ms;
            _probes[region.Endpoint] = (last.Ms, now); // claim this round, so overlapping calls don't probe twice
        }

        double? ms = null;
        try
        {
            // Resolved once an hour, so the handshake time doesn't include a DNS lookup.
            IPAddress? ip;
            lock (_gate) ip = _endpoints.TryGetValue(region.Endpoint, out var e) && now - e.At < ResolveEvery ? e.Ip : null;
            if (ip is null)
            {
                var addresses = await Dns.GetHostAddressesAsync(region.Endpoint);
                ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (ip is null) return null;
                lock (_gate) _endpoints[region.Endpoint] = (ip, now);
            }

            using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var watch = Stopwatch.StartNew();
            await socket.ConnectAsync(ip, 443, timeout.Token);
            ms = watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { }
        lock (_gate) _probes[region.Endpoint] = (ms, now);
        return ms;
    }

    private string? AwsRegion(IPAddress ip)
    {
        // The longest matching prefix; a regional one beats a GLOBAL one of the same length.
        var bytes = ip.GetAddressBytes();
        string? best = null;
        var bestBits = -1;
        bool Better(int bits, string region) => bits > bestBits || (bits == bestBits && best == "GLOBAL" && region != "GLOBAL");
        if (bytes.Length == 4)
        {
            var a = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
            foreach (var (net, mask, bits, region) in _v4!)
                if ((a & mask) == net && Better(bits, region)) (best, bestBits) = (region, bits);
        }
        else
        {
            var a = ToUInt128(bytes);
            foreach (var (net, mask, bits, region) in _v6!)
                if ((a & mask) == net && Better(bits, region)) (best, bestBits) = (region, bits);
        }
        return best;
    }

    private async Task LoadAwsAsync()
    {
        try
        {
            string json;
            if (File.Exists(AwsCache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(AwsCache) < CacheFor)
                json = await File.ReadAllTextAsync(AwsCache);
            else
            {
                json = await _http.GetStringAsync(AwsRangesUrl);
                var tmp = AwsCache + ".tmp";
                await File.WriteAllTextAsync(tmp, json);
                File.Move(tmp, AwsCache, overwrite: true);
                Log.Info("Downloaded AWS's address ranges (for live ping estimates)");
            }

            var v4 = new List<(uint, uint, int, string)>();
            var v6 = new List<(UInt128, UInt128, int, string)>();
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.GetProperty("prefixes").EnumerateArray())
            {
                var region = p.GetProperty("region").GetString();
                if (region is null || !TryParsePrefix(p.GetProperty("ip_prefix").GetString(), out var net, out var bits) || net.Length != 4) continue;
                var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
                v4.Add(((uint)(net[0] << 24 | net[1] << 16 | net[2] << 8 | net[3]) & mask, mask, bits, region));
            }
            foreach (var p in doc.RootElement.GetProperty("ipv6_prefixes").EnumerateArray())
            {
                var region = p.GetProperty("region").GetString();
                if (region is null || !TryParsePrefix(p.GetProperty("ipv6_prefix").GetString(), out var net, out var bits) || net.Length != 16) continue;
                var mask = bits == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - bits);
                v6.Add((ToUInt128(net) & mask, mask, bits, region));
            }
            lock (_gate) (_v4, _v6) = (v4, v6);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load AWS's address ranges; live ping can't estimate AWS servers for now", ex);
            lock (_gate) _loadFailedAt = DateTimeOffset.UtcNow;
        }
    }

    private static bool TryParsePrefix(string? text, out byte[] net, out int bits)
    {
        net = [];
        bits = 0;
        var slash = text?.IndexOf('/') ?? -1;
        if (slash <= 0 || !IPAddress.TryParse(text![..slash], out var ip) || !int.TryParse(text[(slash + 1)..], out bits)) return false;
        net = ip.GetAddressBytes();
        return bits >= 0 && bits <= net.Length * 8;
    }

    private static UInt128 ToUInt128(byte[] b)
    {
        UInt128 v = 0;
        foreach (var x in b) v = v << 8 | x;
        return v;
    }

    public void Dispose() => _http.Dispose();
}

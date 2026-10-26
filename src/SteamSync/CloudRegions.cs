using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SteamSync;

/// <summary>A cloud region a server sits in, and a public endpoint in that region that can be timed (a game profile's
/// target can be one). Code "GLOBAL" (no endpoint) is an address its cloud doesn't tie to a region (anycast or
/// accelerator style): Hunger Playtest's servers (54.115.0.0/16) are in such a block.</summary>
internal sealed record CloudRegion(string Provider, string Code, string? Name, string? Endpoint)
{
    public bool IsGlobal => Code == "GLOBAL";
}

/// <summary>
/// For game servers that don't answer ping (AWS blocks ICMP by default, e.g. Hunger Playtest's servers): which cloud
/// region the server is in, so live ping can say what a game profile for it needs (live ping no longer estimates from
/// the region on its own: real measurements and profiles only), and the time to a region endpoint, which game
/// profiles use as their target: a TCP handshake to an AWS region's API endpoint, or a gcping request (below).
/// <para>
/// AWS: its address ranges are public (ip-ranges.json, with a region per prefix). The file is about 3 MB, so it is
/// fetched only the first time a server stops answering ping, and kept in %LocalAppData%\SteamSync\aws-ip-ranges.json
/// for a week.
/// </para>
/// <para>
/// Google Cloud (ARC Raiders' servers, 34.129.0.0/16 = australia-southeast2): its ranges are public too
/// (gstatic.com/ipranges/cloud.json, a region per prefix, kept as gcp-ip-ranges.json). Google's API hosts are anycast
/// (Google's nearest edge ends the handshake), so a handshake says nothing about the region; instead the endpoint is
/// gcping's Cloud Run service in that region (gcping.com, Google's own region latency tool; its list is kept as
/// gcp-endpoints.json), timed the way gcping does it: a request on an open connection, which goes on to the region.
/// Measured from Sydney: Melbourne 48-54 ms, Sydney 32, Oregon 171-187.
/// </para>
/// </summary>
internal sealed class CloudRegions : IDisposable
{
    private const string AwsRangesUrl = "https://ip-ranges.amazonaws.com/ip-ranges.json";
    // GLOBAL prefixes are kept: an address only in one is reported as such (CloudRegion.IsGlobal), not as unknown.
    private static readonly string AwsCache = Path.Combine(AppInfo.DataDir, "aws-ip-ranges.json");
    private const string GcpRangesUrl = "https://www.gstatic.com/ipranges/cloud.json";
    private static readonly string GcpCache = Path.Combine(AppInfo.DataDir, "gcp-ip-ranges.json");
    private const string GcpEndpointsUrl = "https://global.gcping.com/api/endpoints";
    private static readonly string GcpEndpointsCache = Path.Combine(AppInfo.DataDir, "gcp-endpoints.json");
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
    private readonly HttpClient _pingHttp = new() { Timeout = TimeSpan.FromSeconds(2) };   // keeps gcping connections open between probes
    private readonly object _gate = new();
    private List<(uint Net, uint Mask, int Bits, string Region)>? _v4;
    private List<(UInt128 Net, UInt128 Mask, int Bits, string Region)>? _v6;
    private List<(uint Net, uint Mask, int Bits, string Region)> _gcp4 = [];
    private List<(UInt128 Net, UInt128 Mask, int Bits, string Region)> _gcp6 = [];
    private Dictionary<string, (string Name, string Url)> _gcpEndpoints = [];
    private readonly HashSet<string> _warmed = [];
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
                    _loading = Task.Run(LoadAsync);
                return null;
            }
            if (_found.TryGetValue(ip, out var known)) return known;
            var region = Longest(ip, _v4, _v6!);
            var gcp = region is null ? Longest(ip, _gcp4, _gcp6) : null;
            var found = (region, gcp) switch
            {
                ("GLOBAL", _) => new CloudRegion("AWS", "GLOBAL", null, null),
                ({ } r, _) => new CloudRegion("AWS", r, AwsNames.GetValueOrDefault(r),
                    r.StartsWith("cn-", StringComparison.Ordinal) ? $"ec2.{r}.amazonaws.com.cn" : $"ec2.{r}.amazonaws.com"),
                (_, "global") => new CloudRegion("Google Cloud", "GLOBAL", null, null),
                (_, { } g) => new CloudRegion("Google Cloud", g, _gcpEndpoints.TryGetValue(g, out var e) ? e.Name : null,
                    _gcpEndpoints.TryGetValue(g, out e) ? e.Url : null),
                _ => null,
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
        if (region.Endpoint.StartsWith("https://", StringComparison.Ordinal))
        {
            // gcping (Google Cloud): a request on the connection kept open from the last one. The first request also
            // makes the connection (DNS, TCP, TLS), so it isn't timed.
            var url = region.Endpoint.TrimEnd('/') + "/api/ping";
            try
            {
                bool warm;
                lock (_gate) warm = _warmed.Contains(url);
                if (!warm)
                {
                    await _pingHttp.GetStringAsync(url);
                    lock (_gate) _warmed.Add(url);
                }
                var watch = Stopwatch.StartNew();
                await _pingHttp.GetStringAsync(url);
                ms = watch.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
            lock (_gate) _probes[region.Endpoint] = (ms, now);
            return ms;
        }
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
                // The first handshake to a new address runs slow (381 ms against a usual 148 in testing): spend it untimed.
                using var warm = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                using var warmTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await warm.ConnectAsync(ip, 443, warmTimeout.Token);
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

    /// <summary>The region of the longest prefix the address is in; a regional one beats a GLOBAL (AWS) or global
    /// (Google) one of the same length.</summary>
    private static string? Longest(IPAddress ip, List<(uint Net, uint Mask, int Bits, string Region)> v4,
        List<(UInt128 Net, UInt128 Mask, int Bits, string Region)> v6)
    {
        var bytes = ip.GetAddressBytes();
        string? best = null;
        var bestBits = -1;
        bool Better(int bits, string region) =>
            bits > bestBits || (bits == bestBits && best is "GLOBAL" or "global" && region is not ("GLOBAL" or "global"));
        if (bytes.Length == 4)
        {
            var a = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
            foreach (var (net, mask, bits, region) in v4)
                if ((a & mask) == net && Better(bits, region)) (best, bestBits) = (region, bits);
        }
        else
        {
            var a = ToUInt128(bytes);
            foreach (var (net, mask, bits, region) in v6)
                if ((a & mask) == net && Better(bits, region)) (best, bestBits) = (region, bits);
        }
        return best;
    }

    /// <summary>A file from the web, kept in the data folder for a week.</summary>
    private async Task<string> CachedAsync(string url, string cache, string what)
    {
        if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < CacheFor)
            return await File.ReadAllTextAsync(cache);
        var json = await _http.GetStringAsync(url);
        var tmp = cache + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, cache, overwrite: true);
        Log.Info($"Downloaded {what} (for live ping)");
        return json;
    }

    private async Task LoadAsync()
    {
        // Google Cloud first and on its own: without it, AWS still works (and the other way round, below).
        try
        {
            var g4 = new List<(uint, uint, int, string)>();
            var g6 = new List<(UInt128, UInt128, int, string)>();
            using (var doc = JsonDocument.Parse(await CachedAsync(GcpRangesUrl, GcpCache, "Google Cloud's address ranges")))
            {
                foreach (var p in doc.RootElement.GetProperty("prefixes").EnumerateArray())
                {
                    if (!p.TryGetProperty("scope", out var scope) || scope.GetString() is not { } region) continue;
                    if (p.TryGetProperty("ipv4Prefix", out var v4p) && TryParsePrefix(v4p.GetString(), out var net, out var bits) && net.Length == 4)
                    {
                        var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
                        g4.Add(((uint)(net[0] << 24 | net[1] << 16 | net[2] << 8 | net[3]) & mask, mask, bits, region));
                    }
                    else if (p.TryGetProperty("ipv6Prefix", out var v6p) && TryParsePrefix(v6p.GetString(), out net, out bits) && net.Length == 16)
                    {
                        var mask = bits == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - bits);
                        g6.Add((ToUInt128(net) & mask, mask, bits, region));
                    }
                }
            }
            // gcping's list: {"australia-southeast2": {"URL": "https://...run.app", "RegionName": "Melbourne", ...}}.
            var endpoints = new Dictionary<string, (string, string)>();
            try
            {
                using var doc = JsonDocument.Parse(await CachedAsync(GcpEndpointsUrl, GcpEndpointsCache, "gcping's region list"));
                foreach (var e in doc.RootElement.EnumerateObject())
                    if (e.Value.TryGetProperty("URL", out var u) && u.GetString() is { } url && url.StartsWith("https://", StringComparison.Ordinal))
                        endpoints[e.Name] = (e.Value.TryGetProperty("RegionName", out var n) ? n.GetString() ?? e.Name : e.Name, url);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or TaskCanceledException)
            {
                Log.Error("Could not load gcping's region list; Google Cloud regions have no endpoint to time for now", ex);
            }
            lock (_gate) (_gcp4, _gcp6, _gcpEndpoints) = (g4, g6, endpoints);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load Google Cloud's address ranges; live ping can't name Google Cloud regions for now", ex);
        }

        try
        {
            var json = await CachedAsync(AwsRangesUrl, AwsCache, "AWS's address ranges");
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
            Log.Error("Could not load AWS's address ranges; live ping can't name AWS regions for now", ex);
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

    public void Dispose()
    {
        _http.Dispose();
        _pingHttp.Dispose();
    }
}

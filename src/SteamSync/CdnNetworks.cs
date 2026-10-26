using System.Net;
using System.Runtime.InteropServices;

namespace SteamSync;

/// <summary>An anycast/CDN network a server address belongs to.</summary>
internal sealed record CdnNetwork(int Asn, string Name);

/// <summary>
/// Whether a game server's address belongs to an anycast/CDN network (Cloudflare and the like). Such an address is
/// answered by the network's nearest edge, not by the server: AION 2's 193.202.112.61 is Cloudflare (AS13335), so
/// ping, and even the game's own TCP RTT (Cloudflare ends TCP at the edge), read 22 ms from Sydney while the server is
/// in the US. LivePing doesn't show those numbers: only a game profile that says where the servers really are
/// (GameProfiles) gives such a server's ping, else it's not_configured.
/// <para>
/// The address's network (ASN) comes from Team Cymru's IP-to-ASN DNS service (a TXT lookup of
/// d.c.b.a.origin.asn.cymru.com), because these CDNs also announce customers' own prefixes (Cloudflare Spectrum's
/// 193.202.112.0/24 isn't in Cloudflare's published ranges). Looked up once per address in the background; until
/// the answer is in, the address counts as not CDN.
/// </para>
/// </summary>
internal sealed class CdnNetworks
{
    // Networks that answer from the nearest edge, by ASN.
    private static readonly Dictionary<int, string> Anycast = new()
    {
        [13335] = "Cloudflare", [209242] = "Cloudflare", [54113] = "Fastly", [20940] = "Akamai", [16625] = "Akamai",
        [19551] = "Imperva",
    };

    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, CdnNetwork?> _found = [];
    private readonly HashSet<IPAddress> _looking = [];

    /// <summary>The CDN the address belongs to, or null: not one, or not looked up yet (the first call starts it).</summary>
    public CdnNetwork? Find(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null; // IPv4 only, like the rest of live ping
        lock (_gate)
        {
            if (_found.TryGetValue(ip, out var known)) return known;
            if (!_looking.Add(ip)) return null;
        }
        _ = Task.Run(() =>
        {
            var found = Asn(ip) is { } asn && Anycast.TryGetValue(asn, out var name) ? new CdnNetwork(asn, name) : null;
            if (found is not null) Log.Info($"Live ping: {ip} is {found.Name} (AS{found.Asn}), an anycast edge");
            lock (_gate)
            {
                _found[ip] = found;
                _looking.Remove(ip);
            }
        });
        return null;
    }

    /// <summary>The address's origin ASN from Team Cymru ("13335 | 193.202.112.0/24 | IE | ripencc | 2019-11-18"), or
    /// null when the lookup failed.</summary>
    private static int? Asn(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        var name = $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.origin.asn.cymru.com";
        foreach (var text in TxtRecords(name))
        {
            // Several origins list several ASNs ("13335 209242 | ..."); the first is enough to tell a CDN.
            var first = text.Split('|')[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (int.TryParse(first, out var asn)) return asn;
        }
        return null;
    }

    private static List<string> TxtRecords(string name)
    {
        var texts = new List<string>();
        if (DnsQuery(name, DnsTypeText, DnsQueryStandard, IntPtr.Zero, out var records, IntPtr.Zero) != 0 || records == IntPtr.Zero)
            return texts;
        try
        {
            for (var r = records; r != IntPtr.Zero; r = Marshal.ReadIntPtr(r))
            {
                // DNS_RECORD: pNext, pName, wType, wDataLength, Flags, dwTtl, dwReserved, then the data (DNS_TXT_DATA:
                // dwStringCount, then the string pointers, pointer-aligned).
                var header = IntPtr.Size * 2;
                if ((ushort)Marshal.ReadInt16(r, header) != DnsTypeText) continue;
                var data = header + 16;
                var count = Marshal.ReadInt32(r, data);
                for (var i = 0; i < count; i++)
                {
                    if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(r, data + IntPtr.Size + i * IntPtr.Size)) is { } s) texts.Add(s);
                }
            }
        }
        finally { DnsRecordListFree(records, 1); }
        return texts;
    }

    private const ushort DnsTypeText = 16;
    private const uint DnsQueryStandard = 0;

    [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode)]
    private static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [DllImport("dnsapi.dll")]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);
}

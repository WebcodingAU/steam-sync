using System.Net;

namespace AdVoid;

/// <summary>Hostname helpers: the registrable domain (for first/third-party) and walking a host's parent domains.</summary>
public static class Domains
{
    // Two-label public suffixes common enough to matter (a full Public Suffix List can replace this later). Without
    // them, every site under co.uk would count as the same "site" and third-party rules would stop applying.
    private static readonly HashSet<string> TwoLabelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "asn.au", "id.au",
        "co.nz", "net.nz", "org.nz", "govt.nz", "ac.nz",
        "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp",
        "com.br", "net.br", "org.br", "com.mx", "com.ar", "com.tr", "com.cn", "net.cn", "org.cn",
        "com.hk", "com.tw", "com.sg", "com.my", "co.in", "net.in", "org.in", "co.za", "co.kr", "or.kr",
        "com.ua", "co.il", "com.pl", "com.ru", "co.id", "com.ph", "com.vn", "com.sa", "com.eg",
        "github.io", "blogspot.com", "appspot.com", "herokuapp.com", "netlify.app", "vercel.app", "pages.dev", "workers.dev",
    };

    /// <summary>example.com for www.example.com, example.co.uk for a.b.example.co.uk; an IP address stays as it is.</summary>
    public static string Registrable(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;
        var last = host.LastIndexOf('.');
        if (last <= 0) return host;
        var second = host.LastIndexOf('.', last - 1);
        if (second < 0) return host;
        if (TwoLabelSuffixes.Contains(host[(second + 1)..]))
        {
            var third = host.LastIndexOf('.', second - 1);
            return third < 0 ? host : host[(third + 1)..];
        }
        return host[(second + 1)..];
    }

    public static bool SameSite(string a, string b) => Registrable(a) == Registrable(b);

    /// <summary>The host itself, then each parent domain: a.b.example.com, b.example.com, example.com, com.</summary>
    public static IEnumerable<string> SelfAndParents(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        for (var at = 0; at >= 0 && at < host.Length; )
        {
            yield return host[at..];
            var dot = host.IndexOf('.', at);
            at = dot < 0 ? -1 : dot + 1;
        }
    }

    /// <summary>Whether host is domain or one of its subdomains.</summary>
    public static bool IsSelfOrSub(string host, string domain) =>
        host.Length == domain.Length ? host == domain
        : host.Length > domain.Length && host.EndsWith(domain, StringComparison.Ordinal) && host[host.Length - domain.Length - 1] == '.';
}

namespace AdVoid;

/// <summary>
/// Element hiding: <c>##.ad-banner</c> (every site), <c>example.com##.sidebar-ad</c> (one site and its subdomains),
/// <c>~example.com##.ad</c> (every site but that one), and <c>example.com#@#.ad</c> (don't hide it there).
/// Procedural (<c>#?#</c>, <c>:has-text()</c>), snippet (<c>#$#</c>) and scriptlet (<c>##+js()</c>) filters are
/// recognised as cosmetic but not applied yet.
/// </summary>
internal sealed record CosmeticFilter(string Selector, string[]? Domains, string[]? NotDomains, bool Exception)
{
    private static readonly string[] Separators = ["#@?#", "#@$#", "#@%#", "#@#", "#?#", "#$#", "#%#", "##"];

    // uBO's procedural operators inside a plain ## filter: not CSS, so a stylesheet would drop the whole rule.
    private static readonly string[] Procedural =
    [
        ":has-text(", ":upward(", ":xpath(", ":-abp-", ":matches-css", ":matches-attr(", ":matches-path(", ":matches-prop(",
        ":min-text-length(", ":watch-attr(", ":others(", ":remove(", ":style(", ":remove-attr(", ":remove-class(", ":shadow(", ":if(", ":if-not(",
    ];

    public static bool IsCosmetic(string line) => Find(line).At >= 0;

    private static (int At, string Sep) Find(string line)
    {
        foreach (var sep in Separators)
        {
            var at = line.IndexOf(sep, StringComparison.Ordinal);
            // The domain part before it never holds '/', so "##" inside a URL filter isn't one.
            if (at >= 0 && line.AsSpan(0, at).IndexOfAny('/', '$', '|') < 0) return (at, sep);
        }
        return (-1, "");
    }

    public static CosmeticFilter? Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] == '!') return null;
        var (at, sep) = Find(line);
        if (at < 0 || sep is not ("##" or "#@#")) return null;
        var selector = line[(at + sep.Length)..].Trim();
        if (selector.Length == 0 || selector.StartsWith("+js(", StringComparison.Ordinal) || selector.StartsWith('^')) return null; // scriptlets, HTML filters
        foreach (var p in Procedural) if (selector.Contains(p, StringComparison.Ordinal)) return null;
        if (selector.Contains('{') || selector.Contains('}')) return null; // would break out of the CSS rule

        string[]? domains = null, notDomains = null;
        if (at > 0)
        {
            var yes = new List<string>(); var no = new List<string>();
            foreach (var d in line[..at].ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (d.Contains('*') || d.StartsWith('/')) return null; // entity (example.*) and regex domains: not yet
                if (d[0] == '~') no.Add(d[1..]); else yes.Add(d);
            }
            domains = yes.Count > 0 ? yes.ToArray() : null;
            notDomains = no.Count > 0 ? no.ToArray() : null;
        }
        return new CosmeticFilter(selector, domains, notDomains, sep == "#@#");
    }
}

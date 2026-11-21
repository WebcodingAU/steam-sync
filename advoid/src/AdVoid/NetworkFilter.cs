using System.Text.RegularExpressions;

namespace AdVoid;

/// <summary>
/// One network filter in Adblock Plus / uBlock Origin syntax: <c>||ads.example.com^$third-party,script</c>,
/// <c>@@||example.com/ads.js$script</c>, <c>/banner\d+\.gif/</c>. Parse returns null for comments, cosmetic filters
/// and filters using options this engine doesn't support yet ($csp, $redirect, $removeparam, ...): skipping a filter
/// is safer than applying half of it.
/// </summary>
internal sealed class NetworkFilter
{
    public required string Text { get; init; }
    public bool Exception { get; init; }
    public bool Important { get; init; }
    public RequestType Types { get; init; } = RequestType.AllButDocument;
    public bool? ThirdParty { get; init; }             // null: either
    public string[]? Domains { get; init; }            // $domain= page domains it's limited to
    public string[]? NotDomains { get; init; }         // $domain=~x pages it doesn't apply on
    public bool MatchCase { get; init; }
    /// <summary>Page-level exceptions (@@...$document, $elemhide, $generichide): they turn off blocking or hiding for a
    /// whole page instead of letting one request through.</summary>
    public PageFlags Page { get; init; }

    // The pattern: '*' any run, '^' a separator or the end; anchors are flags.
    public string Pattern { get; init; } = "";
    public bool HostAnchor { get; init; }              // ||
    public bool StartAnchor { get; init; }             // |
    public bool EndAnchor { get; init; }               // trailing |
    public Regex? Regex { get; init; }                 // /.../

    [Flags]
    public enum PageFlags { None = 0, Document = 1, ElemHide = 2, GenericHide = 4, GenericBlock = 8 }

    /// <summary>A plain <c>||host^</c> with no options: matched by hostname lookup alone (most of EasyPrivacy).</summary>
    public string? PlainHost { get; private set; }

    private static readonly HashSet<string> Unsupported = new(StringComparer.Ordinal)
    {
        "csp", "redirect", "redirect-rule", "removeparam", "queryprune", "rewrite", "replace", "header", "permissions",
        "urltransform", "uritransform", "to", "from", "method", "denyallow", "strict1p", "strict3p", "1p-strict", "3p-strict",
        "popup", "popunder", "cname", "inline-script", "inline-font", "empty", "mp4", "badfilter", "sitekey", "webrtc",
    };

    public static NetworkFilter? Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] == '!' || line[0] == '[' || CosmeticFilter.IsCosmetic(line)) return null;

        var exception = line.StartsWith("@@", StringComparison.Ordinal);
        var body = exception ? line[2..] : line;

        // Options: after the last '$', unless that '$' is inside a /regex/.
        string? options = null;
        var dollar = body.LastIndexOf('$');
        if (dollar >= 0)
        {
            var regexLike = body.Length > 1 && body[0] == '/' && body.LastIndexOf('/') > 0;
            if (!regexLike || dollar > body.LastIndexOf('/'))
            {
                options = body[(dollar + 1)..];
                body = body[..dollar];
            }
        }

        RequestType include = RequestType.None, exclude = RequestType.None;
        bool? thirdParty = null;
        bool important = false, matchCase = false;
        string[]? domains = null, notDomains = null;
        var page = PageFlags.None;
        if (options is not null)
        {
            foreach (var raw in options.Split(','))
            {
                var opt = raw.Trim().ToLowerInvariant();
                if (opt.Length == 0) continue;
                var negated = opt[0] == '~';
                var name = negated ? opt[1..] : opt;
                var eq = name.IndexOf('=');
                var value = eq >= 0 ? raw.Trim()[(raw.Trim().IndexOf('=') + 1)..] : null;
                if (eq >= 0) name = name[..eq];

                if (Unsupported.Contains(name)) return null;
                switch (name)
                {
                    case "third-party" or "3p": thirdParty = !negated; break;
                    case "first-party" or "1p": thirdParty = negated; break;
                    case "important": important = true; break;
                    case "match-case": matchCase = true; break;
                    case "domain" or "from" when value is not null:
                        var yes = new List<string>(); var no = new List<string>();
                        foreach (var d in value.ToLowerInvariant().Split('|', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (d.Contains('*') || d.StartsWith('/')) return null; // entity and regex domains: not yet
                            if (d[0] == '~') no.Add(d[1..]); else yes.Add(d);
                        }
                        domains = yes.Count > 0 ? yes.ToArray() : null;
                        notDomains = no.Count > 0 ? no.ToArray() : null;
                        break;
                    case "document" or "doc": if (exception) page |= PageFlags.Document; else return null; break; // blocking whole pages: not yet
                    case "elemhide" or "ehide": if (exception) page |= PageFlags.ElemHide; else return null; break;
                    case "generichide" or "ghide": if (exception) page |= PageFlags.GenericHide; else return null; break;
                    case "genericblock": if (exception) page |= PageFlags.GenericBlock; else return null; break;
                    case "all": include |= RequestType.AllButDocument; break;
                    default:
                        if (TypeOf(name) is not { } type) return null; // unknown option: skip the filter
                        if (negated) exclude |= type; else include |= type;
                        break;
                }
            }
        }

        // A page-level exception has no request types of its own.
        var types = page != PageFlags.None && include == RequestType.None && exclude == RequestType.None ? RequestType.None
            : (include == RequestType.None ? RequestType.AllButDocument : include) & ~exclude;
        if (types == RequestType.None && page == PageFlags.None) return null;

        // The pattern.
        Regex? regex = null;
        bool hostAnchor = false, startAnchor = false, endAnchor = false;
        var pattern = body;
        if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
        {
            try { regex = new Regex(pattern[1..^1], RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(5)); }
            catch (ArgumentException) { return null; }
            pattern = "";
        }
        else
        {
            if (pattern.StartsWith("||", StringComparison.Ordinal)) { hostAnchor = true; pattern = pattern[2..]; }
            else if (pattern.StartsWith('|')) { startAnchor = true; pattern = pattern[1..]; }
            if (pattern.EndsWith('|')) { endAnchor = true; pattern = pattern[..^1]; }
            while (pattern.Contains("**")) pattern = pattern.Replace("**", "*");
            if (!matchCase) pattern = pattern.ToLowerInvariant();
            if (pattern.Length == 0 || pattern == "*")
            {
                // Matches everything: only meaningful with a domain or type limit (e.g. @@*$document,domain=x).
                if (domains is null && page == PageFlags.None) return null;
                pattern = "";
            }
        }

        var filter = new NetworkFilter
        {
            Text = line, Exception = exception, Important = important, Types = types, ThirdParty = thirdParty,
            Domains = domains, NotDomains = notDomains, MatchCase = matchCase, Page = page,
            Pattern = pattern, HostAnchor = hostAnchor, StartAnchor = startAnchor, EndAnchor = endAnchor, Regex = regex,
        };
        if (hostAnchor && !endAnchor && options is null && pattern.EndsWith('^') && IsHostname(pattern.AsSpan(0, pattern.Length - 1)))
            filter.PlainHost = pattern[..^1];
        return filter;
    }

    private static bool IsHostname(ReadOnlySpan<char> s)
    {
        if (s.Length == 0 || s.IndexOf('.') < 0) return false;
        foreach (var c in s) if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-')) return false;
        return true;
    }

    private static RequestType? TypeOf(string name) => name switch
    {
        "script" => RequestType.Script,
        "image" or "img" => RequestType.Image,
        "stylesheet" or "css" => RequestType.Stylesheet,
        "subdocument" or "frame" => RequestType.Subdocument,
        "xmlhttprequest" or "xhr" => RequestType.Xhr,
        "media" => RequestType.Media,
        "font" => RequestType.Font,
        "websocket" => RequestType.WebSocket,
        "ping" or "beacon" => RequestType.Ping,
        "object" or "object-subrequest" => RequestType.Object,
        "other" => RequestType.Other,
        _ => null,
    };

    /// <summary>Whether the options allow this request (type, party, page domain). The pattern is checked separately.</summary>
    public bool OptionsAllow(RequestType type, bool thirdParty, string pageHost)
    {
        if ((Types & type) == 0) return false;
        if (ThirdParty is { } tp && tp != thirdParty) return false;
        return DomainsAllow(pageHost);
    }

    public bool DomainsAllow(string pageHost)
    {
        if (NotDomains is not null)
            foreach (var d in NotDomains) if (AdVoid.Domains.IsSelfOrSub(pageHost, d)) return false;
        if (Domains is null) return true;
        foreach (var d in Domains) if (AdVoid.Domains.IsSelfOrSub(pageHost, d)) return true;
        return false;
    }

    /// <summary>Whether the pattern matches the URL (lowercased already unless the filter is $match-case) whose host
    /// starts at hostStart and runs hostLength characters.</summary>
    public bool PatternMatches(string url, string lowerUrl, int hostStart, int hostLength)
    {
        var target = MatchCase ? url : lowerUrl;
        if (Regex is not null)
        {
            try { return Regex.IsMatch(url); }
            catch (RegexMatchTimeoutException) { return false; }
        }
        if (Pattern.Length == 0) return true;
        if (StartAnchor) return MatchAt(target, 0, 0);
        if (HostAnchor)
        {
            // At the start of the host or right after one of its dots: ||example.com matches ads.example.com, not badexample.com.
            for (var at = hostStart; at < hostStart + hostLength; at++)
            {
                if ((at == hostStart || target[at - 1] == '.') && MatchAt(target, at, 0)) return true;
            }
            return false;
        }
        // Unanchored: try each position, jumping to where the first literal can start.
        var first = Pattern[0];
        for (var at = 0; at <= target.Length; at++)
        {
            if (first is not '*' and not '^')
            {
                at = target.IndexOf(first, at);
                if (at < 0) return false;
            }
            if (MatchAt(target, at, 0)) return true;
        }
        return false;
    }

    private bool MatchAt(string s, int si, int pi)
    {
        while (pi < Pattern.Length)
        {
            var c = Pattern[pi];
            if (c == '*')
            {
                if (pi + 1 == Pattern.Length) return true; // a trailing * matches the rest
                for (var k = si; k <= s.Length; k++) if (MatchAt(s, k, pi + 1)) return true;
                return false;
            }
            if (c == '^')
            {
                if (si == s.Length) { pi++; continue; }     // the end of the URL counts as a separator
                if (!IsSeparator(s[si])) return false;
                si++; pi++;
                continue;
            }
            if (si >= s.Length || s[si] != c) return false;
            si++; pi++;
        }
        return !EndAnchor || si == s.Length;
    }

    /// <summary>ABP's separator: anything but a letter, digit, or _ - . %.</summary>
    public static bool IsSeparator(char c) => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '%');

    /// <summary>The token the filter is indexed under: its longest run of letters and digits that must appear whole in
    /// any URL it matches, or null (then it's checked against every request).</summary>
    public string? Token()
    {
        if (Regex is not null || Pattern.Length == 0 || MatchCase) return null;
        string? best = null;
        var p = Pattern;
        for (var i = 0; i < p.Length; )
        {
            if (!IsTokenChar(p[i])) { i++; continue; }
            var start = i;
            while (i < p.Length && IsTokenChar(p[i])) i++;
            // Whole only when bounded: not next to '*', and at the pattern's ends only with an anchor or a separator.
            var leftOk = start > 0 ? p[start - 1] != '*' : HostAnchor || StartAnchor;
            var rightOk = i < p.Length ? p[i] != '*' : EndAnchor;
            if (!leftOk || !rightOk) continue;
            var token = p[start..i];
            if (token.Length < 2 || token is "http" or "https" or "www" or "com" or "net" or "org") continue;
            if (best is null || token.Length > best.Length) best = token;
        }
        return best;
    }

    public static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '%';
}

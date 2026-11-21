using System.Text;

namespace AdVoid;

/// <summary>
/// The filter engine: load filter lists (EasyList syntax), then ask it about each request (Match) and each page's
/// element hiding (Cosmetics). Built once and then only read, so one engine can answer from any thread; to change
/// lists, build a new engine and swap it in.
/// <para>
/// Network filters are indexed like uBlock Origin and Adblock Plus do it: a plain <c>||host^</c> by hostname, every
/// other filter under its longest whole token (a run of letters and digits it requires), so a request is only tested
/// against filters sharing a token with its URL instead of all of them.
/// </para>
/// </summary>
public sealed class FilterEngine
{
    private readonly Dictionary<string, NetworkFilter> _blockHosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NetworkFilter>> _blockByToken = new(StringComparer.Ordinal);
    private readonly List<NetworkFilter> _blockUntokened = [];
    private readonly Dictionary<string, List<NetworkFilter>> _allowByToken = new(StringComparer.Ordinal);
    private readonly List<NetworkFilter> _allowUntokened = [];
    private readonly List<NetworkFilter> _pageExceptions = [];

    private readonly List<CosmeticFilter> _genericHide = [];                                  // ##sel and ~a.com##sel
    private readonly Dictionary<string, List<string>> _hideByDomain = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _unhideByDomain = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unhideEverywhere = new(StringComparer.Ordinal);
    private string? _genericCss;

    /// <summary>How many filters were taken in (network, cosmetic) and how many lines were skipped as unsupported.</summary>
    public (int Network, int Cosmetic, int Skipped) Counts { get; private set; }

    /// <summary>Builds an engine from filter list lines (any number of lists, concatenated).</summary>
    public static FilterEngine Build(IEnumerable<string> lines)
    {
        var engine = new FilterEngine();
        int network = 0, cosmetic = 0, skipped = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '!' or '[') continue;
            if (CosmeticFilter.IsCosmetic(line))
            {
                if (CosmeticFilter.Parse(line) is { } c) { engine.AddCosmetic(c); cosmetic++; }
                else skipped++;
                continue;
            }
            if (NetworkFilter.Parse(line) is { } n) { engine.AddNetwork(n); network++; }
            else skipped++;
        }
        engine.Counts = (network, cosmetic, skipped);
        return engine;
    }

    private void AddNetwork(NetworkFilter f)
    {
        if (f.Page != NetworkFilter.PageFlags.None) { _pageExceptions.Add(f); if (f.Types == RequestType.None) return; }
        if (!f.Exception && f.PlainHost is { } host && !f.Important) { _blockHosts.TryAdd(host, f); return; }
        var (byToken, untokened) = f.Exception ? (_allowByToken, _allowUntokened) : (_blockByToken, _blockUntokened);
        if (f.Token() is { } token)
        {
            if (!byToken.TryGetValue(token, out var list)) byToken[token] = list = [];
            list.Add(f);
        }
        else untokened.Add(f);
    }

    private void AddCosmetic(CosmeticFilter c)
    {
        if (c.Exception)
        {
            if (c.Domains is null) { _unhideEverywhere.Add(c.Selector); return; }
            foreach (var d in c.Domains)
            {
                if (!_unhideByDomain.TryGetValue(d, out var set)) _unhideByDomain[d] = set = new(StringComparer.Ordinal);
                set.Add(c.Selector);
            }
            return;
        }
        if (c.Domains is null) { _genericHide.Add(c); return; }
        foreach (var d in c.Domains)
        {
            if (!_hideByDomain.TryGetValue(d, out var list)) _hideByDomain[d] = list = [];
            list.Add(c.Selector);
        }
        // a.com,~b.a.com##x: the ~ part means "not on that subdomain"; kept as an unhide there.
        if (c.NotDomains is not null)
            foreach (var d in c.NotDomains)
            {
                if (!_unhideByDomain.TryGetValue(d, out var set)) _unhideByDomain[d] = set = new(StringComparer.Ordinal);
                set.Add(c.Selector);
            }
    }

    /// <summary>Whether to block a request, and the filter that decided.</summary>
    public Decision Match(Request request)
    {
        if (request.Type == RequestType.Document) return Decision.Allow; // whole pages aren't blocked (yet)
        var url = request.Url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "ws" && uri.Scheme != "wss"))
            return Decision.Allow;
        var host = uri.Host.ToLowerInvariant();
        var pageHost = request.PageUrl is { } page ? Request.Host(page) : "";
        if (pageHost.Length > 0 && PageExcepted(request.PageUrl!, pageHost, NetworkFilter.PageFlags.Document)) return Decision.Allow;
        var thirdParty = pageHost.Length > 0 && !Domains.SameSite(host, pageHost);
        var lower = url.ToLowerInvariant();
        var hostStart = lower.IndexOf(host, StringComparison.Ordinal);
        if (hostStart < 0) hostStart = 0;

        HashSet<string>? tokens = null;
        NetworkFilter? block = null;

        // Hostname filters first (cheapest), then indexed filters.
        foreach (var h in Domains.SelfAndParents(host))
            if (_blockHosts.TryGetValue(h, out var f) && f.OptionsAllow(request.Type, thirdParty, pageHost)) { block = f; break; }
        if (block is null)
        {
            tokens = Tokens(lower);
            block = FirstMatch(_blockByToken, _blockUntokened, tokens, url, lower, hostStart, host.Length, request.Type, thirdParty, pageHost, importantOnly: false);
        }
        if (block is null) return Decision.Allow;
        if (pageHost.Length > 0 && !block.Important && block.Domains is null && PageExcepted(request.PageUrl!, pageHost, NetworkFilter.PageFlags.GenericBlock))
            return Decision.Allow;
        if (block.Important) return new Decision(true, block.Text);

        // An $important filter elsewhere beats exceptions; otherwise an exception lets it through.
        tokens ??= Tokens(lower);
        if (FirstMatch(_blockByToken, _blockUntokened, tokens, url, lower, hostStart, host.Length, request.Type, thirdParty, pageHost, importantOnly: true) is { } important)
            return new Decision(true, important.Text);
        var allow = FirstMatch(_allowByToken, _allowUntokened, tokens, url, lower, hostStart, host.Length, request.Type, thirdParty, pageHost, importantOnly: false);
        return allow is null ? new Decision(true, block.Text) : new Decision(false, allow.Text);
    }

    private static NetworkFilter? FirstMatch(Dictionary<string, List<NetworkFilter>> byToken, List<NetworkFilter> untokened, HashSet<string> tokens,
        string url, string lower, int hostStart, int hostLength, RequestType type, bool thirdParty, string pageHost, bool importantOnly)
    {
        foreach (var token in tokens)
        {
            if (!byToken.TryGetValue(token, out var list)) continue;
            foreach (var f in list)
                if ((!importantOnly || f.Important) && f.OptionsAllow(type, thirdParty, pageHost) && f.PatternMatches(url, lower, hostStart, hostLength)) return f;
        }
        foreach (var f in untokened)
            if ((!importantOnly || f.Important) && f.OptionsAllow(type, thirdParty, pageHost) && f.PatternMatches(url, lower, hostStart, hostLength)) return f;
        return null;
    }

    private static HashSet<string> Tokens(string lower)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lower.Length; )
        {
            if (!NetworkFilter.IsTokenChar(lower[i])) { i++; continue; }
            var start = i;
            while (i < lower.Length && NetworkFilter.IsTokenChar(lower[i])) i++;
            if (i - start >= 2) set.Add(lower[start..i]);
        }
        return set;
    }

    /// <summary>Whether a page-level exception (@@...$document / $elemhide / $generichide / $genericblock) covers the page.</summary>
    private bool PageExcepted(string pageUrl, string pageHost, NetworkFilter.PageFlags flag)
    {
        var lower = pageUrl.ToLowerInvariant();
        var hostStart = Math.Max(0, lower.IndexOf(pageHost, StringComparison.Ordinal));
        foreach (var f in _pageExceptions)
            if ((f.Page & flag) != 0 && f.DomainsAllow(pageHost) && f.PatternMatches(pageUrl, lower, hostStart, pageHost.Length)) return true;
        return false;
    }

    /// <summary>
    /// The CSS that hides ads on a page: the site's own hiding rules plus the generic ones (unless the page has
    /// $generichide), minus #@# exceptions. Each selector gets its own rule, so one the browser doesn't understand
    /// can't take the others down with it. Empty when the page is allowlisted ($document or $elemhide).
    /// </summary>
    public string Cosmetics(string pageUrl)
    {
        var host = Request.Host(pageUrl);
        if (host.Length == 0) return "";
        if (PageExcepted(pageUrl, host, NetworkFilter.PageFlags.Document | NetworkFilter.PageFlags.ElemHide)) return "";

        var unhide = new HashSet<string>(_unhideEverywhere, StringComparer.Ordinal);
        var specific = new List<string>();
        foreach (var d in Domains.SelfAndParents(host))
        {
            if (_unhideByDomain.TryGetValue(d, out var u)) unhide.UnionWith(u);
            if (_hideByDomain.TryGetValue(d, out var h)) specific.AddRange(h);
        }

        var css = new StringBuilder();
        foreach (var s in specific) if (!unhide.Contains(s)) Rule(css, s);
        if (!PageExcepted(pageUrl, host, NetworkFilter.PageFlags.GenericHide))
        {
            if (unhide.SetEquals(_unhideEverywhere))
            {
                css.Append(GenericCss());
                foreach (var g in _genericHide) if (g.NotDomains is not null && !unhide.Contains(g.Selector) && g.DomainsAllow(host)) Rule(css, g.Selector);
            }
            else foreach (var g in _genericHide) if (!unhide.Contains(g.Selector) && g.DomainsAllow(host)) Rule(css, g.Selector);
        }
        return css.ToString();
    }

    // The generic part is the same on nearly every page: built once. Filters with ~domain exclusions are left out of
    // the cached copy and checked per page.
    private string GenericCss()
    {
        if (_genericCss is not null) return _genericCss;
        var css = new StringBuilder();
        foreach (var g in _genericHide) if (g.NotDomains is null && !_unhideEverywhere.Contains(g.Selector)) Rule(css, g.Selector);
        return _genericCss = css.ToString();
    }

    private static void Rule(StringBuilder css, string selector) => css.Append(selector).Append("{display:none!important}\n");
}

internal static class CosmeticFilterExtensions
{
    public static bool DomainsAllow(this CosmeticFilter c, string host)
    {
        if (c.NotDomains is not null) foreach (var d in c.NotDomains) if (Domains.IsSelfOrSub(host, d)) return false;
        return true;
    }
}

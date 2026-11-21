using AdVoid;
using Xunit;

public class EngineTests
{
    private static FilterEngine Engine(params string[] lines) => FilterEngine.Build(lines);
    private static bool Blocked(FilterEngine e, string url, string page = "https://news.example/", RequestType type = RequestType.Script) =>
        e.Match(new Request(url, page, type)).Blocked;

    [Fact]
    public void HostFilterBlocksTheHostAndItsSubdomainsOnly()
    {
        var e = Engine("||doubleclick.net^");
        Assert.True(Blocked(e, "https://doubleclick.net/x.js"));
        Assert.True(Blocked(e, "https://ad.doubleclick.net/x.js"));
        Assert.False(Blocked(e, "https://notdoubleclick.net/x.js"));
        Assert.False(Blocked(e, "https://doubleclick.network/x.js"));
    }

    [Fact]
    public void ExceptionLetsARequestThrough()
    {
        var e = Engine("||ads.example.com^", "@@||ads.example.com/allowed.js");
        Assert.True(Blocked(e, "https://ads.example.com/banner.js"));
        Assert.False(Blocked(e, "https://ads.example.com/allowed.js"));
    }

    [Fact]
    public void ImportantBeatsExceptions()
    {
        var e = Engine("||tracker.example^$important", "@@||tracker.example^");
        Assert.True(Blocked(e, "https://tracker.example/t.js"));
    }

    [Fact]
    public void WildcardsSeparatorsAndAnchors()
    {
        var e = Engine("/banner/*/img^", "|https://start.example/ad", "swf|");
        Assert.True(Blocked(e, "https://x.example/banner/123/img?x=1", type: RequestType.Image));
        Assert.True(Blocked(e, "https://x.example/banner/123/img", type: RequestType.Image));
        Assert.False(Blocked(e, "https://x.example/banner/123/imgs", type: RequestType.Image));
        Assert.True(Blocked(e, "https://start.example/ad.js"));
        Assert.False(Blocked(e, "https://other.example/?u=https://start.example/ad"));
        Assert.True(Blocked(e, "https://x.example/movie.swf", type: RequestType.Object));
        Assert.False(Blocked(e, "https://x.example/movie.swf?x", type: RequestType.Object));
    }

    [Fact]
    public void TypeAndPartyOptions()
    {
        var e = Engine("||cdn.example^$script,third-party", "||pix.example^$~image");
        Assert.True(Blocked(e, "https://cdn.example/a.js", "https://site.example/"));
        Assert.False(Blocked(e, "https://cdn.example/a.css", "https://site.example/", RequestType.Stylesheet));
        Assert.False(Blocked(e, "https://cdn.example/a.js", "https://www.cdn.example/"));
        Assert.False(Blocked(e, "https://pix.example/p.gif", type: RequestType.Image));
        Assert.True(Blocked(e, "https://pix.example/p.js"));
    }

    [Fact]
    public void DomainOption()
    {
        var e = Engine("/promo.js$domain=a.example|~b.a.example");
        Assert.True(Blocked(e, "https://cdn.example/promo.js", "https://www.a.example/"));
        Assert.False(Blocked(e, "https://cdn.example/promo.js", "https://b.a.example/"));
        Assert.False(Blocked(e, "https://cdn.example/promo.js", "https://c.example/"));
    }

    [Fact]
    public void RegexFilter()
    {
        var e = Engine(@"/\/ad[0-9]+\.js/");
        Assert.True(Blocked(e, "https://x.example/ad42.js"));
        Assert.False(Blocked(e, "https://x.example/add.js"));
    }

    [Fact]
    public void DocumentExceptionAllowlistsAPage()
    {
        var e = Engine("||ads.example^", "@@||trusted.example^$document");
        Assert.True(Blocked(e, "https://ads.example/a.js", "https://news.example/"));
        Assert.False(Blocked(e, "https://ads.example/a.js", "https://trusted.example/page"));
        Assert.Equal("", e.Cosmetics("https://trusted.example/"));
    }

    [Fact]
    public void UnsupportedOptionsSkipTheFilter()
    {
        var e = Engine("||x.example^$csp=script-src 'none'", "||y.example^$redirect=noop.js");
        Assert.False(Blocked(e, "https://x.example/a.js"));
        Assert.False(Blocked(e, "https://y.example/a.js"));
        Assert.Equal(0, e.Counts.Network);
        Assert.Equal(2, e.Counts.Skipped);
    }

    [Fact]
    public void TopLevelPagesAreNeverBlocked()
    {
        var e = Engine("||ads.example^");
        Assert.False(Blocked(e, "https://ads.example/", null!, RequestType.Document));
    }

    [Fact]
    public void Cosmetics()
    {
        var e = Engine("##.ad-banner", "example.com##.side-ad", "~quiet.example##.loud-ad", "shop.example.com#@#.ad-banner",
            "##div:has-text(Sponsored)", "example.com##+js(set-constant, x, 1)");
        var news = e.Cosmetics("https://news.example/");
        Assert.Contains(".ad-banner{display:none!important}", news);
        Assert.Contains(".loud-ad{", news);
        Assert.DoesNotContain(".side-ad", news);
        Assert.DoesNotContain("has-text", news);

        var site = e.Cosmetics("https://www.example.com/");
        Assert.Contains(".side-ad{", site);
        Assert.Contains(".ad-banner{", site);

        Assert.DoesNotContain(".ad-banner{", e.Cosmetics("https://shop.example.com/"));
        Assert.DoesNotContain(".loud-ad{", e.Cosmetics("https://quiet.example/"));
    }

    [Fact]
    public void GenericHideKeepsSiteRules()
    {
        var e = Engine("##.ad-banner", "example.com##.side-ad", "@@||example.com^$generichide");
        var css = e.Cosmetics("https://example.com/");
        Assert.Contains(".side-ad{", css);
        Assert.DoesNotContain(".ad-banner{", css);
    }

    [Theory]
    [InlineData("www.example.com", "example.com")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("news.com.au", "news.com.au")]
    [InlineData("x.news.com.au", "news.com.au")]
    [InlineData("localhost", "localhost")]
    [InlineData("192.168.1.10", "192.168.1.10")]
    public void RegistrableDomain(string host, string expected) => Assert.Equal(expected, Domains.Registrable(host));

    [Fact]
    public void YouTubeScriptIsEmbedded() => Assert.Contains("adPlacements", Scriptlets.YouTube);
}

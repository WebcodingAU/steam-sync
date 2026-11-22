using System.Net;
using System.Text.RegularExpressions;
using AdVoid.Twitch;
using Xunit;

namespace AdVoid.Tests;

// TwitchAds against a pretend Twitch: the player's master and media playlists, gql tokens and the other copies.
public class TwitchFlowTests
{
    private const string Live = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:10\n#EXTINF:2.000,live\nhttps://x/seg/live1\n";
    private const string WithAd = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:10\n#EXT-X-DATERANGE:ID=\"stitched-ad-1\",CLASS=\"twitch-stitched-ad\"\n"
        + "#EXTINF:2.000,Amazon|1\nhttps://x/seg/ad1\n#EXTINF:2.000,live\nhttps://x/seg/live1\n";
    private const string PlayerUsher = "https://usher.ttvnw.net/api/v2/channel/hls/chan.m3u8?supported_codecs=av1,h265,h264&player_type=site&sig=S&token=T&p=1&play_session_id=abc";

    private static string MasterOf(string copy, params (string Id, int Height, string Codec)[] variants) =>
        "#EXTM3U\n" + string.Concat(variants.Select(v =>
            $"#EXT-X-STREAM-INF:BANDWIDTH=1,CODECS=\"{v.Codec},mp4a.40.2\",RESOLUTION=1x{v.Height},FRAME-RATE=60.000,STABLE-VARIANT-ID=\"{v.Id}\",IVS-NAME=\"{v.Id}\"\nhttps://{copy}.example/{v.Id}.m3u8\n"));

    private sealed class FakeTwitch : HttpMessageHandler
    {
        public string Site1440 = WithAd;
        public string SiteMaster = MasterOf("site", ("1440p60", 1440, "hev1.1.6.L150"), ("1080p60", 1080, "avc1.64002A"));
        public Dictionary<string, string> CopyMasters = new();     // "embed+auth", "embed", ... -> master
        public Dictionary<string, string> Pages = new();           // other URLs -> body
        public List<string> GqlAuth = [];                          // Authorization header of each gql request ("" for none)
        public List<string> Ushers = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var url = request.RequestUri!.ToString();
            string body;
            if (url.StartsWith("https://gql.twitch.tv/", StringComparison.Ordinal))
            {
                var auth = request.Headers.TryGetValues("Authorization", out var a) ? a.First() : "";
                GqlAuth.Add(auth);
                var who = auth.Length > 0 ? "auth" : "anon";
                var value = "{\\\"expires\\\":4102444800,\\\"who\\\":\\\"" + who + "\\\"}";
                body = "{\"data\":{\"streamPlaybackAccessToken\":{\"value\":\"" + value + "\",\"signature\":\"" + who + "\"}}}";
            }
            else if (url.StartsWith("https://usher.ttvnw.net/", StringComparison.Ordinal))
            {
                Ushers.Add(url);
                var query = request.RequestUri.Query;
                var type = Regex.Match(query, "player_type=([a-z-]+)").Groups[1].Value;
                body = type == "site" ? SiteMaster : CopyMasters.GetValueOrDefault(type + (query.Contains("sig=auth") ? "+auth" : ""), "#EXTM3U\n");
            }
            else if (url == "https://site.example/1440p60.m3u8") body = Site1440;
            else body = Pages.GetValueOrDefault(url, Live);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static async Task<(TwitchAds Ads, FakeTwitch Twitch, List<string> Log)> Start(Action<FakeTwitch> setup, bool signedIn)
    {
        var twitch = new FakeTwitch();
        setup(twitch);
        var log = new List<string>();
        var ads = new TwitchAds(new HttpClient(twitch)) { Log = log.Add };
        if (signedIn) ads.SetViewerHeaders([new("Authorization", "OAuth secret"), new("Client-Integrity", "ci"), new("Cookie", "no"), new("X-Other", "no")]);
        await ads.FetchAsync(PlayerUsher, []);
        return (ads, twitch, log);
    }

    private static Task<PlaylistResponse> Poll(TwitchAds ads) => ads.FetchAsync("https://site.example/1440p60.m3u8", []);

    [Fact]
    public async Task SignedInCopyKeepsTheViewersQualityAndCodec()
    {
        var (ads, twitch, log) = await Start(t =>
        {
            t.CopyMasters["embed"] = MasterOf("anon", ("1080p60", 1080, "avc1.64002A"));                     // anonymous: no 1440p
            t.CopyMasters["embed+auth"] = MasterOf("auth", ("1440p60", 1440, "hev1.1.6.L150"), ("1080p60", 1080, "avc1.64002A"));
            t.Pages["https://auth.example/1440p60.m3u8"] = Live.Replace("live1", "auth1440");
        }, signedIn: true);

        var r = await Poll(ads);
        Assert.Equal(PlaylistAction.Replaced, r.Action);
        Assert.Contains("auth1440", r.Body);
        Assert.Equal("OAuth secret", twitch.GqlAuth[0]);
        // The copy was asked for the way the player asked: same API and codecs, its own player type and token.
        var copyUsher = twitch.Ushers.Last();
        Assert.StartsWith("https://usher.ttvnw.net/api/v2/channel/hls/chan.m3u8?", copyUsher);
        Assert.Contains("supported_codecs=av1%2Ch265%2Ch264", copyUsher);
        Assert.Contains("player_type=embed", copyUsher);
        Assert.DoesNotContain("play_session_id", copyUsher);
        Assert.DoesNotContain("sig=S", copyUsher);
        Assert.Contains(log, l => l.Contains("embed (signed in) copy at 1440p60") && l.Contains("1440p60 hevc"));
        Assert.DoesNotContain(log, l => l.Contains("secret"));
    }

    [Fact]
    public async Task NoCopyInTheSameCodecCutsTheAdInsteadOfGoingBlack()
    {
        var (ads, _, log) = await Start(t =>
        {
            t.CopyMasters["embed"] = MasterOf("anon", ("1080p60", 1080, "avc1.64002A"));
            t.CopyMasters["popout"] = MasterOf("anon2", ("1080p60", 1080, "avc1.64002A"));
            t.CopyMasters["autoplay"] = MasterOf("anon3", ("360p30", 360, "avc1.4D401E"));
        }, signedIn: false);

        var r = await Poll(ads);
        Assert.Equal(PlaylistAction.Stripped, r.Action);
        Assert.DoesNotContain("ad1", r.Body);
        Assert.Contains("live1", r.Body);
        Assert.Contains(log, l => l.Contains("no ad-free copy in that codec"));
    }

    [Fact]
    public async Task AChannelStaysOnTheCopyAfterTheBreak()
    {
        var (ads, twitch, log) = await Start(t =>
        {
            t.SiteMaster = MasterOf("site", ("1440p60", 1440, "avc1.640033"), ("720p60", 720, "avc1.4D401F"));
            t.CopyMasters["embed"] = MasterOf("anon", ("1080p60", 1080, "avc1.64002A"), ("720p60", 720, "avc1.4D401F"));
            t.Pages["https://anon.example/1080p60.m3u8"] = Live.Replace("live1", "copy1080");
            t.Pages["https://anon.example/720p60.m3u8"] = Live.Replace("live1", "copy720");
            t.Pages["https://site.example/720p60.m3u8"] = Live.Replace("live1", "site720");
        }, signedIn: false);

        Assert.Equal(PlaylistAction.None, (await ads.FetchAsync("https://site.example/720p60.m3u8", [])).Action); // no ad yet: the stream itself
        Assert.Equal(PlaylistAction.Replaced, (await Poll(ads)).Action);
        twitch.Site1440 = Live;                                            // the break is over...
        var after = await Poll(ads);
        Assert.Equal(PlaylistAction.Continued, after.Action);              // ...but the copy keeps numbering the segments
        Assert.Contains("copy1080", after.Body);
        var other = await ads.FetchAsync("https://site.example/720p60.m3u8", []);
        Assert.Equal(PlaylistAction.Continued, other.Action);              // every quality of the channel, not just the one
        Assert.Contains("copy720", other.Body);
        twitch.Site1440 = WithAd;                                          // a later break: still the copy, logged again
        Assert.Equal(PlaylistAction.Replaced, (await Poll(ads)).Action);
        await ads.FetchAsync(PlayerUsher, []);                             // the player reloads its master: still the copy
        Assert.Contains("copy1080", (await Poll(ads)).Body);
        Assert.Equal(2, log.Count(l => l.Contains("playing the embed copy at 1080p60")));
        Assert.Single(log, l => l.Contains("is over (staying on the ad-free copy)"));
    }

    [Fact]
    public void SignedOutPagesDontClearTheSession()
    {
        var ads = new TwitchAds(new HttpClient(new FakeTwitch()));
        Assert.False(ads.SignedIn);
        ads.SetViewerHeaders([new("Client-Id", "x")]);
        Assert.False(ads.SignedIn);
        ads.SetViewerHeaders([new("Authorization", "OAuth a")]);
        Assert.True(ads.SignedIn);
        ads.SetViewerHeaders([new("Client-Id", "x")]);
        Assert.True(ads.SignedIn);
    }
}

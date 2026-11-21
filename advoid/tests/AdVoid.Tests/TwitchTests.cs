using AdVoid.Twitch;
using Xunit;

namespace AdVoid.Tests;

// Playlists shaped like Twitch's (2026-11), with made-up URLs.
public class TwitchTests
{
    private const string Master = """
        #EXTM3U
        #EXT-X-TWITCH-INFO:NODE="video-edge",SERVING-ID="x"
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="chunked",NAME="1080p60 (source)",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=8000000,RESOLUTION=1920x1080,CODECS="avc1.64002A,mp4a.40.2",VIDEO="chunked",FRAME-RATE=60.000
        https://a.playlist.ttvnw.net/v1/playlist/source.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="720p60",NAME="720p60",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=3400000,RESOLUTION=1280x720,CODECS="avc1.4D401F,mp4a.40.2",VIDEO="720p60",FRAME-RATE=60.000
        https://a.playlist.ttvnw.net/v1/playlist/720p60.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="480p30",NAME="480p",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=1400000,RESOLUTION=852x480,CODECS="avc1.4D401F,mp4a.40.2",VIDEO="480p30",FRAME-RATE=30.000
        https://a.playlist.ttvnw.net/v1/playlist/480p30.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="audio_only",NAME="audio_only",AUTOSELECT=NO,DEFAULT=NO
        #EXT-X-STREAM-INF:BANDWIDTH=160000,CODECS="mp4a.40.2",VIDEO="audio_only"
        https://a.playlist.ttvnw.net/v1/playlist/audio.m3u8
        """;

    private const string Live = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:20815
        #EXT-X-DATERANGE:ID="source-1",CLASS="twitch-stream-source",START-DATE="2026-11-21T12:58:04.908Z",END-ON-NEXT=YES,X-TV-TWITCH-STREAM-SOURCE="live"
        #EXT-X-PROGRAM-DATE-TIME:2026-11-21T12:58:04.908Z
        #EXTINF:1.986,live
        https://x.hls.ttvnw.net/v1/segment/live1
        #EXT-X-PROGRAM-DATE-TIME:2026-11-21T12:58:06.894Z
        #EXTINF:1.985,live
        https://x.hls.ttvnw.net/v1/segment/live2
        """;

    // A mid-roll: two ad segments, then the stream again.
    private const string MidRoll = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-TARGETDURATION:5
        #EXT-X-MEDIA-SEQUENCE:100
        #EXT-X-DATERANGE:ID="stitched-ad-1",CLASS="twitch-stitched-ad",START-DATE="2026-11-21T12:58:33.984Z",DURATION=4.0
        #EXT-X-DATERANGE:ID="quartile-1-0",CLASS="twitch-ad-quartile",START-DATE="2026-11-21T12:58:33.984Z",DURATION=2.000
        #EXT-X-DISCONTINUITY
        #EXT-X-MAP:URI="https://x.hls.ttvnw.net/v1/segment/adinit"
        #EXT-X-PROGRAM-DATE-TIME:2026-11-21T12:58:33.984Z
        #EXTINF:2.000,Amazon|2474283100495.0
        https://x.hls.ttvnw.net/v1/segment/ad1
        #EXT-X-PROGRAM-DATE-TIME:2026-11-21T12:58:35.984Z
        #EXTINF:2.000,Amazon|2474283100495.0
        https://x.hls.ttvnw.net/v1/segment/ad2
        #EXT-X-DISCONTINUITY
        #EXT-X-PROGRAM-DATE-TIME:2026-11-21T12:58:37.984Z
        #EXTINF:2.000,live
        https://x.hls.ttvnw.net/v1/segment/live3
        #EXT-X-TWITCH-PREFETCH:https://x.hls.ttvnw.net/v1/segment/next
        """;

    [Fact]
    public void ReadsTheQualities()
    {
        var variants = Hls.ParseMaster(Master);
        Assert.Equal(4, variants.Count);
        Assert.Equal(new Variant("https://a.playlist.ttvnw.net/v1/playlist/source.m3u8", "1080p60 (source)", "chunked", 1080, 60, 8000000), variants[0]);
        Assert.Equal("480p", variants[2].Name);
        Assert.Equal(0, variants[3].Height);
    }

    // The 2026 format: no #EXT-X-MEDIA, names in IVS-NAME, ids in STABLE-VARIANT-ID.
    private const string MasterIvs = """
        #EXTM3U
        #EXT-X-SESSION-DATA:DATA-ID="SUPPRESS",VALUE="false"
        #EXT-X-STREAM-INF:BANDWIDTH=6348158,CODECS="avc1.64002A,mp4a.40.2",RESOLUTION=1920x1080,FRAME-RATE=59.940,STABLE-VARIANT-ID="1080p60",IVS-NAME="1080p60",IVS-VARIANT-SOURCE="source"
        https://d1.cloudfront.net/x/1080p60/index-dvr.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=1459403,CODECS="avc1.4D401F,mp4a.40.2",RESOLUTION=852x480,FRAME-RATE=30.221,STABLE-VARIANT-ID="480p30",IVS-NAME="480p",IVS-VARIANT-SOURCE="transcode"
        https://d1.cloudfront.net/x/480p30/index-dvr.m3u8
        """;

    [Fact]
    public void ReadsTheNewerQualities()
    {
        var variants = Hls.ParseMaster(MasterIvs);
        Assert.Equal(2, variants.Count);
        Assert.Equal(new Variant("https://d1.cloudfront.net/x/480p30/index-dvr.m3u8", "480p", "480p30", 480, 30.221, 1459403), variants[1]);
        Assert.Equal("1080p60", variants[0].Name);
    }

    [Fact]
    public async Task KnowsMediaPlaylistsFromTheirMaster()
    {
        var ads = new TwitchAds(new HttpClient(new Canned(MasterIvs)));
        var media = "https://d1.cloudfront.net/x/480p30/index-dvr.m3u8";
        Assert.Equal(PlaylistKind.None, ads.Kind(media));
        await ads.FetchAsync("https://usher.ttvnw.net/api/v2/channel/hls/someone.m3u8?sig=x", []);
        Assert.Equal(PlaylistKind.Media, ads.Kind(media));
    }

    private sealed class Canned(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    [Fact]
    public void PicksTheClosestQuality()
    {
        var variants = Hls.ParseMaster(Master);
        var autoplay = variants.Where(v => v.Group == "480p30").ToList();
        Assert.Equal("720p60", Hls.Closest(variants, variants[1])!.Group);                    // same quality
        Assert.Equal("480p30", Hls.Closest(autoplay, variants[0])!.Group);                    // the best one there is
        Assert.Equal("chunked", Hls.Closest(variants, null)!.Group);                          // unknown: the best
        Assert.Equal("480p30", Hls.Closest(variants, variants[1] with { Group = "x", Height = 600 })!.Group); // tallest not taller
        Assert.Null(Hls.Closest(variants.Where(v => v.Height == 0).ToList(), variants[0]));   // audio only
    }

    [Fact]
    public void SpotsAds()
    {
        Assert.False(Hls.HasAds(Live));
        Assert.True(Hls.HasAds(MidRoll));
        Assert.True(Hls.HasAds(Live.Replace(",live\nhttps://x.hls.ttvnw.net/v1/segment/live2", ",Amazon|1\nhttps://x.hls.ttvnw.net/v1/segment/live2")));
        Assert.True(Hls.HasLive(MidRoll));
        Assert.False(Hls.HasLive(MidRoll.Replace("2.000,live", "2.000,Amazon|1")));
    }

    [Fact]
    public void CutsTheAdOut()
    {
        var cut = Hls.StripAds(MidRoll);
        Assert.False(Hls.HasAds(cut));
        Assert.DoesNotContain("segment/ad", cut);
        Assert.DoesNotContain("PREFETCH", cut);
        Assert.Contains("https://x.hls.ttvnw.net/v1/segment/live3", cut);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:102\n", cut);           // two segments removed ahead of the first kept
        Assert.Contains("12:58:37.984Z\n#EXTINF:2.000,live", cut);       // the kept segment keeps its own tags
        Assert.Equal(1, cut.Split('\n').Count(l => l.StartsWith("#EXT-X-DISCONTINUITY")));
    }

    [Fact]
    public void AnAllAdPlaylistBecomesEmpty()
    {
        var cut = Hls.StripAds(MidRoll.Replace("2.000,live", "2.000,Amazon|1"));
        Assert.DoesNotContain("#EXTINF", cut);
        Assert.StartsWith("#EXTM3U\n", cut);
    }

    [Fact]
    public void LiveIsUntouched()
    {
        var cut = Hls.StripAds(Live);
        Assert.Equal(Live.Replace("\r", "").TrimEnd() + "\n", cut);
    }

    [Theory]
    [InlineData("https://usher.ttvnw.net/api/v2/channel/hls/nix.m3u8?acmb=x&sig=y", PlaylistKind.Master)]
    [InlineData("https://usher.ttvnw.net/api/channel/hls/some_one.m3u8?token=x", PlaylistKind.Master)]
    [InlineData("https://aps21.playlist.ttvnw.net/v1/playlist/Cs0HBpPb1sRk.m3u8", PlaylistKind.Media)]
    [InlineData("https://aps21.playlist.ttvnw.net/v1/playlist/Cs0HBpPb1sRk", PlaylistKind.Media)]
    [InlineData("https://video-weaver.syd03.hls.ttvnw.net/v1/playlist/abc.m3u8", PlaylistKind.Media)]
    [InlineData("https://x.j.cloudfront.hls.ttvnw.net/v1/segment/abc", PlaylistKind.None)]
    [InlineData("https://aps23.playlist.ttvnw.net/trigger/abc", PlaylistKind.None)]
    [InlineData("https://usher.ttvnw.net/vod/123.m3u8", PlaylistKind.None)]
    [InlineData("https://evil.example/v1/playlist/x.m3u8", PlaylistKind.None)]
    [InlineData("http://aps21.playlist.ttvnw.net/v1/playlist/x.m3u8", PlaylistKind.None)]
    public void ClassifiesRequests(string url, PlaylistKind kind) => Assert.Equal(kind, TwitchAds.Classify(url));
}

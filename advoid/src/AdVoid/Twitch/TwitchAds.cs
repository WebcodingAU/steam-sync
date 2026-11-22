using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdVoid.Twitch;

/// <summary>A playlist for the host to answer the player's request with.</summary>
/// <param name="Headers">Response headers worth passing on (content type, CORS, caching).</param>
/// <param name="Action">What AdVoid did: passed through, swapped for an ad-free copy, or ads cut out.</param>
public sealed record PlaylistResponse(int Status, string Reason, string Body, IReadOnlyList<KeyValuePair<string, string>> Headers, PlaylistAction Action);

/// <summary>What AdVoid answered a media playlist with.</summary>
public enum PlaylistAction
{
    /// <summary>The stream's own playlist, as it came.</summary>
    None,
    /// <summary>An ad-free copy, during an ad break.</summary>
    Replaced,
    /// <summary>The ad cut out (no copy to use), during an ad break.</summary>
    Stripped,
    /// <summary>The ad-free copy again after the break: a channel stays on its copy (see <see cref="TwitchAds"/>).</summary>
    Continued,
}

/// <summary>Which of Twitch's playlists a request is for.</summary>
public enum PlaylistKind { None, Master, Media }

/// <summary>
/// Removes the ads Twitch splices into live streams. Twitch doesn't load ads from ad servers: it puts ad segments into
/// the stream's own playlist, so request blocking can't touch them. But Twitch serves several copies of each stream,
/// one per player type, and the copies for embedded players usually come without ads. So the host routes the
/// player's playlist requests through here: master playlists are passed on (remembering which URL is which quality),
/// and while a media playlist carries an ad, the player is given the same quality from an ad-free copy instead.
/// </summary>
/// <remarks>
/// <para>Copies are tried in order: <c>embed</c> and <c>popout</c> as the signed-in viewer (when the host has passed on
/// the page's own Twitch headers, see <see cref="SetViewerHeaders"/>), so the copy has the same qualities as the
/// viewer's stream (signed-in viewers get 1440p and HEVC/AV1 tiers anonymous ones don't), then <c>embed</c>,
/// <c>popout</c> and the 360p <c>autoplay</c> anonymously. A copy is only used in the same codec as what the player is
/// playing: a player can't switch codecs mid-stream (it goes black). With no copy to use, the ad segments are cut out,
/// which pauses the picture for the break instead of showing it.</para>
/// <para>Once a channel has been switched to a copy, it stays on it (every quality, from then on): a stream that
/// started with an ad numbers its segments from 0, the copies by the live stream, so handing the stream back after the
/// break would give the player segment numbers it has already passed, and it would wait for ever. The copy is the same
/// stream, so staying costs nothing but one playlist request per poll.</para>
/// <para>Thread-safe; one instance can serve every window. The viewer's headers are only ever sent to gql.twitch.tv,
/// and never logged.</para>
/// </remarks>
public sealed partial class TwitchAds(HttpClient http)
{
    /// <summary>The client id twitch.tv's own web player uses (public, part of the site).</summary>
    public const string ClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    /// <summary>The page's request headers worth reusing for the signed-in copy (the viewer's session with Twitch).</summary>
    public static readonly string[] ViewerHeaderNames = ["Authorization", "Client-Integrity", "Device-ID", "Client-Version", "Client-Session-Id"];

    private sealed record Source(string PlayerType, bool SignedIn)
    {
        public override string ToString() => SignedIn ? PlayerType + " (signed in)" : PlayerType;
    }

    private static readonly Source[] SignedInSources = [new("embed", true), new("popout", true)];
    private static readonly Source[] AnonymousSources = [new("embed", false), new("popout", false), new("autoplay", false)];
    private static readonly TimeSpan BackupLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NoCopyFor = TimeSpan.FromSeconds(8);
    private static readonly string[] PassedHeaders =
        ["Content-Type", "Cache-Control", "Access-Control-Allow-Origin", "Access-Control-Allow-Credentials", "Access-Control-Expose-Headers", "Timing-Allow-Origin"];
    // Usher parameters that belong to one request or one player type; the rest (codecs, platform, ...) are reused.
    private static readonly HashSet<string> OwnUsherParameters = new(StringComparer.OrdinalIgnoreCase)
        { "sig", "token", "p", "player_type", "play_session_id", "acmb" };

    private sealed record Known(string Login, Variant Variant);
    private sealed record Backup(List<Variant> Variants, DateTime Expires);
    private sealed record Usher(string Path, List<KeyValuePair<string, string>> Parameters);

    // A channel's switch to a copy: which copy (null: cutting the ad out), what was last logged, whether a break is on.
    private sealed class Break
    {
        public Source? Source;
        public string Shown = "";
        public bool InAd = true;
    }

    private readonly ConcurrentDictionary<string, Known> _known = new(StringComparer.Ordinal);    // media playlist URL -> channel, quality
    private readonly ConcurrentDictionary<string, Usher> _usher = new(StringComparer.Ordinal);    // login -> how the player asked for it
    private readonly ConcurrentDictionary<string, Backup> _backups = new(StringComparer.Ordinal); // "login|source" -> its qualities
    private readonly ConcurrentDictionary<string, DateTime> _noCopy = new(StringComparer.Ordinal); // login -> until when not to look
    private readonly ConcurrentDictionary<string, Break> _breaks = new(StringComparer.Ordinal);   // login -> its switch to a copy
    private volatile IReadOnlyList<KeyValuePair<string, string>> _viewer = [];
    private long _replaced, _stripped;

    /// <summary>Media playlists answered with an ad-free copy.</summary>
    public long Replaced => Interlocked.Read(ref _replaced);
    /// <summary>Media playlists answered with the ad segments cut out.</summary>
    public long Stripped => Interlocked.Read(ref _stripped);

    /// <summary>Called with a line worth logging (an ad break starting or ending, a failed copy).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Passes on the headers of one of the page's own gql.twitch.tv requests (only <see cref="ViewerHeaderNames"/> are
    /// kept), so ad-free copies can be asked for as the signed-in viewer. Requests without an Authorization header
    /// are ignored, so a signed-out page leaves the last session in place (a stale one just fails and falls back).
    /// </summary>
    public void SetViewerHeaders(IEnumerable<KeyValuePair<string, string>> pageHeaders)
    {
        var kept = pageHeaders.Where(h => ViewerHeaderNames.Contains(h.Key, StringComparer.OrdinalIgnoreCase) && h.Value.Length > 0).ToList();
        if (!kept.Any(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))) return;
        _viewer = kept;
    }

    /// <summary>True once the page's signed-in headers are known.</summary>
    public bool SignedIn => _viewer.Count > 0;

    /// <summary>
    /// What kind of Twitch playlist <paramref name="url"/> is; only these need <see cref="FetchAsync"/>. Media playlists
    /// are known from the master playlists that listed them (some are on cloudfront.net, not ttvnw.net).
    /// </summary>
    public PlaylistKind Kind(string url) => _known.ContainsKey(url) ? PlaylistKind.Media : Classify(url);

    /// <summary>What kind of Twitch playlist <paramref name="url"/> looks like from its address alone.</summary>
    public static PlaylistKind Classify(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return PlaylistKind.None;
        var host = uri.Host;
        if (!host.EndsWith(".ttvnw.net", StringComparison.OrdinalIgnoreCase)) return PlaylistKind.None;
        var path = uri.AbsolutePath;
        if (host.Equals("usher.ttvnw.net", StringComparison.OrdinalIgnoreCase))
            return ChannelRegex().IsMatch(path) ? PlaylistKind.Master : PlaylistKind.None;
        if (path.Contains("/trigger/", StringComparison.Ordinal) || path.Contains("/segment/", StringComparison.Ordinal)) return PlaylistKind.None;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || path.Contains("/playlist/", StringComparison.Ordinal)
            ? PlaylistKind.Media : PlaylistKind.None;
    }

    /// <summary>
    /// Fetches the playlist the player asked for (with the player's own request headers, minus cookies) and returns
    /// what to answer it with. Errors from Twitch are passed on as they are; network failures throw, and the host
    /// should then let the request go ahead normally.
    /// </summary>
    public async Task<PlaylistResponse> FetchAsync(string url, IEnumerable<KeyValuePair<string, string>> requestHeaders, CancellationToken cancel = default)
    {
        var kind = Kind(url);
        // A channel already on a copy: fetch the copy alongside the stream's own playlist, not after it, so the
        // player's playlist requests take no longer than without AdVoid.
        Task<(string Text, Variant Variant, Source Source)?>? early = null;
        if (kind == PlaylistKind.Media && _known.TryGetValue(url, out var knownEarly)
            && _breaks.TryGetValue(knownEarly.Login, out var breakEarly) && breakEarly.Source is { } sourceEarly)
            early = TryCopyAsync(knownEarly, sourceEarly, cancel);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in requestHeaders)
        {
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(name, value);
        }
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        var headers = PassOn(response);
        if (response.StatusCode != HttpStatusCode.OK || kind == PlaylistKind.None)
            return new((int)response.StatusCode, response.ReasonPhrase ?? "", body, headers, PlaylistAction.None);

        if (kind == PlaylistKind.Master)
        {
            var login = Login(url);
            if (login is not null)
            {
                RememberUsher(login, url);
                foreach (var variant in Hls.ParseMaster(body)) _known[variant.Url] = new Known(login, variant);
            }
            return new(200, "OK", body, headers, PlaylistAction.None);
        }

        var known = _known.GetValueOrDefault(url);
        var hasAds = Hls.HasAds(body);
        var passThrough = new PlaylistResponse(200, "OK", body, headers, PlaylistAction.None);
        if (known is null) // a media playlist no master listed: cut any ad out, nothing better to offer
        {
            if (!hasAds) return passThrough;
            Interlocked.Increment(ref _stripped);
            return new(200, "OK", Hls.StripAds(body), headers, PlaylistAction.Stripped);
        }

        _breaks.TryGetValue(known.Login, out var brk);
        if (brk is null && !hasAds) return passThrough;
        brk ??= _breaks.GetOrAdd(known.Login, _ => new Break());
        if (brk.InAd != hasAds)
        {
            brk.InAd = hasAds;
            if (!hasAds) Log?.Invoke($"Twitch: ad break on {known.Login} is over (staying on the ad-free copy)");
            else brk.Shown = ""; // a new break: log which copy it plays
        }

        (string Text, Variant Variant, Source Source)? copy = null;
        if (!(_noCopy.TryGetValue(known.Login, out var until) && DateTime.UtcNow < until))
        {
            // The copy already in use first (switching copies is a jump for the player), then the rest in order.
            var order = (_viewer.Count > 0 ? SignedInSources.Concat(AnonymousSources) : AnonymousSources).ToList();
            if (brk.Source is not null && order.Remove(brk.Source)) order.Insert(0, brk.Source);
            if (early is not null && brk.Source is not null)
            {
                copy = await early.ConfigureAwait(false);
                order.Remove(brk.Source);
            }
            if (copy is null)
                foreach (var source in order)
                    if ((copy = await TryCopyAsync(known, source, cancel).ConfigureAwait(false)) is not null) break;
            if (copy is null) _noCopy[known.Login] = DateTime.UtcNow + NoCopyFor;
        }

        var what = $"{known.Variant.Name} {Hls.CodecFamily(known.Variant.Codec)}".TrimEnd();
        if (copy is { } c)
        {
            brk.Source = c.Source;
            var shown = $"{c.Source}|{c.Variant.Name}";
            if (brk.Shown != shown && hasAds)
                Log?.Invoke($"Twitch: ad break on {known.Login} ({what}): playing the {c.Source} copy at {c.Variant.Name}");
            brk.Shown = shown;
            if (!hasAds) return new(200, "OK", c.Text, headers, PlaylistAction.Continued);
            Interlocked.Increment(ref _replaced);
            return new(200, "OK", c.Text, headers, PlaylistAction.Replaced);
        }

        if (!hasAds) return passThrough; // no copy right now (failed): the stream itself is all there is
        brk.Source = null;
        if (brk.Shown != "cut")
            Log?.Invoke($"Twitch: ad break on {known.Login} ({what}): no ad-free copy in that codec, cutting the ad out (the picture pauses)");
        brk.Shown = "cut";
        Interlocked.Increment(ref _stripped);
        return new(200, "OK", Hls.StripAds(body), headers, PlaylistAction.Stripped);
    }

    // The same quality (same codec) from another copy of the stream, or null if it has none, has the ad too, or fails.
    private async Task<(string Text, Variant Variant, Source Source)?> TryCopyAsync(Known known, Source source, CancellationToken cancel)
    {
        var key = $"{known.Login}|{source}";
        try
        {
            if (!_backups.TryGetValue(key, out var backup) || DateTime.UtcNow >= backup.Expires)
            {
                backup = await LoadBackupAsync(known.Login, source, cancel).ConfigureAwait(false);
                _backups[key] = backup;
            }
            var variant = Hls.Closest(backup.Variants, known.Variant);
            if (variant is null) return null;
            var text = await http.GetStringAsync(variant.Url, cancel).ConfigureAwait(false);
            return Hls.HasAds(text) || !Hls.HasLive(text) ? null : (text, variant, source);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !cancel.IsCancellationRequested)
        {
            Log?.Invoke($"Twitch: the {source} copy of {known.Login} failed: {ex.Message}");
            _backups.TryRemove(key, out _);
            return null;
        }
    }

    private async Task<Backup> LoadBackupAsync(string login, Source source, CancellationToken cancel)
    {
        var query = new JsonObject
        {
            ["operationName"] = "PlaybackAccessToken_Template",
            ["query"] = "query PlaybackAccessToken_Template($login: String!, $playerType: String!) { streamPlaybackAccessToken(channelName: $login, "
                + "params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: $playerType}) { value signature } }",
            ["variables"] = new JsonObject { ["login"] = login, ["playerType"] = source.PlayerType },
        };
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
        {
            Content = new StringContent(query.ToJsonString(), Encoding.UTF8, "text/plain"),
        };
        if (source.SignedIn)
            foreach (var (name, headerValue) in _viewer) tokenRequest.Headers.TryAddWithoutValidation(name, headerValue);
        if (!tokenRequest.Headers.Contains("Client-Id")) tokenRequest.Headers.Add("Client-Id", ClientId);
        using var tokenResponse = await http.SendAsync(tokenRequest, cancel).ConfigureAwait(false);
        tokenResponse.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        var token = json?["data"]?["streamPlaybackAccessToken"];
        var value = (string?)token?["value"];
        var signature = (string?)token?["signature"];
        if (value is null || signature is null)
        {
            var error = (string?)json?["errors"]?[0]?["message"];
            throw new JsonException(error is null ? "no token" : "no token: " + error);
        }

        var expires = DateTime.UtcNow + BackupLifetime;
        try
        {
            if (JsonNode.Parse(value)?["expires"]?.GetValue<long>() is long unix)
            {
                var tokenExpiry = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime - TimeSpan.FromMinutes(1);
                if (tokenExpiry < expires) expires = tokenExpiry;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }

        // Asked for the way the player asked for its own stream (same API, codecs, platform), so the copy has the same
        // qualities in the same codecs.
        var usher = _usher.GetValueOrDefault(login);
        var builder = new StringBuilder("https://usher.ttvnw.net")
            .Append(usher?.Path ?? $"/api/channel/hls/{Uri.EscapeDataString(login)}.m3u8").Append('?');
        var parameters = usher?.Parameters ?? [new("allow_source", "true"), new("fast_bread", "true"), new("playlist_include_framerate", "true")];
        foreach (var (name, v) in parameters) builder.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(v)).Append('&');
        builder.Append("player_type=").Append(source.PlayerType)
            .Append("&p=").Append(Random.Shared.Next(1_000_000))
            .Append("&sig=").Append(signature)
            .Append("&token=").Append(Uri.EscapeDataString(value));
        var master = await http.GetStringAsync(builder.ToString(), cancel).ConfigureAwait(false);
        return new Backup(Hls.ParseMaster(master), expires);
    }

    private void RememberUsher(string login, string url)
    {
        var uri = new Uri(url);
        var parameters = new List<KeyValuePair<string, string>>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            if (OwnUsherParameters.Contains(name)) continue;
            parameters.Add(new(name, eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..])));
        }
        _usher[login] = new Usher(uri.AbsolutePath, parameters);
    }

    private static string? Login(string usherUrl)
    {
        var match = ChannelRegex().Match(new Uri(usherUrl).AbsolutePath);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static List<KeyValuePair<string, string>> PassOn(HttpResponseMessage response)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var name in PassedHeaders)
        {
            if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                list.Add(new(name, string.Join(", ", values)));
        }
        if (!list.Any(h => h.Key == "Content-Type")) list.Add(new("Content-Type", "application/vnd.apple.mpegurl"));
        return list;
    }

    [GeneratedRegex(@"/channel/hls/([A-Za-z0-9_]+)\.m3u8$")]
    private static partial Regex ChannelRegex();
}

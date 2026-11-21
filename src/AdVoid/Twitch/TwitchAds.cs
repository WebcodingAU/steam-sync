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

public enum PlaylistAction { None, Replaced, Stripped }

/// <summary>Which of Twitch's playlists a request is for.</summary>
public enum PlaylistKind { None, Master, Media }

/// <summary>
/// Removes the ads Twitch splices into live streams. Twitch doesn't load ads from ad servers: it puts ad segments into
/// the stream's own playlist, so request blocking can't touch them. But Twitch serves several copies of each stream,
/// one per player type, and the copies for embedded players usually come without ads. So the host routes the
/// player's playlist requests through here: master playlists are passed on (remembering which URL is which quality),
/// and while a media playlist carries an ad, the player is given the same quality from an ad-free copy instead
/// (<c>embed</c>, then <c>popout</c>, then the 360p <c>autoplay</c>). If every copy has the ad, the ad segments are cut
/// out, which freezes the picture for the break instead of showing it.
/// </summary>
/// <remarks>Thread-safe; one instance can serve every window. Uses Twitch's public web client id, never an account.</remarks>
public sealed partial class TwitchAds(HttpClient http)
{
    /// <summary>The client id twitch.tv's own web player uses (public, part of the site).</summary>
    public const string ClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    private static readonly string[] BackupPlayerTypes = ["embed", "popout", "autoplay"];
    private static readonly TimeSpan BackupLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AllBackupsHadAdsFor = TimeSpan.FromSeconds(8);
    private static readonly string[] PassedHeaders =
        ["Content-Type", "Cache-Control", "Access-Control-Allow-Origin", "Access-Control-Allow-Credentials", "Access-Control-Expose-Headers", "Timing-Allow-Origin"];

    private sealed record Known(string Login, Variant Variant);
    private sealed record Backup(List<Variant> Variants, DateTime Expires);

    private readonly ConcurrentDictionary<string, Known> _known = new(StringComparer.Ordinal);    // media playlist URL -> channel, quality
    private readonly ConcurrentDictionary<string, Backup> _backups = new(StringComparer.Ordinal); // "login|playerType" -> its qualities
    private readonly ConcurrentDictionary<string, DateTime> _allHadAds = new(StringComparer.Ordinal); // login -> until when to stop asking
    private long _replaced, _stripped;

    /// <summary>Media playlists answered with an ad-free copy.</summary>
    public long Replaced => Interlocked.Read(ref _replaced);
    /// <summary>Media playlists answered with the ad segments cut out.</summary>
    public long Stripped => Interlocked.Read(ref _stripped);

    /// <summary>Called with a line worth logging (an ad break starting or ending, a failed backup).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>What kind of Twitch playlist <paramref name="url"/> is; only these need <see cref="FetchAsync"/>.</summary>
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
        var kind = Classify(url);
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
                foreach (var variant in Hls.ParseMaster(body)) _known[variant.Url] = new Known(login, variant);
            return new(200, "OK", body, headers, PlaylistAction.None);
        }

        var known = _known.GetValueOrDefault(url);
        if (!Hls.HasAds(body))
        {
            if (known is not null && _inBreak.TryRemove(known.Login, out _)) Log?.Invoke($"Twitch: ad break on {known.Login} is over");
            return new(200, "OK", body, headers, PlaylistAction.None);
        }

        string? how = null, text = null;
        if (known is not null && !(_allHadAds.TryGetValue(known.Login, out var until) && DateTime.UtcNow < until))
        {
            foreach (var playerType in BackupPlayerTypes)
            {
                try { text = await BackupPlaylistAsync(known, playerType, cancel).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !cancel.IsCancellationRequested)
                {
                    Log?.Invoke($"Twitch: the {playerType} copy of {known.Login} failed: {ex.Message}");
                    _backups.TryRemove(known.Login + "|" + playerType, out _);
                    continue;
                }
                if (text is not null) { how = playerType; break; }
            }
            if (how is null) _allHadAds[known.Login] = DateTime.UtcNow + AllBackupsHadAdsFor;
        }

        PlaylistAction action;
        if (how is not null) { action = PlaylistAction.Replaced; Interlocked.Increment(ref _replaced); }
        else { action = PlaylistAction.Stripped; how = "cut"; text = Hls.StripAds(body); Interlocked.Increment(ref _stripped); }
        if (known is not null && (!_inBreak.TryGetValue(known.Login, out var before) || before != how))
            Log?.Invoke(how == "cut"
                ? $"Twitch: ad break on {known.Login} and no ad-free copy: cutting the ad out"
                : $"Twitch: ad break on {known.Login}: playing the {how} copy ({known.Variant.Name}) until it ends");
        if (known is not null) _inBreak[known.Login] = how;
        return new(200, "OK", text!, headers, action);
    }

    private readonly ConcurrentDictionary<string, string> _inBreak = new(StringComparer.Ordinal); // login -> "embed"/"popout"/"autoplay"/"cut"

    // The same quality from another player type's copy of the stream, or null if that copy has the ad too.
    private async Task<string?> BackupPlaylistAsync(Known known, string playerType, CancellationToken cancel)
    {
        var key = known.Login + "|" + playerType;
        if (!_backups.TryGetValue(key, out var backup) || DateTime.UtcNow >= backup.Expires)
        {
            backup = await LoadBackupAsync(known.Login, playerType, cancel).ConfigureAwait(false);
            _backups[key] = backup;
        }
        var variant = Hls.Closest(backup.Variants, known.Variant);
        if (variant is null) return null;
        var text = await http.GetStringAsync(variant.Url, cancel).ConfigureAwait(false);
        return Hls.HasAds(text) || !Hls.HasLive(text) ? null : text;
    }

    private async Task<Backup> LoadBackupAsync(string login, string playerType, CancellationToken cancel)
    {
        var query = new JsonObject
        {
            ["operationName"] = "PlaybackAccessToken_Template",
            ["query"] = "query PlaybackAccessToken_Template($login: String!, $playerType: String!) { streamPlaybackAccessToken(channelName: $login, "
                + "params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: $playerType}) { value signature } }",
            ["variables"] = new JsonObject { ["login"] = login, ["playerType"] = playerType },
        };
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
        {
            Content = new StringContent(query.ToJsonString(), Encoding.UTF8, "text/plain"),
        };
        tokenRequest.Headers.Add("Client-Id", ClientId);
        using var tokenResponse = await http.SendAsync(tokenRequest, cancel).ConfigureAwait(false);
        tokenResponse.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        var token = json?["data"]?["streamPlaybackAccessToken"];
        var value = (string?)token?["value"];
        var signature = (string?)token?["signature"];
        if (value is null || signature is null) throw new JsonException($"no {playerType} token for {login}");

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

        var usher = $"https://usher.ttvnw.net/api/channel/hls/{Uri.EscapeDataString(login)}.m3u8"
            + $"?allow_source=true&fast_bread=true&player_type={playerType}&playlist_include_framerate=true"
            + $"&p={Random.Shared.Next(1_000_000)}&sig={signature}&token={Uri.EscapeDataString(value)}";
        var master = await http.GetStringAsync(usher, cancel).ConfigureAwait(false);
        return new Backup(Hls.ParseMaster(master), expires);
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

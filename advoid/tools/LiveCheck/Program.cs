using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AdVoid;
using AdVoid.Twitch;

// LiveCheck: is AdVoid still right about the live sites? Plain HTTP, signed out, no browser. The nightly check runs it
// (tools/nightly-check.ps1); a person can too.
//
//   LiveCheck twitch [seconds]   reads the master playlists of top channels for every player type AdVoid uses, watches
//                                their playlists for an ad break (default 150 s) and, when one comes, runs TwitchAds on it
//   LiveCheck youtube            reads the player data of a few popular videos and lists every ad field and ad box in it
//
// Prints a JSON report. Exit code: 0 fine, 1 something AdVoid relies on has changed ("problems"), 2 nothing wrong but
// no ad was seen, so the part that removes ads wasn't exercised.

var mode = args.FirstOrDefault() ?? "";
var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36 Edg/142.0.0.0");
http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-AU,en;q=0.9");
// Twitch gets a plain client, like the app's TwitchAds: its gql answers this browser user agent with no data.
var twitchHttp = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };

var report = new JsonObject { ["check"] = mode, ["at"] = DateTimeOffset.Now.ToString("o") };
var problems = new JsonArray();
var notes = new JsonArray();
report["problems"] = problems;
report["notes"] = notes;
int exit;
try
{
    exit = mode switch
    {
        "twitch" => await Twitch(args.Length > 1 ? int.Parse(args[1]) : 150),
        "youtube" => await YouTube(),
        _ => throw new ArgumentException("usage: LiveCheck twitch [seconds] | youtube"),
    };
}
catch (Exception ex)
{
    problems.Add($"the check itself failed: {ex.GetType().Name}: {ex.Message} {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
    exit = 1;
}
if (problems.Count > 0) exit = 1;
report["result"] = exit switch { 0 => "ok", 2 => "no-ad-seen", _ => "problems" };
Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
return exit;

// ---------------------------------------------------------------- Twitch

async Task<int> Twitch(int seconds)
{
    var top = await Gql("query{streams(first:30){edges{node{broadcaster{login} contentClassificationLabels{id}}}}}");
    var channels = top["data"]!["streams"]!["edges"]!.AsArray()
        .Where(e => e!["node"]!["contentClassificationLabels"]?.AsArray().Count is null or 0) // mature-rated channels need sign-in
        .Select(e => (string)e!["node"]!["broadcaster"]!["login"]!).ToList();
    if (channels.Count == 0) { problems.Add("Twitch's top streams query returned no ungated channels"); return 1; }

    // 1. Every player type AdVoid takes copies from still answers, in a format Hls.ParseMaster understands.
    var masters = new JsonArray();
    report["masters"] = masters;
    var watch = new List<(string Login, string Usher, string Media)>();
    foreach (var login in channels.Take(12))
    {
        string[] types = channels.IndexOf(login) < 4 ? ["site", "embed", "popout", "autoplay"] : ["site"];
        foreach (var type in types)
        {
            var usher = await UsherUrl(login, type);
            if (usher is null) { problems.Add($"{login}: no playback token for player type {type}"); continue; }
            using var res = await twitchHttp.GetAsync(usher);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) { notes.Add($"{login} {type}: usher answered {(int)res.StatusCode}"); continue; }
            var variants = Hls.ParseMaster(text);
            var streamInfs = Regex.Matches(text, "^#EXT-X-STREAM-INF", RegexOptions.Multiline).Count;
            if (variants.Count == 0 || variants.Count < streamInfs) problems.Add($"{login} {type}: master has {streamInfs} streams but Hls.ParseMaster read {variants.Count}");
            if (variants.Any(v => v.Name == "" || v.Height == 0 || v.Codec == "")) problems.Add($"{login} {type}: a variant without a name, height or codec: {string.Join(", ", variants.Select(v => $"[{v.Name}|{v.Height}|{v.Codec}]"))}");
            masters.Add(new JsonObject
            {
                ["channel"] = login, ["type"] = type,
                ["format"] = text.Contains("STABLE-VARIANT-ID") ? "stable-variant-id" : text.Contains("#EXT-X-MEDIA:") ? "media-groups" : "other",
                ["variants"] = string.Join(", ", variants.Select(v => $"{v.Name} {Hls.CodecFamily(v.Codec)}")),
            });
            if (type == "site" && variants.Count > 0) watch.Add((login, usher, variants[0].Url));
        }
    }

    // 2. Watch the channels' playlists for an ad break; note every tag, daterange class and segment title seen, so
    //    a new kind of ad marker shows up even when AdVoid doesn't recognise it as one.
    var tags = new SortedSet<string>(); var classes = new SortedSet<string>(); var titles = new SortedSet<string>();
    var adHint = new Regex(@"(?i)stitched|amazon|preroll|midroll|commercial|CLASS=""[^""]*\bad", RegexOptions.Compiled);
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    JsonObject? adBreak = null;
    var tries = 0;
    while (DateTime.UtcNow < deadline && adBreak is null)
    {
        foreach (var (login, usher, media) in watch)
        {
            string text;
            try { text = await twitchHttp.GetStringAsync(media); } catch { continue; }
            foreach (Match m in Regex.Matches(text, "^(#EXT[A-Z0-9-]*)", RegexOptions.Multiline)) tags.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, "CLASS=\"([^\"]+)\"")) classes.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, "^#EXTINF:[0-9.]+,(.*)$", RegexOptions.Multiline))
                titles.Add(m.Groups[1].Value.Trim() is var t && t.Contains('|') ? t[..t.IndexOf('|')] + "|…" : m.Groups[1].Value.Trim());
            if (!Hls.HasAds(text))
            {
                if (adHint.IsMatch(text)) problems.Add($"{login}: playlist looks like it has an ad ({adHint.Match(text).Value}) but Hls.HasAds says no");
                continue;
            }
            // 3. An ad break: does TwitchAds hand the player an ad-free copy, and does that copy play?
            if (tries++ >= 3) break;
            adBreak = await SwapTest(login, usher);
            if (adBreak is not null) break;
        }
        if (adBreak is null) await Task.Delay(4000);
    }
    report["playlistTags"] = string.Join(" ", tags);
    report["daterangeClasses"] = string.Join(" ", classes);
    report["segmentTitles"] = string.Join(" ", titles);
    report["adBreak"] = adBreak;
    if (adBreak is null) { notes.Add($"no ad break on {watch.Count} channels in {seconds} s"); return 2; }
    return 0;
}

async Task<JsonObject?> SwapTest(string login, string usher)
{
    var log = new List<string>();
    var ads = new TwitchAds(twitchHttp) { Log = log.Add };
    var master = await ads.FetchAsync(usher, []);
    var top = Hls.ParseMaster(master.Body).FirstOrDefault();
    if (top is null) { problems.Add($"{login}: TwitchAds returned a master without variants"); return new JsonObject { ["channel"] = login }; }
    var r = await ads.FetchAsync(top.Url, []);
    if (r.Action == PlaylistAction.None) return null; // the break ended in between
    var result = new JsonObject
    {
        ["channel"] = login, ["quality"] = $"{top.Name} {Hls.CodecFamily(top.Codec)}", ["action"] = r.Action.ToString(),
        ["log"] = new JsonArray(log.Select(l => (JsonNode)l).ToArray()),
    };
    if (Hls.HasAds(r.Body)) problems.Add($"{login}: TwitchAds handed the player a playlist that still has ads ({r.Action})");
    else if (!Hls.HasLive(r.Body)) problems.Add($"{login}: TwitchAds handed the player a playlist with no live segments ({r.Action})");
    if (r.Action == PlaylistAction.Stripped) problems.Add($"{login}: no ad-free copy was found for a signed-out {top.Name} viewer; the ad was cut instead");
    // The copy's newest segment must download: a playlist that lists segments nobody can fetch plays as a frozen picture.
    var segment = r.Body.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("http", StringComparison.Ordinal));
    if (segment is not null)
    {
        using var res = await twitchHttp.GetAsync(segment, HttpCompletionOption.ResponseHeadersRead);
        result["segment"] = (int)res.StatusCode;
        if (!res.IsSuccessStatusCode) problems.Add($"{login}: the copy's newest segment answered {(int)res.StatusCode}");
    }
    return result;
}

async Task<string?> UsherUrl(string login, string type)
{
    var t = (await Gql($"query{{streamPlaybackAccessToken(channelName:\"{login}\",params:{{platform:\"web\",playerBackend:\"mediaplayer\",playerType:\"{type}\"}}){{value signature}}}}"))["data"]?["streamPlaybackAccessToken"];
    if (t?["value"] is null) return null;
    // The way the web player asks (api/v2 and its parameters), so the masters are the ones real viewers get.
    return $"https://usher.ttvnw.net/api/v2/channel/hls/{login}.m3u8?allow_source=true&fast_bread=true&p={Random.Shared.Next(1_000_000, 9_999_999)}"
        + "&platform=web&player_backend=mediaplayer&playlist_include_framerate=true&reassignments_supported=true"
        + $"&supported_codecs=av1,h265,h264&transcode_mode=cbr_v1&player_type={type}&sig={t["signature"]}&token={Uri.EscapeDataString((string)t["value"]!)}";
}

async Task<JsonNode> Gql(string query)
{
    var req = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql") { Content = new StringContent(new JsonObject { ["query"] = query }.ToJsonString()) };
    req.Headers.Add("Client-Id", TwitchAds.ClientId);
    using var res = await twitchHttp.SendAsync(req);
    var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    if (json["errors"] is JsonArray { Count: > 0 } errors) throw new InvalidOperationException("Twitch gql: " + errors[0]!["message"]);
    return json;
}

// ---------------------------------------------------------------- YouTube

async Task<int> YouTube()
{
    // What youtube.js removes: the keys it deletes from player data, and the ad boxes its CSS hides.
    var script = Scriptlets.YouTube;
    var adKeys = Regex.Match(script, @"AD_KEYS = \[(.*?)\]").Groups[1].Value.Split(',').Select(k => k.Trim().Trim('"')).ToHashSet();
    if (adKeys.Count == 0) { problems.Add("couldn't read AD_KEYS from youtube.js"); return 1; }

    var ids = new List<string>();
    foreach (var q in new[] { "official music video", "game trailer 2026", "podcast full episode" })
    {
        var page = await http.GetStringAsync("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q));
        ids.AddRange(Regex.Matches(page, "\"videoRenderer\":\\{\"videoId\":\"([\\w-]{11})\"").Select(m => m.Groups[1].Value).Distinct().Take(2));
    }
    if (ids.Count == 0) { problems.Add("YouTube search pages listed no videos (page format changed, or a consent/bot page)"); return 1; }

    var adLike = new Regex("^(ad|ads)([A-Z_]|$)|Ads?([A-Z]|$)|^promoted|Promoted|[Ss]ponsor", RegexOptions.Compiled);
    var videos = new JsonArray();
    report["videos"] = videos;
    var anyScheduled = 0;
    var seenTop = new SortedSet<string>(); var seenNested = new SortedSet<string>(); var seenRenderers = new SortedSet<string>();
    // The popups the player data asks for (auxiliaryUi.messageRenderers). youtube.js removes POPUP_KEYS; a new kind of
    // popup here may be a new ad-blocker warning.
    var popupKeys = Regex.Match(script, @"POPUP_KEYS = \[(.*?)\]").Groups[1].Value.Split(',').Select(k => k.Trim().Trim('"')).ToHashSet();
    var seenPopups = new SortedSet<string>();
    foreach (var id in ids)
    {
        var page = await http.GetStringAsync($"https://www.youtube.com/watch?v={id}&hl=en");
        var player = JsonAfter(page, "ytInitialPlayerResponse = ");
        var data = JsonAfter(page, "ytInitialData = ");
        var entry = new JsonObject { ["id"] = id };
        videos.Add(entry);
        if (player is null) { problems.Add($"{id}: the watch page no longer sets ytInitialPlayerResponse the way youtube.js expects"); continue; }
        if (player["playabilityStatus"]?["status"]?.GetValue<string>() is { } status && status != "OK") { entry["status"] = status; continue; }

        // youtube.js deletes AD_KEYS from the top of the player data only, so an ad field anywhere else is reported.
        var top = player.AsObject().Select(p => p.Key).Where(k => adLike.IsMatch(k)).ToList();
        var nested = new SortedSet<string>();
        Walk(player, "", 0, (path, key) =>
        {
            var root = Regex.Match(path, @"^[^.\[]+").Value; // fields inside a removed key go with it
            if (path.Contains('.') && !adKeys.Contains(root) && adLike.IsMatch(key)) nested.Add(path);
        });
        entry["scheduled"] = string.Join(" ", top.Where(adKeys.Contains));
        if (player["auxiliaryUi"]?["messageRenderers"] is JsonObject messages)
            foreach (var (key, _) in messages) seenPopups.Add(key);
        if (top.Any(adKeys.Contains)) anyScheduled++;
        foreach (var k in top) seenTop.Add(k);
        foreach (var n in nested) seenNested.Add(Regex.Replace(n, @"\[\d+\]", "[]"));

        // The watch page's other data (/next): ad boxes, which youtube.js hides by their element names.
        if (data is not null)
            Walk(data, "", 0, (_, key) => { if (key.EndsWith("Renderer", StringComparison.Ordinal) && adLike.IsMatch(key)) seenRenderers.Add(key); });
    }
    var uncoveredTop = seenTop.Where(k => !adKeys.Contains(k)).ToList();
    var uncoveredRenderers = seenRenderers.Where(r => !script.Contains(ElementName(r), StringComparison.Ordinal)).ToList();
    report["adKeysRemoved"] = string.Join(" ", adKeys);
    report["topLevelAdFields"] = string.Join(" ", seenTop);
    report["nestedAdFields"] = new JsonArray(seenNested.Take(60).Select(n => (JsonNode)n).ToArray());
    report["adRenderers"] = string.Join(" ", seenRenderers);
    report["popupsRemoved"] = string.Join(" ", popupKeys);
    report["popups"] = string.Join(" ", seenPopups);
    foreach (var k in seenPopups.Where(k => popupKeys.Contains(k))) notes.Add($"a signed-out client was sent the ad-blocker popup ({k}); youtube.js removes it");
    foreach (var k in seenPopups.Where(k => !popupKeys.Contains(k) && Regex.IsMatch(k, "(?i)enforce|block|adblock|warning")))
        problems.Add($"player data asks for a popup youtube.js doesn't remove and may be an ad-blocker warning: {k}");
    report["uncoveredTopLevelAdFields"] = string.Join(" ", uncoveredTop);
    report["uncoveredAdRenderers"] = string.Join(" ", uncoveredRenderers);
    foreach (var k in uncoveredTop) problems.Add($"player data has a top-level ad field youtube.js doesn't remove: {k}");
    foreach (var r in uncoveredRenderers) notes.Add($"an ad box youtube.js's CSS doesn't name: {r} (<{ElementName(r)}>); check it shows on the page");
    if (anyScheduled == 0) { notes.Add($"none of {ids.Count} videos had ads scheduled for a signed-out HTTP client"); return 2; }
    notes.Add($"{anyScheduled} of {ids.Count} videos had ads scheduled in fields youtube.js removes");
    return 0;
}

// adSlotRenderer -> ytd-ad-slot-renderer (the custom element YouTube draws it with).
static string ElementName(string renderer) => "ytd-" + Regex.Replace(renderer, "([A-Z])", "-$1").ToLowerInvariant();

static void Walk(JsonNode? node, string path, int depth, Action<string, string> visit)
{
    if (depth > 40) return;
    if (node is JsonObject o)
        foreach (var (key, value) in o) { var p = path.Length == 0 ? key : path + "." + key; visit(p, key); Walk(value, p, depth + 1, visit); }
    else if (node is JsonArray a)
        for (var i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]", depth + 1, visit);
}

// The JSON value assigned after `marker` in a page's inline script.
static JsonNode? JsonAfter(string page, string marker)
{
    var at = page.IndexOf(marker, StringComparison.Ordinal);
    if (at < 0) return null;
    var start = page.IndexOf('{', at);
    if (start < 0) return null;
    var bytes = Encoding.UTF8.GetBytes(page[start..]);
    var reader = new Utf8JsonReader(bytes);
    try { reader.Read(); reader.Skip(); return JsonNode.Parse(bytes.AsSpan(0, (int)reader.BytesConsumed)); }
    catch (JsonException) { return null; }
}

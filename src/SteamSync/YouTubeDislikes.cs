using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SteamSync;

/// <summary>
/// Dislike counts for the YouTube window (Scripts/youtube-dislikes.js asks for them), from Return YouTube Dislike's
/// public API (returnyoutubedislikeapi.com). Its terms: credit the project (the count's tooltip and the README do), and
/// at most 100 calls a minute and 10,000 a day per client. Counts are cached for 10 minutes, and calls past the limits
/// are refused here rather than sent. Only the video's ID is sent.
/// </summary>
internal static partial class YouTubeDislikes
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, (long Dislikes, DateTime At)> Cache = [];
    private static readonly Queue<DateTime> Calls = new();
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
    private static int _today;
    private static DateOnly _day;
    private static bool _failureLogged;

    /// <summary>The page script, added to every YouTube document.</summary>
    public static string Script { get; } = LoadScript();

    [GeneratedRegex(@"^[\w-]{11}$")]
    private static partial Regex VideoId();

    /// <summary>The video's estimated dislikes, or null when it isn't known (bad ID, over the limits, API down).</summary>
    public static async Task<long?> GetAsync(string id)
    {
        if (!VideoId().IsMatch(id)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var hit) && DateTime.UtcNow - hit.At < Keep) return hit.Dislikes;
            if (!TakeCall()) return null;
        }
        try
        {
            var json = JsonNode.Parse(await Http.GetStringAsync($"https://returnyoutubedislikeapi.com/votes?videoId={id}"));
            var dislikes = json?["dislikes"]?.GetValue<long>();
            if (dislikes is null) return null;
            lock (Cache)
            {
                if (Cache.Count > 500) Cache.Clear();
                Cache[id] = (dislikes.Value, DateTime.UtcNow);
            }
            return dislikes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            if (!_failureLogged) { _failureLogged = true; Log.Error("YouTube dislike counts unavailable", ex); }
            return null;
        }
    }

    // The API's per-client limits: 100 a minute, 10,000 a day. Called under the cache lock.
    private static bool TakeCall()
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        if (today != _day) { _day = today; _today = 0; }
        while (Calls.Count > 0 && now - Calls.Peek() > TimeSpan.FromMinutes(1)) Calls.Dequeue();
        if (Calls.Count >= 100 || _today >= 10_000) return false;
        Calls.Enqueue(now);
        _today++;
        return true;
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GamingEnjoyer/{AppInfo.Version}");
        return http;
    }

    private static string LoadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamSync.youtube-dislikes.js")!;
        return new StreamReader(stream).ReadToEnd();
    }
}

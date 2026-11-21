using AdVoid;
using AdVoid.Twitch;

namespace SteamSync;

/// <summary>
/// The app's ad blocker: AdVoid (the user's own engine, C:\Repos\advoid) with EasyList and EasyPrivacy, shared by every
/// window that browses other sites (the YouTube and Twitch windows), plus AdVoid's Twitch stream ad remover. The lists live in %LocalAppData%\SteamSync\AdVoid; they're
/// read on a worker thread when first needed and refreshed from their publishers when 4 days old (checked at start
/// and every 12 hours). Until the first download, AdVoid's own YouTube ad remover and a few big ad hosts still apply.
/// </summary>
internal static class AdBlocker
{
    private static readonly FilterListStore Store = new(Path.Combine(AppInfo.DataDir, "AdVoid"), FilterListStore.Default);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(4);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(12);

    // The big ad networks, so the first minutes before EasyList arrives aren't wide open.
    private static readonly string[] Starter =
    [
        "||doubleclick.net^", "||googlesyndication.com^", "||googleadservices.com^", "||adservice.google.com^",
        "||youtube.com/pagead/", "||youtube.com/api/stats/ads", "||youtube.com/ptracking", "||amazon-adsystem.com^",
    ];

    private static volatile FilterEngine _engine = FilterEngine.Build(Starter);
    private static Task? _loading;
    private static System.Threading.Timer? _timer;
    private static long _blocked;

    private static readonly HttpClient TwitchHttp = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = System.Net.DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Twitch's stream ads: the Twitch window routes the stream's playlists through this.</summary>
    public static readonly TwitchAds Twitch = new(TwitchHttp) { Log = line => SteamSync.Log.Info(line) };

    /// <summary>The current engine (swapped whole when lists change, so readers never see a half-built one).</summary>
    public static FilterEngine Engine => _engine;

    /// <summary>Requests blocked since the app started, across windows.</summary>
    public static long Blocked => Interlocked.Read(ref _blocked);

    public static void CountBlocked() => Interlocked.Increment(ref _blocked);

    /// <summary>Loads the cached lists and starts the refresh timer, once; returns at once (the work is on a worker thread).</summary>
    public static void EnsureStarted()
    {
        if (_loading is not null) return;
        _loading = Task.Run(async () =>
        {
            Load("cached");
            await RefreshAsync();
        });
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, CheckEvery, CheckEvery);
    }

    private static async Task RefreshAsync()
    {
        try
        {
            if (!Store.NeedsUpdate(RefreshAfter)) return;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"GamingEnjoyer-AdVoid/{AppInfo.Version}");
            if (await Store.UpdateAsync(http, RefreshAfter) > 0) Load("updated");
        }
        catch (Exception ex) { Log.Error("AdVoid: updating the filter lists failed", ex); }
    }

    private static void Load(string why)
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var engine = FilterEngine.Build(Starter.Concat(Store.ReadLines()));
            if (engine.Counts.Network <= Starter.Length) return; // nothing cached yet
            _engine = engine;
            Log.Info($"AdVoid: {why} filter lists loaded in {watch.ElapsedMilliseconds} ms ({engine.Counts.Network} blocking, {engine.Counts.Cosmetic} hiding, {engine.Counts.Skipped} skipped)");
        }
        catch (Exception ex) { Log.Error("AdVoid: reading the filter lists failed", ex); }
    }
}

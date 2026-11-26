using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WatchCheck;

// WatchCheck: plays YouTube videos or a Twitch stream in the app's own WatchWindow (the code people run, AdVoid and
// all) and watches for ads, stalls and black pictures. The window is muted and keeps its data (profile, filter lists,
// log) in its own folder, never the installed app's. AdVoid's nightly check runs it (advoid/tools/nightly-check.ps1).
//
//   WatchCheck youtube [seconds per video]     finds a few popular videos and plays each (default 40 s)
//   WatchCheck twitch <channel> [seconds]      plays the channel (default 180 s)
//
// Prints a JSON report. Exit code: 0 fine, 1 an ad showed, playback stalled or the picture was black, 2 nothing wrong
// but nothing was proven either (no ad came up, or the site asked for a sign-in or a bot check).
internal static class Program
{
    private static readonly JsonObject Report = new();
    private static readonly JsonArray Problems = [];
    private static readonly JsonArray Notes = [];
    private static CoreWebView2 _core = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        // Before any app type is touched: AppInfo.DataDir reads it once.
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GamingEnjoyer-WatchCheck");
        Environment.SetEnvironmentVariable("GAMING_ENJOYER_DATA_DIR", data);
        Report["check"] = string.Join(" ", args);
        Report["at"] = DateTimeOffset.Now.ToString("o");
        Report["problems"] = Problems;
        Report["notes"] = Notes;

        var exit = 1;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var site = args.FirstOrDefault() == "twitch" ? SteamSync.WatchSite.Twitch : SteamSync.WatchSite.YouTube;
        var twitchLog = new List<string>();
        SteamSync.AdBlocker.Twitch.Log = line => { lock (twitchLog) twitchLog.Add(line); SteamSync.Log.Info(line); };
        var win = new SteamSync.WatchWindow(site) { Size = new Size(1280, 800) };
        win.Shown += async (_, _) =>
        {
            try
            {
                var web = win.Controls.OfType<WebView2>().First();
                for (var i = 0; i < 150 && web.CoreWebView2 is null; i++) await Task.Delay(200);
                _core = web.CoreWebView2 ?? throw new InvalidOperationException("the window's browser didn't start");
                _core.IsMuted = true;
                exit = args.FirstOrDefault() switch
                {
                    "youtube" => await YouTube(args.Length > 1 ? int.Parse(args[1]) : 40),
                    "twitch" when args.Length > 1 => await Twitch(args[1], args.Length > 2 ? int.Parse(args[2]) : 180, twitchLog),
                    _ => throw new ArgumentException("usage: WatchCheck youtube [seconds] | twitch <channel> [seconds]"),
                };
            }
            catch (Exception ex) { Problems.Add($"the check itself failed: {ex.GetType().Name}: {ex.Message}"); exit = 1; }
            win.Close();
        };
        win.FormClosed += (_, _) => Application.ExitThread();
        Application.Run(win);

        if (Problems.Count > 0) exit = 1;
        Report["result"] = exit switch { 0 => "ok", 2 => "inconclusive", _ => "problems" };
        Console.WriteLine(Report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return exit;
    }

    // ------------------------------------------------------------ YouTube

    private static async Task<int> YouTube(int seconds)
    {
        var urls = new List<string>();
        foreach (var q in new[] { "official music video", "game trailer 2026" })
        {
            await Navigate("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q));
            await Task.Delay(4000);
            var found = await Js("[...document.querySelectorAll('ytd-search a[href^=\"/watch?v=\"]')].map(a => a.href)");
            urls.AddRange(found?.AsArray().Select(n => n!.GetValue<string>().Split('&')[0]).Distinct().Take(3) ?? []);
        }
        if (urls.Count == 0) { Problems.Add("YouTube's search page listed no videos"); return 1; }

        var videos = new JsonArray();
        Report["videos"] = videos;
        var proven = 0;
        foreach (var url in urls)
        {
            await Navigate(url);
            var entry = new JsonObject { ["url"] = url };
            videos.Add(entry);
            double adSeconds = 0, streak = 0, longest = 0, firstT = -1, lastT = -1;
            int frames = 0, black = 0, removed = 0, popups = 0;
            double popupSeconds = 0;
            var bot = false; var error = false;
            for (var tick = 0; tick < seconds * 2; tick++)
            {
                await Task.Delay(500);
                var s = await Js("""
                    (() => { const p = document.querySelector('#movie_player'); const v = p && p.querySelector('video');
                      const text = document.body ? document.body.innerText : '';
                      if (v && v.paused && v.currentTime < 1) v.play().catch(() => {});
                      return { ad: !!(p && p.classList.contains('ad-showing')), t: v ? v.currentTime : -1,
                               stats: window[Symbol.for('advoid.youtube')]?.() ?? { removed: -1, popups: -1 },
                               popup: [...document.querySelectorAll('ytd-enforcement-message-view-model')].some(e => e.getClientRects().length > 0),
                               error: !!document.querySelector('.ytp-error'),
                               bot: /confirm you.re not a bot|Sign in to confirm/.test(text) }; })()
                    """);
                if (s is null) continue;
                var ad = s["ad"]!.GetValue<bool>();
                adSeconds += ad ? 0.5 : 0;
                streak = ad ? streak + 0.5 : 0;
                longest = Math.Max(longest, streak);
                var t = s["t"]!.GetValue<double>();
                if (t >= 0) { if (firstT < 0) firstT = t; lastT = t; }
                removed = Math.Max(removed, s["stats"]!["removed"]!.GetValue<int>());
                popups = Math.Max(popups, s["stats"]!["popups"]!.GetValue<int>());
                if (s["popup"]!.GetValue<bool>()) popupSeconds += 0.5;
                bot |= s["bot"]!.GetValue<bool>();
                error |= s["error"]!.GetValue<bool>();
                if (tick % 20 == 19 && !ad && await IsBlack() is { } isBlack) { frames++; if (isBlack) black++; }
            }
            var played = lastT - Math.Max(firstT, 0);
            entry["adSeconds"] = adSeconds;
            entry["played"] = Math.Round(played, 1);
            entry["removed"] = removed;
            if (popups > 0) entry["popupsRemoved"] = popups;
            entry["blackFrames"] = $"{black}/{frames}";
            if (bot) { entry["status"] = "bot check"; Notes.Add($"{url}: YouTube asked for a sign-in or bot check"); continue; }
            if (error) entry["status"] = "player error";
            if (longest >= 3) Problems.Add($"{url}: an ad played for {longest:0.#} s (youtube.js didn't remove it and the fallback didn't skip it)");
            else if (adSeconds > 0) Notes.Add($"{url}: an ad started and the fallback skipped it in {adSeconds:0.#} s: the player data still had an ad youtube.js didn't remove");
            if (played < 5) Problems.Add($"{url}: the video didn't play ({played:0.#} s in {seconds} s){(error ? ", player error" : "")}");
            if (frames >= 2 && black >= 2) Problems.Add($"{url}: the picture was black in {black} of {frames} frames");
            if (popupSeconds >= 1) Problems.Add($"{url}: YouTube's \"ad blockers are not allowed\" popup was on screen for {popupSeconds:0.#} s");
            else if (popups > 0) Notes.Add($"{url}: YouTube sent its ad-blocker popup {popups} time(s); AdVoid removed it: YouTube has flagged this profile");
            if (removed > 0) proven++;
        }
        await Stealth();
        await PopupFallback();
        if (Problems.Count > 0) return 1;
        if (proven == 0) { Notes.Add("no video had an ad scheduled, so removing one wasn't tested"); return 2; }
        Notes.Add($"{proven} of {urls.Count} videos had ads scheduled and removed; all played");
        return 0;
    }

    // YouTube looks for ad blockers by how the page's built-in functions look: youtube.js's hooks must read exactly
    // like the originals, and nothing on the page may be named after AdVoid.
    private static async Task Stealth()
    {
        var r = await Js("""
            (() => { const ts = Function.prototype.toString; const bad = [];
              const want = { 'JSON.parse': [JSON.parse, 'parse'], 'Response.prototype.json': [Response.prototype.json, 'json'],
                             'Promise.prototype.then': [Promise.prototype.then, 'then'], 'Function.prototype.toString': [ts, 'toString'] };
              for (const [label, [fn, name]] of Object.entries(want))
                if (ts.call(fn) !== 'function ' + name + '() { [native code] }') bad.push(label + ' shows "' + ts.call(fn).slice(0, 60) + '"');
              if (Object.getOwnPropertyNames(window).some(k => /advoid/i.test(k))) bad.push('a global is named after AdVoid');
              return bad; })()
            """);
        if (r is not JsonArray bad) { Notes.Add("couldn't run the stealth check"); return; }
        Report["stealth"] = bad.Count == 0 ? "ok" : string.Join("; ", bad.Select(b => (string)b!));
        foreach (var b in bad) Problems.Add($"youtube.js can be spotted: {b}");
    }

    // Signed-out profiles rarely get YouTube's ad-blocker popup, so it's staged: a copy of its dialog is put on the
    // page over a paused video, and youtube.js must take it away and let the video play.
    private static async Task PopupFallback()
    {
        await Js("""
            (() => { const d = document.createElement('tp-yt-paper-dialog'); d.id = 'watchcheck-popup';
              d.appendChild(document.createElement('ytd-enforcement-message-view-model')); document.body.appendChild(d);
              document.querySelector('#movie_player video')?.pause(); return true; })()
            """);
        await Task.Delay(2000);
        var r = await Js("""
            (() => { const v = document.querySelector('#movie_player video');
              return { gone: !document.getElementById('watchcheck-popup'), playing: !!v && !v.paused }; })()
            """);
        var gone = r?["gone"]?.GetValue<bool>() == true;
        var playing = r?["playing"]?.GetValue<bool>() == true;
        Report["popupFallback"] = gone && playing ? "ok" : $"gone={gone} playing={playing}";
        if (!gone) Problems.Add("a staged ad-blocker popup stayed on the page (youtube.js's fallback didn't remove it)");
        else if (!playing) Problems.Add("youtube.js removed a staged ad-blocker popup but the video stayed paused");
    }

    // ------------------------------------------------------------ Twitch

    private static async Task<int> Twitch(string channel, int seconds, List<string> log)
    {
        long replaced0 = SteamSync.AdBlocker.Twitch.Replaced, stripped0 = SteamSync.AdBlocker.Twitch.Stripped;
        await Navigate("https://www.twitch.tv/" + channel);
        double labelSeconds = 0, stall = 0, longestStall = 0, lastT = -1, played = 0;
        int frames = 0, black = 0;
        var gated = false;
        for (var tick = 0; tick < seconds; tick++)
        {
            await Task.Delay(1000);
            var s = await Js("""
                (() => { document.querySelector('[data-a-target="content-classification-gate-overlay-start-watching-button"], [data-a-target="player-overlay-mature-accept"]')?.click();
                  const v = [...document.querySelectorAll('video')].sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight)[0];
                  return { t: v ? v.currentTime : -1, paused: v ? v.paused : true, h: v ? v.videoHeight : 0,
                           label: !!document.querySelector('[data-a-target="video-ad-label"], [data-a-target="video-ad-countdown"]'),
                           gate: !!document.querySelector('[data-a-target="content-classification-gate-overlay"]') }; })()
                """);
            if (s is null) continue;
            gated |= s["gate"]!.GetValue<bool>();
            if (s["label"]!.GetValue<bool>()) labelSeconds++;
            var t = s["t"]!.GetValue<double>();
            if (t >= 0 && lastT >= 0 && t > lastT) { played += t - lastT; stall = 0; }
            else if (tick >= 20 && !s["paused"]!.GetValue<bool>()) { stall++; longestStall = Math.Max(longestStall, stall); }
            if (t >= 0) lastT = t;
            if (tick % 10 == 9 && await IsBlack() is { } isBlack) { frames++; if (isBlack) black++; }
        }
        var replaced = SteamSync.AdBlocker.Twitch.Replaced - replaced0;
        var stripped = SteamSync.AdBlocker.Twitch.Stripped - stripped0;
        Report["channel"] = channel;
        Report["played"] = Math.Round(played);
        Report["longestStall"] = longestStall;
        Report["adLabelSeconds"] = labelSeconds;
        Report["blackFrames"] = $"{black}/{frames}";
        Report["playlistsReplaced"] = replaced;
        Report["playlistsStripped"] = stripped;
        lock (log) Report["log"] = new JsonArray(log.Select(l => (JsonNode)l).ToArray());

        if (labelSeconds >= 2) Problems.Add($"Twitch showed its ad label for {labelSeconds} s");
        if (longestStall >= 10) Problems.Add($"the stream stalled for {longestStall} s");
        if (played < seconds / 3.0) Problems.Add($"the stream played only {played:0} s of {seconds} s{(gated ? " (age gate)" : "")}");
        if (frames >= 3 && black * 2 >= frames) Problems.Add($"the picture was black in {black} of {frames} frames");
        if (Problems.Count > 0) return 1;
        if (replaced + stripped == 0) { Notes.Add("no ad break came up while watching, so skipping one wasn't tested"); return 2; }
        if (stripped > 0 && replaced == 0) Notes.Add("the ad was cut out (no ad-free copy in the stream's codec) rather than swapped");
        return 0;
    }

    // ------------------------------------------------------------ helpers

    private static async Task<JsonNode?> Js(string script)
    {
        try { return JsonNode.Parse(await _core.ExecuteScriptAsync(script)); }
        catch (Exception) { return null; } // mid-navigation
    }

    private static async Task Navigate(string url)
    {
        var done = new TaskCompletionSource();
        void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult();
        _core.NavigationCompleted += OnDone;
        _core.Navigate(url);
        await Task.WhenAny(done.Task, Task.Delay(30_000));
        _core.NavigationCompleted -= OnDone;
        await Task.Delay(2000);
    }

    // Whether the largest video on the page is showing a black (or blank) picture: a grid of pixels from a capture of
    // the window, all dark and alike. Null when there's no video on screen to look at.
    private static async Task<bool?> IsBlack()
    {
        var rect = await Js("""
            (() => { const v = [...document.querySelectorAll('video')].sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight)[0];
              if (!v || v.clientWidth < 100) return null; const r = v.getBoundingClientRect();
              return [r.left, r.top, r.width, r.height, innerWidth]; })()
            """);
        if (rect is not JsonArray a) return null;
        using var png = new MemoryStream();
        await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
        png.Position = 0;
        using var bmp = new Bitmap(png);
        var scale = bmp.Width / a[4]!.GetValue<double>();
        double x = a[0]!.GetValue<double>() * scale, y = a[1]!.GetValue<double>() * scale, w = a[2]!.GetValue<double>() * scale, h = a[3]!.GetValue<double>() * scale;
        var values = new List<double>();
        for (var i = 1; i < 20; i++)
            for (var j = 1; j < 12; j++)
            {
                int px = (int)(x + w * i / 20), py = (int)(y + h * j / 12);
                if (px < 0 || py < 0 || px >= bmp.Width || py >= bmp.Height) continue;
                var c = bmp.GetPixel(px, py);
                values.Add(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B);
            }
        if (values.Count < 50) return null;
        var mean = values.Average();
        var spread = Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        return mean < 12 && spread < 6;
    }
}

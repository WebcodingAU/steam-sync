using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdVoid;
using AdVoid.Twitch;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace SteamSync;

/// <summary>A site the tray can open in its own window: YouTube or Twitch.</summary>
/// <param name="Sites">Registrable domains that stay in the window; any other link opens in the default browser.</param>
/// <param name="GoogleSignIn">Google's sign-in and consent pages stay in the window too (YouTube accounts).</param>
/// <param name="TwitchStreams">Live stream playlists go through AdVoid's Twitch ad remover.</param>
/// <param name="Dislikes">Shows dislike counts (Return YouTube Dislike's estimates; YouTubeDislikes.cs).</param>
internal sealed record WatchSite(string Name, string Home, Color Background, string[] Sites, bool GoogleSignIn, bool TwitchStreams, bool Dislikes = false)
{
    public static readonly WatchSite YouTube = new("YouTube", "https://www.youtube.com/", Color.FromArgb(15, 15, 15),
        ["youtube.com", "youtu.be", "youtube-nocookie.com", "ytimg.com", "googlevideo.com"], GoogleSignIn: true, TwitchStreams: false, Dislikes: true);

    public static readonly WatchSite Twitch = new("Twitch", "https://www.twitch.tv/", Color.FromArgb(14, 14, 16),
        ["twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net", "twitchsvc.net"], GoogleSignIn: false, TwitchStreams: true);

    public bool IsInApp(Uri uri)
    {
        var site = Domains.Registrable(uri.Host.ToLowerInvariant());
        return Sites.Contains(site)
            || GoogleSignIn && site.StartsWith("google.", StringComparison.Ordinal); // sign-in and consent pages (google.com, google.com.au, ...)
    }
}

/// <summary>
/// A YouTube or Twitch window (the tray's "Open YouTube" / "Open Twitch"), for guides and streams on a second monitor
/// without a browser open. Each site has its own WebView2 profile (its own sign-in) and blocks ads with AdVoid: every
/// request is checked by the engine (WebResourceRequested), each page gets its element-hiding CSS, AdVoid's YouTube
/// script removes the video ads YouTube schedules inside the video's own data, and on Twitch the stream's playlists go
/// through AdVoid's Twitch ad remover, which swaps in an ad-free copy of the stream during ad breaks. Only the site's
/// own pages open here; other links go to the default browser. It remembers where it was (the second monitor the first
/// time there is one), and the title bar's menu has "Keep on top". Closing it ends its browser engine.
/// </summary>
internal sealed class WatchWindow : Form
{
    private const string BrowserArguments = "--disable-sync --no-first-run --disable-default-apps "
        + "--disable-features=Translate,OptimizationHints,MediaRouter,msSmartScreenProtection,msEdgeShoppingAssistant,msShowSearchInSidebar";

    private readonly WatchSite _site;
    private readonly string _profileDir;
    private readonly string _boundsFile;
    private readonly WebView2 _web;
    private readonly System.Windows.Forms.Timer _titleTimer = new() { Interval = 1000 };
    private string _pageTitle;
    private string _page;                          // the top page, for third-party checks and $domain= options
    private string? _navigatingTo;                 // the top-level document being loaded (never blocked)
    private long _blockedHere;
    private int _adBreaksHere;                     // Twitch ad breaks skipped in this window
    private bool _inAdBreak;
    private bool _playlistErrorLogged;
    private bool _fullScreen;
    private FormWindowState _stateBeforeFullScreen;

    // Open windows: while any is, the tray process stays out of Efficiency mode (see Efficiency.Held).
    private static int _openCount;

    public WatchWindow(WatchSite site)
    {
        _site = site;
        _profileDir = Path.Combine(AppInfo.DataDir, "WebView2-" + site.Name);
        _boundsFile = Path.Combine(AppInfo.DataDir, site.Name.ToLowerInvariant() + "-window.json");
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = site.Background };
        _pageTitle = site.Name;
        _page = site.Home;
        Text = site.Name;
        Icon = AppInfo.LoadIcon();
        BackColor = site.Background;
        MinimumSize = new Size(400, 260);
        StartPosition = FormStartPosition.Manual;
        LoadBounds();
        Controls.Add(_web);
        _web.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11 && e.Modifiers == Keys.None) { e.Handled = true; SetFullScreen(!_fullScreen); }
        };
        _titleTimer.Tick += (_, _) => UpdateTitle();
        Load += async (_, _) => await InitAsync();
        FormClosing += (_, _) => SaveBounds();
    }

    // ---- the browser ----

    private async Task InitAsync()
    {
        _openCount++;
        Efficiency.Held = true;
        Efficiency.SetSelf(false); // the engine's request checks run on this process's UI thread: never at idle priority
        AdBlocker.EnsureStarted();
        try
        {
            var options = new CoreWebView2EnvironmentOptions(BrowserArguments) { AreBrowserExtensionsEnabled = false };
            var env = await CoreWebView2Environment.CreateAsync(null, _profileDir, options);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Log.Error($"{_site.Name} window could not start", ex);
            MessageBox.Show(this, $"The {_site.Name} window could not start: {ex.Message}", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsReputationCheckingRequired = false;
        core.Settings.IsSwipeNavigationEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.NavigationStarting += OnNavigationStarting;
        core.SourceChanged += (_, _) => { _page = core.Source; _navigatingTo = null; _inAdBreak = false; }; // a new channel starts its own break, if any
        core.NewWindowRequested += OnNewWindowRequested;
        core.DocumentTitleChanged += (_, _) => { _pageTitle = string.IsNullOrWhiteSpace(core.DocumentTitle) ? _site.Name : core.DocumentTitle; UpdateTitle(); };
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
        core.WebMessageReceived += OnWebMessage;

        // AdVoid: its page scripts before the page's own, the element hiding asked for by each top-level page, and
        // every request (from pages, workers and service workers) checked by the engine.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Scriptlets.All);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(CosmeticLoader);
        if (_site.Dislikes) await core.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeDislikes.Script);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;

        Log.Info($"{_site.Name} window opened (WebView2 {core.Environment.BrowserVersionString})");
        _titleTimer.Start();
        core.Navigate(_site.Home);
    }

    // Asks the host for the page's hiding CSS once the document exists, and adds it as a stylesheet. Also hides the
    // page's scrollbar (it still scrolls): the user doesn't want visible scrollbars.
    private const string CosmeticLoader = """
        (() => {
          if (window.top !== window || !window.chrome?.webview) return;
          const addBar = () => {
            const bar = document.createElement("style");
            bar.textContent = "html { scrollbar-width: none !important; } html::-webkit-scrollbar { display: none !important; }";
            (document.head || document.documentElement).appendChild(bar);
          };
          if (document.documentElement) addBar(); else document.addEventListener("readystatechange", addBar, { once: true });
          const onMessage = (e) => {
            if (e.data?.type !== "advoid-css") return;
            chrome.webview.removeEventListener("message", onMessage);
            if (!e.data.css) return;
            const style = document.createElement("style");
            style.id = "advoid";
            style.textContent = e.data.css;
            (document.head || document.documentElement).appendChild(style);
          };
          chrome.webview.addEventListener("message", onMessage);
          chrome.webview.postMessage({ type: "advoid-css" });
        })();
        """;

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject? message;
        try { message = JsonNode.Parse(e.WebMessageAsJson) as JsonObject; }
        catch (JsonException) { return; }
        if (_site.Dislikes && (string?)message?["type"] == "dislikes" && message["id"]?.GetValueKind() == JsonValueKind.String)
        {
            _ = AnswerDislikesAsync((string)message["id"]!);
            return;
        }
        if ((string?)message?["type"] != "advoid-css") return;
        var css = AdBlocker.Engine.Cosmetics(e.Source);
        try { _web.CoreWebView2.PostWebMessageAsJson(new JsonObject { ["type"] = "advoid-css", ["css"] = css }.ToJsonString()); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { }
    }

    private async Task AnswerDislikesAsync(string id)
    {
        var dislikes = await YouTubeDislikes.GetAsync(id);
        var reply = new JsonObject { ["type"] = "dislikes", ["id"] = id, ["dislikes"] = dislikes };
        try { _web.CoreWebView2?.PostWebMessageAsJson(reply.ToJsonString()); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { } // the window closed meanwhile
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var url = e.Request.Uri;
        if (_site.TwitchStreams)
        {
            if (e.Request.Method == "GET" && AdBlocker.Twitch.Kind(url) != PlaylistKind.None)
            {
                _ = AnswerPlaylistAsync(e, url);
                return;
            }
            // The page's own Twitch session (when signed in), so ad-free copies come in the viewer's qualities and
            // codecs. AdVoid keeps it in memory, sends it only to gql.twitch.tv and never logs it.
            if (url.StartsWith("https://gql.twitch.tv/", StringComparison.Ordinal) && e.Request.Headers.Contains("Authorization"))
                AdBlocker.Twitch.SetViewerHeaders(TwitchAds.ViewerHeaderNames
                    .Where(e.Request.Headers.Contains)
                    .Select(name => new KeyValuePair<string, string>(name, e.Request.Headers.GetHeader(name))));
        }
        var type = e.ResourceContext switch
        {
            // The page being opened is never blocked; any other document is an iframe.
            CoreWebView2WebResourceContext.Document => url == _navigatingTo ? RequestType.Document : RequestType.Subdocument,
            CoreWebView2WebResourceContext.Script => RequestType.Script,
            CoreWebView2WebResourceContext.Stylesheet => RequestType.Stylesheet,
            CoreWebView2WebResourceContext.Image => RequestType.Image,
            CoreWebView2WebResourceContext.Media => RequestType.Media,
            CoreWebView2WebResourceContext.Font => RequestType.Font,
            CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Fetch or CoreWebView2WebResourceContext.EventSource => RequestType.Xhr,
            CoreWebView2WebResourceContext.Websocket => RequestType.WebSocket,
            CoreWebView2WebResourceContext.Ping or CoreWebView2WebResourceContext.CspViolationReport => RequestType.Ping,
            _ => RequestType.Other,
        };
        if (type == RequestType.Document) return;
        var decision = AdBlocker.Engine.Match(new Request(url, _navigatingTo ?? _page, type));
        if (!decision.Blocked) return;
        e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked by AdVoid", "Content-Type: text/plain");
        _blockedHere++;
        AdBlocker.CountBlocked();
    }

    // A Twitch stream playlist: fetched by AdVoid's Twitch ad remover, which answers with the playlist as it is, or with
    // an ad-free copy (or the ad cut out) during an ad break. If fetching fails, the request goes ahead normally.
    private async Task AnswerPlaylistAsync(CoreWebView2WebResourceRequestedEventArgs e, string url)
    {
        using var deferral = e.GetDeferral();
        try
        {
            var headers = e.Request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToList();
            var answer = await AdBlocker.Twitch.FetchAsync(url, headers);
            if (IsDisposed || _web.CoreWebView2 is null) return;
            var body = new MemoryStream(Encoding.UTF8.GetBytes(answer.Body));
            var headerText = string.Join("\r\n", answer.Headers.Select(h => $"{h.Key}: {h.Value}"));
            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(body, answer.Status, answer.Reason, headerText);
            if (AdBlocker.Twitch.Kind(url) != PlaylistKind.Media) return;
            var inBreak = answer.Action is PlaylistAction.Replaced or PlaylistAction.Stripped; // not Continued: the copy after a break
            if (inBreak && !_inAdBreak) _adBreaksHere++;
            _inAdBreak = inBreak;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or COMException or InvalidOperationException)
        {
            if (!_playlistErrorLogged) Log.Error("Twitch: fetching a stream playlist failed; letting it through", ex);
            _playlistErrorLogged = true;
        }
        finally { deferral.Complete(); }
    }

    private void UpdateTitle()
    {
        var parts = new List<string> { _pageTitle };
        if (_blockedHere > 0) parts.Add($"AdVoid blocked {_blockedHere:N0}");
        if (_inAdBreak) parts.Add("skipping an ad break");
        else if (_adBreaksHere > 0) parts.Add(_adBreaksHere == 1 ? "1 ad break skipped" : $"{_adBreaksHere} ad breaks skipped");
        var text = string.Join(" · ", parts);
        if (Text != text) Text = text;
    }

    // ---- what stays in the window ----

    private bool IsInApp(Uri uri) => _site.IsInApp(uri);

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            if (!e.Uri.StartsWith("about:", StringComparison.Ordinal)) e.Cancel = true;
            return;
        }
        if (IsInApp(uri)) { _navigatingTo = e.Uri; return; }
        e.Cancel = true;
        OpenExternally(uri.AbsoluteUri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // never a second browser window: the site's links open here, anything else in the default browser
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && IsInApp(uri)) _web.CoreWebView2.Navigate(uri.AbsoluteUri);
        else if (uri is not null && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)) OpenExternally(uri.AbsoluteUri);
    }

    private static void OpenExternally(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open {url}", ex); }
    }

    // ---- window: full screen, keep on top, dark title bar, where it was ----

    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;
        if (on)
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _stateBeforeFullScreen;
            if (_web.CoreWebView2?.ContainsFullScreenElement == true)
                _ = _web.CoreWebView2.ExecuteScriptAsync("document.exitFullscreen && document.exitFullscreen()");
        }
    }

    private const int WmSysCommand = 0x0112, KeepOnTopId = 0x1F10;
    private const uint MfString = 0x0, MfSeparator = 0x800, MfChecked = 0x8, MfUnchecked = 0x0;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar when Windows' apps are dark (YouTube and Twitch follow it too).
        var dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        var on = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
        // "Keep on top" in the title bar's menu (right-click the title bar, or Alt+Space).
        var menu = GetSystemMenu(Handle, false);
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString | (TopMost ? MfChecked : MfUnchecked), KeepOnTopId, "Keep on top");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmSysCommand && ((int)m.WParam & 0xFFF0) == KeepOnTopId)
        {
            TopMost = !TopMost;
            CheckMenuItem(GetSystemMenu(Handle, false), KeepOnTopId, TopMost ? MfChecked : MfUnchecked);
            SaveBounds();
            return;
        }
        base.WndProc(ref m);
    }

    private sealed record SavedBounds(int X, int Y, int Width, int Height, bool Maximized, bool TopMost);

    private void LoadBounds()
    {
        SavedBounds? saved = null;
        try { if (File.Exists(_boundsFile)) saved = JsonSerializer.Deserialize<SavedBounds>(File.ReadAllText(_boundsFile)); }
        catch (Exception ex) when (ex is IOException or JsonException) { }

        var bounds = saved is null ? Rectangle.Empty : new Rectangle(saved.X, saved.Y, Math.Max(saved.Width, 400), Math.Max(saved.Height, 260));
        if (saved is null || !Screen.AllScreens.Any(s => Rectangle.Intersect(s.WorkingArea, bounds).Width >= 200))
        {
            // First time (or its screen is gone): the second monitor when there is one, else the main one.
            var screen = Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen!;
            var area = screen.WorkingArea;
            var size = new Size(Math.Min(1280, area.Width - 80), Math.Min(760, area.Height - 80));
            bounds = new Rectangle(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height);
        }
        Bounds = bounds;
        if (saved?.Maximized == true) WindowState = FormWindowState.Maximized;
        TopMost = saved?.TopMost == true;
    }

    private void SaveBounds()
    {
        if (_fullScreen) return;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        try { File.WriteAllText(_boundsFile, JsonSerializer.Serialize(new SavedBounds(b.X, b.Y, b.Width, b.Height, WindowState == FormWindowState.Maximized, TopMost))); }
        catch (IOException) { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _titleTimer.Dispose();
        _web.Dispose(); // ends this window's browser processes
        _openCount = Math.Max(0, _openCount - 1);
        if (_openCount == 0)
        {
            Efficiency.Held = false;
            Efficiency.SetSelf(true);
        }
        var breaks = _site.TwitchStreams ? $", {_adBreaksHere} ad break(s) skipped" : "";
        Log.Info($"{_site.Name} window closed; AdVoid blocked {_blockedHere:N0} request(s) in it{breaks}");
        base.OnFormClosed(e);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);
    [DllImport("user32.dll")] private static extern uint CheckMenuItem(IntPtr menu, int id, uint check);
}

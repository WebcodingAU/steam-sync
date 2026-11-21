using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdVoid;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace SteamSync;

/// <summary>
/// A YouTube window (the tray's "Open YouTube"), for guides on a second monitor without a browser open. It has its own
/// WebView2 profile (its own YouTube sign-in) and blocks ads with AdVoid: every request is checked by the engine
/// (WebResourceRequested), each page gets its element-hiding CSS, and AdVoid's YouTube script removes the video ads
/// YouTube schedules inside the video's own data. Only YouTube and Google's sign-in pages open here; other links go
/// to the default browser. It remembers where it was (the second monitor the first time there is one), and the
/// title bar's menu has "Keep on top". Closing it ends its browser engine.
/// </summary>
internal sealed class YouTubeWindow : Form
{
    private static readonly string ProfileDir = Path.Combine(AppInfo.DataDir, "WebView2-YouTube");
    private static readonly string BoundsFile = Path.Combine(AppInfo.DataDir, "youtube-window.json");
    private static readonly Uri Home = new("https://www.youtube.com/");
    private const string BrowserArguments = "--disable-sync --no-first-run --disable-default-apps "
        + "--disable-features=Translate,OptimizationHints,MediaRouter,msSmartScreenProtection,msEdgeShoppingAssistant,msShowSearchInSidebar";

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(15, 15, 15) };
    private readonly System.Windows.Forms.Timer _titleTimer = new() { Interval = 1000 };
    private string _pageTitle = "YouTube";
    private string _page = Home.AbsoluteUri;       // the top page, for third-party checks and $domain= options
    private string? _navigatingTo;                 // the top-level document being loaded (never blocked)
    private long _blockedHere;
    private bool _fullScreen;
    private FormWindowState _stateBeforeFullScreen;

    private static bool _open;
    /// <summary>A YouTube window is open (the tray process stays out of Efficiency mode meanwhile, see Efficiency.Held).</summary>
    public static bool IsOpen => _open;

    public YouTubeWindow()
    {
        Text = "YouTube";
        Icon = AppInfo.LoadIcon();
        BackColor = Color.FromArgb(15, 15, 15);
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
        _open = true;
        Efficiency.Held = true;
        Efficiency.SetSelf(false); // the engine's request checks run on this process's UI thread: never at idle priority
        AdBlocker.EnsureStarted();
        try
        {
            var options = new CoreWebView2EnvironmentOptions(BrowserArguments) { AreBrowserExtensionsEnabled = false };
            var env = await CoreWebView2Environment.CreateAsync(null, ProfileDir, options);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Log.Error("YouTube window could not start", ex);
            MessageBox.Show(this, $"The YouTube window could not start: {ex.Message}", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        core.SourceChanged += (_, _) => { _page = core.Source; _navigatingTo = null; };
        core.NewWindowRequested += OnNewWindowRequested;
        core.DocumentTitleChanged += (_, _) => { _pageTitle = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "YouTube" : core.DocumentTitle; UpdateTitle(); };
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
        core.WebMessageReceived += OnWebMessage;

        // AdVoid: its page scripts before the page's own, the element hiding asked for by each top-level page, and
        // every request (from pages, workers and service workers) checked by the engine.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Scriptlets.All);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(CosmeticLoader);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;

        Log.Info($"YouTube window opened (WebView2 {core.Environment.BrowserVersionString})");
        _titleTimer.Start();
        core.Navigate(Home.AbsoluteUri);
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
        if ((string?)message?["type"] != "advoid-css") return;
        var css = AdBlocker.Engine.Cosmetics(e.Source);
        try { _web.CoreWebView2.PostWebMessageAsJson(new JsonObject { ["type"] = "advoid-css", ["css"] = css }.ToJsonString()); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var url = e.Request.Uri;
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

    private void UpdateTitle()
    {
        var text = _blockedHere > 0 ? $"{_pageTitle} · AdVoid blocked {_blockedHere:N0}" : _pageTitle;
        if (Text != text) Text = text;
    }

    // ---- what stays in the window ----

    private static bool IsInApp(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var site = Domains.Registrable(host);
        return site is "youtube.com" or "youtu.be" or "youtube-nocookie.com" or "ytimg.com" or "googlevideo.com"
            || site.StartsWith("google.", StringComparison.Ordinal); // sign-in and consent pages (google.com, google.com.au, ...)
    }

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
        e.Handled = true; // never a second browser window: YouTube links open here, anything else in the default browser
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
        // Dark title bar when Windows' apps are dark (YouTube follows the same setting).
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
        try { if (File.Exists(BoundsFile)) saved = JsonSerializer.Deserialize<SavedBounds>(File.ReadAllText(BoundsFile)); }
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
        try { File.WriteAllText(BoundsFile, JsonSerializer.Serialize(new SavedBounds(b.X, b.Y, b.Width, b.Height, WindowState == FormWindowState.Maximized, TopMost))); }
        catch (IOException) { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _titleTimer.Dispose();
        _web.Dispose(); // ends this window's browser processes
        _open = false;
        Efficiency.Held = false;
        Efficiency.SetSelf(true);
        Log.Info($"YouTube window closed; AdVoid blocked {_blockedHere:N0} request(s) in it");
        base.OnFormClosed(e);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);
    [DllImport("user32.dll")] private static extern uint CheckMenuItem(IntPtr menu, int id, uint check);
}

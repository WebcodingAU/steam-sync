using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SteamSync;

/// <summary>
/// The Spotify player (beta builds): a window whose page (spotify.html, embedded) is a Spotify Connect device called
/// "Gaming Enjoyer" through Spotify's Web Playback SDK, so music plays from this app with the Spotify app closed
/// (Spotify Premium only; that's Spotify's rule for the SDK). The page also browses and searches the user's Spotify
/// through the Web API and can take over playback from any other device.
/// <para>
/// The page runs on https://spotify.gaming-enjoyer.invalid/, a WebView2 virtual host mapped to a folder this writes
/// it into, because the SDK's DRM (Widevine, which WebView2 has) needs a secure page. The page asks this window for
/// access tokens (SpotifyAuth) over WebView2's message channel; nothing else on the page can reach the app.
/// </para>
/// <para>
/// Closing the window hides it, so the music keeps playing; the tray's Spotify menu shows what's playing and controls
/// it. Hidden and paused for 10 minutes, the browser engine is shut down (freeing its memory for games) and starts
/// again when the window opens. Its processes are kept out of Efficiency mode, which the rest of the app uses, so
/// audio never stutters while a game keeps the CPU busy.
/// </para>
/// </summary>
internal sealed class SpotifyWindow : Form
{
    private const string Host = "spotify.gaming-enjoyer.invalid";
    private static readonly string ProfileDir = Path.Combine(AppInfo.DataDir, "WebView2-Spotify");
    private static readonly string PageDir = Path.Combine(AppInfo.DataDir, "spotify-ui");
    private static readonly string BoundsFile = Path.Combine(AppInfo.DataDir, "spotify-window.json");
    private static readonly TimeSpan UnloadAfter = TimeSpan.FromMinutes(10);

    // Autoplay without a click, so the tray's Play works on a page nobody has clicked yet; media keys and Windows'
    // media flyout reach the page's media session. The rest matches the library window's trimmed-down engine, except
    // that component updates stay on: they register the Widevine DRM module Spotify's songs need (without it every
    // song fails and the player skips through them).
    private const string BrowserArguments =
        "--autoplay-policy=no-user-gesture-required --enable-features=HardwareMediaKeyHandling "
        + "--disable-sync --disable-default-apps --no-first-run "
        + "--disable-features=Translate,OptimizationHints,MediaRouter,AutofillServerCommunication,msSmartScreenProtection,"
        + "msEdgeShoppingAssistant,msShowSearchInSidebar";

    private readonly SpotifyAuth? _auth;
    private readonly System.Windows.Forms.Timer _unloadTimer = new() { Interval = (int)UnloadAfter.TotalMilliseconds };
    private WebView2? _web;
    private bool _exiting, _pageReady;
    private string? _pendingCommand;

    /// <summary>What's playing (null when nothing is), for the tray menu.</summary>
    public NowPlaying? Current { get; private set; }

    public SpotifyWindow(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.SpotifyClientId)) _auth = new SpotifyAuth(config.SpotifyClientId.Trim());
        Text = $"Spotify — {AppInfo.Name}";
        Icon = AppInfo.LoadIcon();
        BackColor = Color.FromArgb(10, 12, 17);
        MinimumSize = new Size(760, 480);
        StartPosition = FormStartPosition.Manual;
        LoadBounds();
        _unloadTimer.Tick += (_, _) => UnloadIfIdle();
        FormClosing += OnFormClosing;
        VisibleChanged += (_, _) => { if (Visible) _unloadTimer.Stop(); else ScheduleUnload(); };
    }

    /// <summary>Shows the window, starting the player if it isn't running.</summary>
    public async Task ShowPlayerAsync()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        await EnsureStartedAsync();
    }

    /// <summary>Play/pause, next or previous from the tray, starting the player (hidden) if it isn't running.</summary>
    public async Task CommandAsync(string action)
    {
        if (_pageReady) { Post(new JsonObject { ["type"] = "command", ["action"] = action }); return; }
        _pendingCommand = action; // sent once the page has loaded and asked for its state
        await EnsureStartedAsync();
    }

    /// <summary>Closes it for real (the app is exiting).</summary>
    public void Exit()
    {
        _exiting = true;
        Close();
    }

    private async Task EnsureStartedAsync()
    {
        if (_web?.CoreWebView2 is not null) return;
        if (!IsHandleCreated) CreateHandle();
        _web ??= new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(10, 12, 17) };
        if (!Controls.Contains(_web)) Controls.Add(_web);
        try
        {
            WritePage();
            var options = new CoreWebView2EnvironmentOptions(BrowserArguments) { AreBrowserExtensionsEnabled = false };
            var env = await CoreWebView2Environment.CreateAsync(null, ProfileDir, options);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Log.Error("Spotify player could not start", ex);
            MessageBox.Show(this, ex is WebView2RuntimeNotFoundException
                    ? "The Microsoft Edge WebView2 Runtime is needed for the Spotify player."
                    : $"The Spotify player could not start: {ex.Message}",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DisposeWeb();
            return;
        }

        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping(Host, PageDir, CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Host == Host) return;
            e.Cancel = true; // the player page only; links open in the default browser
            OpenExternally(e.Uri);
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternally(e.Uri); };
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, e) =>
        {
            Log.Info($"Spotify player: WebView2 {e.ProcessFailedKind} ({e.Reason})");
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) BeginInvoke(DisposeWeb);
            else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive) core.Reload();
        };
        core.Environment.ProcessInfosChanged += (_, _) => KeepOutOfEfficiencyMode();
        KeepOutOfEfficiencyMode();
        Log.Info($"Spotify player started (WebView2 {core.Environment.BrowserVersionString})");
        core.Navigate($"https://{Host}/index.html");
    }

    /// <summary>The page is embedded in the app; it's written out each start so an update brings the new one.</summary>
    private static void WritePage()
    {
        Directory.CreateDirectory(PageDir);
        using var page = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamSync.spotify.html")!;
        using var file = File.Create(Path.Combine(PageDir, "index.html"));
        page.CopyTo(file);
    }

    private void KeepOutOfEfficiencyMode()
    {
        try
        {
            foreach (var info in _web?.CoreWebView2?.Environment.GetProcessInfos() ?? [])
                Efficiency.Set(info.ProcessId, false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || source.Host != Host) return;
        JsonObject? message;
        try { message = JsonNode.Parse(e.WebMessageAsJson) as JsonObject; }
        catch (JsonException) { return; }
        if (message is null) return;

        switch ((string?)message["type"])
        {
            case "state":
                _pageReady = true;
                PostState();
                if (_pendingCommand is { } pending)
                {
                    _pendingCommand = null;
                    Post(new JsonObject { ["type"] = "command", ["action"] = pending });
                }
                break;
            case "token":
                var token = _auth is null ? null : await _auth.GetTokenAsync();
                Post(new JsonObject
                {
                    ["type"] = "token",
                    ["id"] = message["id"]?.DeepClone(),
                    ["token"] = token?.Token,
                    ["expires"] = token?.Expires.ToUnixTimeMilliseconds(),
                });
                if (token is null) PostState();
                break;
            case "signin" when _auth is not null:
                var problem = await _auth.SignInAsync();
                Activate();
                Post(new JsonObject { ["type"] = "signin", ["error"] = string.IsNullOrEmpty(problem) ? null : problem });
                PostState();
                break;
            case "signout" when _auth is not null:
                _auth.SignOut();
                SetNowPlaying(null);
                PostState();
                break;
            case "open":
                if (Uri.TryCreate((string?)message["url"], UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
                    && (url.Host.EndsWith(".spotify.com", StringComparison.OrdinalIgnoreCase) || url.Host == "spotify.com"))
                    OpenExternally(url.AbsoluteUri);
                break;
            case "log":
                Log.Info($"Spotify player: {Truncate((string?)message["message"] ?? "", 300)}");
                break;
            case "nowplaying":
                var title = (string?)message["title"];
                SetNowPlaying(title is null ? null : new NowPlaying(title, (string?)message["artist"] ?? "",
                    (bool?)message["playing"] == true, (bool?)message["here"] == true, (string?)message["device"]));
                break;
        }
    }

    private void PostState() => Post(new JsonObject
    {
        ["type"] = "state",
        ["configured"] = _auth is not null,
        ["signedIn"] = _auth is not null && SpotifyAuth.HasStoredSignIn,
        ["version"] = AppInfo.Version,
    });

    private void Post(JsonObject message)
    {
        try { _web?.CoreWebView2?.PostWebMessageAsJson(message.ToJsonString()); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void SetNowPlaying(NowPlaying? now)
    {
        if (now == Current) return;
        Current = now;
        if (!Visible) ScheduleUnload();
    }

    private void ScheduleUnload()
    {
        _unloadTimer.Stop();
        if (!Visible && _web?.CoreWebView2 is not null && Current is not { Playing: true, Here: true }) _unloadTimer.Start();
    }

    private void UnloadIfIdle()
    {
        _unloadTimer.Stop();
        if (Visible || Current is { Playing: true, Here: true } || _web?.CoreWebView2 is null) return;
        DisposeWeb();
        SetNowPlaying(null);
        Log.Info($"Spotify player unloaded after {UnloadAfter.TotalMinutes:0} minutes hidden and paused; memory freed");
    }

    /// <summary>Disposing the only WebView2 ends all its browser processes.</summary>
    private void DisposeWeb()
    {
        _pageReady = false;
        if (_web is null) return;
        Controls.Remove(_web);
        _web.Dispose();
        _web = null;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        SaveBounds();
        if (_exiting || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing or CloseReason.ApplicationExitCall) return;
        e.Cancel = true; // keep playing: the tray's Spotify menu brings it back
        Hide();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _unloadTimer.Dispose();
        DisposeWeb();
        base.OnFormClosed(e);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static void OpenExternally(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Could not open a link from the Spotify player", ex); }
    }

    private void LoadBounds()
    {
        var bounds = new Rectangle(0, 0, 1100, 720);
        try
        {
            if (File.Exists(BoundsFile) && JsonSerializer.Deserialize<SavedBounds>(File.ReadAllText(BoundsFile)) is { } saved)
                bounds = new Rectangle(saved.X, saved.Y, Math.Max(saved.Width, 760), Math.Max(saved.Height, 480));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        if (!Screen.AllScreens.Any(s => Rectangle.Intersect(s.WorkingArea, bounds).Width >= 200))
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1100, 720);
            bounds = new Rectangle(area.X + (area.Width - bounds.Width) / 2, area.Y + (area.Height - bounds.Height) / 2,
                Math.Min(bounds.Width, area.Width), Math.Min(bounds.Height, area.Height));
        }
        Bounds = bounds;
    }

    private void SaveBounds()
    {
        if (WindowState == FormWindowState.Minimized || !Visible) return;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        try { File.WriteAllText(BoundsFile, JsonSerializer.Serialize(new SavedBounds(b.X, b.Y, b.Width, b.Height))); }
        catch (IOException) { }
    }

    private sealed record SavedBounds(int X, int Y, int Width, int Height);
}

/// <summary>The track the Spotify player shows: on this PC (Here) or another Spotify Connect device.</summary>
internal sealed record NowPlaying(string Title, string Artist, bool Playing, bool Here, string? Device);

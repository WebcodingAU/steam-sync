using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace SteamSync;

/// <summary>
/// The Spotify player (beta builds): a page (spotify.html, embedded) that is a Spotify Connect device called "Gaming
/// Enjoyer" through Spotify's Web Playback SDK, so music plays from this app with the Spotify app closed (Spotify
/// Premium only; that's Spotify's rule for the SDK). It also browses and searches the user's Spotify through the Web API
/// and can take over playback from any other device.
/// <para>
/// It lives inside the Library window (LibraryWindow.AttachSpotify): a player bar along the bottom that expands to the
/// whole window. It's a bare CoreWebView2Controller rather than a WebView2 control so it can move between windows: when
/// the Library window closes it moves to a hidden window and keeps playing, the tray's Spotify menu controls it, and the
/// next Library window takes it back. Detached and paused for 10 minutes, it shuts down (freeing its memory for games).
/// </para>
/// <para>
/// The page runs on https://spotify.gaming-enjoyer.invalid/, served from memory to WebView2 (WebResourceRequested; no
/// file, so nothing on disk can be swapped or blocked): the SDK's DRM (Widevine) needs a secure page, and component updates must stay on so WebView2 registers its
/// Widevine module (without it every song fails). Its engine is its own (profile WebView2-Spotify), apart from the
/// site's, and its processes are kept out of the Efficiency mode the rest of the app uses, so audio never stutters
/// while a game keeps the CPU busy. The page asks this class for access tokens (SpotifyAuth) over WebView2's message
/// channel; nothing else can reach the app.
/// </para>
/// </summary>
internal sealed class SpotifyPlayer : IDisposable
{
    private const string Host = "spotify.gaming-enjoyer.invalid";
    private static readonly string ProfileDir = Path.Combine(AppInfo.DataDir, "WebView2-Spotify");
    private static readonly Lazy<byte[]> Page = new(() =>
    {
        using var page = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamSync.spotify.html")!;
        using var bytes = new MemoryStream();
        page.CopyTo(bytes);
        return bytes.ToArray();
    });
    private static readonly TimeSpan UnloadAfter = TimeSpan.FromMinutes(10);

    // Autoplay without a click, so the tray's Play works on a page nobody has clicked yet; media keys and Windows'
    // media flyout reach the page's media session. No --disable-component-update: see the class comment.
    private const string BrowserArguments =
        "--autoplay-policy=no-user-gesture-required --enable-features=HardwareMediaKeyHandling "
        + "--disable-sync --disable-default-apps --no-first-run "
        + "--disable-features=Translate,OptimizationHints,MediaRouter,AutofillServerCommunication,msSmartScreenProtection,"
        + "msEdgeShoppingAssistant,msShowSearchInSidebar";

    private readonly SpotifyAuth? _auth;
    private readonly Form _parking = new() { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(1, 1) };
    private readonly System.Windows.Forms.Timer _unloadTimer = new() { Interval = (int)UnloadAfter.TotalMilliseconds };
    private CoreWebView2Controller? _controller;
    private Task? _starting;
    private Control? _host;
    private bool _pageReady, _expanded;
    private string? _pendingCommand;

    public SpotifyPlayer(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.SpotifyClientId)) _auth = new SpotifyAuth(config.SpotifyClientId.Trim());
        _unloadTimer.Tick += (_, _) => UnloadIfIdle();
        _ = _parking.Handle; // a window to hold the player while no Library window shows it; never shown
    }

    /// <summary>What's playing (null when nothing is), for the tray menu.</summary>
    public NowPlaying? Current { get; private set; }

    /// <summary>Signed in to Spotify (or at least a sign-in is stored): the Library window shows the bar.</summary>
    public bool SignedIn => _auth is not null && SpotifyAuth.HasStoredSignIn;

    /// <summary>Running in a Library window or in the background.</summary>
    public bool Running => _controller is not null;

    /// <summary>The whole window (true) or the bar along the bottom (false).</summary>
    public bool Expanded => _expanded;

    /// <summary>The player wants the bar or the whole window; the Library window lays it out.</summary>
    public event Action? ExpandedChanged;

    /// <summary>The player started or stopped; the Library window shows or hides its space.</summary>
    public event Action? RunningChanged;

    /// <summary>What the Library page's glass bar shows (glassbar.js), as the page's JSON; null until it reports.</summary>
    public string? Bar { get; private set; }

    /// <summary>A message for the glass bar: kind "bar" (Bar changed) or "toast", with its JSON.</summary>
    public event Action<string, string>? BarMessage;

    // The glass bar's buttons. Play, next and previous also start the player; the rest need it running.
    private static readonly HashSet<string> BarActions = ["shuffle", "repeat", "like", "here", "mute", "seek", "volume"];

    /// <summary>A click on the glass bar (anything the page's bar could do, nothing more).</summary>
    public async Task BarCommandAsync(string action, double? value)
    {
        if (action is "toggle" or "next" or "previous") { await CommandAsync(action); return; }
        if (action == "expand") { SetExpanded(true); return; }
        if (!BarActions.Contains(action) || !_pageReady) return;
        var command = new JsonObject { ["type"] = "command", ["action"] = action };
        if (value is { } v && double.IsFinite(v)) command["value"] = v;
        Post(command);
    }

    /// <summary>Shows the player in a Library window's panel (starting it if it isn't running).</summary>
    public async Task AttachAsync(Control host)
    {
        _host = host;
        _unloadTimer.Stop();
        host.Resize += OnHostResize;
        if (_controller is null) await StartAsync();
        if (_controller is null || _host != host) return;
        _controller.ParentWindow = host.Handle;
        OnHostResize(host, EventArgs.Empty);
        _controller.IsVisible = true;
    }

    /// <summary>The Library window is closing: the player moves to the hidden window and keeps playing.</summary>
    public void Detach(Control host)
    {
        if (_host != host) return;
        host.Resize -= OnHostResize;
        _host = null;
        if (_controller is not null)
        {
            _controller.IsVisible = false;
            _controller.ParentWindow = _parking.Handle;
        }
        SetExpanded(false);
        ScheduleUnload();
    }

    public void SetExpanded(bool expanded)
    {
        if (expanded == _expanded) return;
        _expanded = expanded;
        Post(new JsonObject { ["type"] = "mode", ["expanded"] = expanded });
        ExpandedChanged?.Invoke();
        if (expanded && _controller is not null && _host is not null) _controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }

    /// <summary>Play/pause, next or previous from the tray, starting the player in the background if it isn't running.</summary>
    public async Task CommandAsync(string action)
    {
        if (_pageReady) { Post(new JsonObject { ["type"] = "command", ["action"] = action }); return; }
        _pendingCommand = action; // sent once the page has loaded and asked for its state
        if (_controller is null) await StartAsync();
    }

    private void OnHostResize(object? sender, EventArgs e)
    {
        if (_controller is not null && sender is Control host && host == _host) _controller.Bounds = host.ClientRectangle;
    }

    private Task StartAsync() => _starting ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions(BrowserArguments) { AreBrowserExtensionsEnabled = false };
            var env = await CoreWebView2Environment.CreateAsync(null, ProfileDir, options);
            var controller = await env.CreateCoreWebView2ControllerAsync(_host?.Handle ?? _parking.Handle);
            controller.DefaultBackgroundColor = Color.FromArgb(10, 12, 17);
            controller.IsVisible = _host is not null;
            if (_host is not null) controller.Bounds = _host.ClientRectangle;
            Configure(controller.CoreWebView2);
            _controller = controller;
            KeepOutOfEfficiencyMode();
            Log.Info($"Spotify player started (WebView2 {env.BrowserVersionString})");
            controller.CoreWebView2.Navigate($"https://{Host}/index.html");
            RunningChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Spotify player could not start", ex);
        }
        finally { _starting = null; }
    }

    private void Configure(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsZoomControlEnabled = false;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
#endif
        core.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var path = Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "";
            e.Response = path is "/" or "/index.html"
                ? core.Environment.CreateWebResourceResponse(new MemoryStream(Page.Value, writable: false), 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store")
                : core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        };
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
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) _parking.BeginInvoke(Shutdown);
            else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive) core.Reload();
        };
        core.Environment.ProcessInfosChanged += (_, _) => KeepOutOfEfficiencyMode();
    }

    private void KeepOutOfEfficiencyMode()
    {
        try
        {
            foreach (var info in _controller?.CoreWebView2.Environment.GetProcessInfos() ?? [])
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
                var token = _auth is null ? null : await _auth.GetTokenAsync(fresh: (bool?)message["fresh"] == true);
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
                _host?.FindForm()?.Activate();
                Post(new JsonObject { ["type"] = "signin", ["error"] = string.IsNullOrEmpty(problem) ? null : problem });
                PostState();
                break;
            case "signout" when _auth is not null:
                _auth.SignOut();
                SetNowPlaying(null);
                PostState();
                break;
            case "mode":
                SetExpanded((bool?)message["expanded"] == true);
                break;
            case "open":
                if (Uri.TryCreate((string?)message["url"], UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
                    && (url.Host.EndsWith(".spotify.com", StringComparison.OrdinalIgnoreCase) || url.Host == "spotify.com"))
                    OpenExternally(url.AbsoluteUri);
                break;
            case "log":
                var text = (string?)message["message"] ?? "";
                Log.Info($"Spotify player: {(text.Length <= 300 ? text : text[..300] + "…")}");
                break;
            case "bar" when message["data"] is JsonObject barData:
                Bar = barData.ToJsonString();
                BarMessage?.Invoke("bar", Bar);
                break;
            case "toast" when message["text"] is JsonValue toastText:
                BarMessage?.Invoke("toast", toastText.ToJsonString());
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
        ["signedIn"] = SignedIn,
        ["expanded"] = _expanded,
        ["version"] = AppInfo.Version,
    });

    private void Post(JsonObject message)
    {
        try { _controller?.CoreWebView2.PostWebMessageAsJson(message.ToJsonString()); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void SetNowPlaying(NowPlaying? now)
    {
        if (now == Current) return;
        Current = now;
        ScheduleUnload();
    }

    private void ScheduleUnload()
    {
        _unloadTimer.Stop();
        if (_host is null && _controller is not null && Current is not { Playing: true, Here: true }) _unloadTimer.Start();
    }

    private void UnloadIfIdle()
    {
        _unloadTimer.Stop();
        if (_host is not null || Current is { Playing: true, Here: true } || _controller is null) return;
        Shutdown();
        Log.Info($"Spotify player unloaded after {UnloadAfter.TotalMinutes:0} minutes in the background and paused; memory freed");
    }

    /// <summary>Closing the controller ends all its browser processes.</summary>
    private void Shutdown()
    {
        _pageReady = false;
        if (_controller is null) return;
        try { _controller.Close(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        _controller = null;
        Current = null;
        Bar = null;
        RunningChanged?.Invoke();
    }

    public void Dispose()
    {
        _unloadTimer.Dispose();
        Shutdown();
        _parking.Dispose();
    }

    private static void OpenExternally(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Could not open a link from the Spotify player", ex); }
    }
}

/// <summary>The track the Spotify player shows: on this PC (Here) or another Spotify Connect device.</summary>
internal sealed record NowPlaying(string Title, string Artist, bool Playing, bool Here, string? Device);

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SteamSync;

/// <summary>
/// A single-purpose window for gamingenjoyer.com on the WebView2 (Edge) runtime that ships with Windows, built to stay
/// out of a game's way: one profile, no extensions or background services, Efficiency mode whenever it isn't the
/// active window, frozen while minimized, and every browser process gone once it is closed.
/// </summary>
internal sealed class LibraryWindow : Form
{
    private static readonly string ProfileDir = Path.Combine(AppInfo.DataDir, "WebView2");
    private static readonly string BoundsFile = Path.Combine(AppInfo.DataDir, "window.json");

    // Chromium switches that only cost memory or network in a one-site window. Unknown switches are ignored.
    // The GPU stays on: turning it off measured ~100 MB lighter (218 vs 314 MB on the Library page) but made
    // scrolling noticeably laggy, and the 2-minute unload already frees everything while a game runs.
    private const string BrowserArguments =
        "--disable-background-networking --disable-component-update --disable-sync --disable-default-apps "
        + "--no-first-run --disable-features=Translate,OptimizationHints,MediaRouter,AutofillServerCommunication,"
        + "msSmartScreenProtection,msEdgeShoppingAssistant,msShowSearchInSidebar";

    // The launch links the site itself allows (is_allowed_launch_uri in gamingenjoyer). From the site they start the
    // game directly instead of WebView2's "open this app?" prompt.
    private static readonly HashSet<string> LaunchSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "battlenet", "blizzard", "goggalaxy", "uplay", "origin", "origin2", "com.epicgames.launcher",
        "amazon-games", "itch", "minecraft", "rockstar", "playnite", "heroic", "ea", "eagames", "riot-client",
        GameLauncher.Scheme, // games started by this app (GameLauncher), not through Steam
    };

    // Minimized this long, the browser engine is shut down entirely (freeing ~300 MB, e.g. for a game) and the page
    // reloads on restore. Shorter alt-tabs keep the frozen page and come back instantly.
    private static readonly TimeSpan UnloadAfter = TimeSpan.FromMinutes(2);

    private WebView2 _web;
    private readonly System.Windows.Forms.Timer _unloadTimer = new() { Interval = (int)UnloadAfter.TotalMilliseconds };
    private bool _unloaded;
    private Uri? _resumeAt;
    private readonly Uri _home;
    private readonly HashSet<string> _siteHosts;
    private bool _active;
    private bool? _efficient;
    private bool _fullScreen;
    private FormWindowState _stateBeforeFullScreen;

    public LibraryWindow(AppConfig config)
    {
        var site = new Uri(config.WebsiteUrl);
        _home = new Uri(site, "/library.php");
        var bare = site.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? site.Host[4..] : site.Host;
        _siteHosts = new(StringComparer.OrdinalIgnoreCase) { bare, "www." + bare };

        Text = AppInfo.Name;
        Icon = AppInfo.LoadIcon();
        BackColor = Color.FromArgb(10, 12, 17);
        MinimumSize = new Size(480, 360);
        StartPosition = FormStartPosition.Manual;
        LoadBounds();
        _web = NewWebView();
        Controls.Add(_web);
        _unloadTimer.Tick += (_, _) => UnloadIfStillMinimized();

        Load += async (_, _) => await InitAsync(_home);
        Activated += (_, _) => { _active = true; UpdateEfficiency(); };
        Deactivate += (_, _) => { _active = false; UpdateEfficiency(); };
        Resize += async (_, _) => { UpdateEfficiency(); await OnResizedAsync(); };
        FormClosing += (_, _) => SaveBounds();
    }

    private WebView2 NewWebView()
    {
        var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(10, 12, 17) };
        // F11 pressed inside the page reaches the WebView as an accelerator key, not the form's ProcessCmdKey.
        web.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.F11 || e.Modifiers != Keys.None) return;
            e.Handled = true;
            SetFullScreen(!_fullScreen);
        };
        return web;
    }

    private async Task InitAsync(Uri start)
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions(BrowserArguments) { AreBrowserExtensionsEnabled = false };
            var env = await CoreWebView2Environment.CreateAsync(null, ProfileDir, options);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            Log.Error("WebView2 runtime is not installed", ex);
            MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is needed to show your library.\n\n"
                + "It comes with Windows 11; on Windows 10 install it from microsoft.com/edge/webview2.",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Library window could not start", ex);
            MessageBox.Show(this, $"The library window could not start: {ex.Message}", AppInfo.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsReputationCheckingRequired = false; // SmartScreen look-ups; the window only shows one site
        settings.IsSwipeNavigationEnabled = false;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
#endif

        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? AppInfo.Name : core.DocumentTitle;
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                Log.Info($"Library page failed to load: {e.WebErrorStatus}");
        };
        core.ProcessFailed += OnProcessFailed;
        // A page asking for fullscreen (a video's fullscreen button) gets the whole screen, as in Chrome.
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
        core.Environment.ProcessInfosChanged += (_, _) => ApplyEfficiency();

        Log.Info($"Library window opened (WebView2 {core.Environment.BrowserVersionString})");
        core.Navigate(start.AbsoluteUri);
        _efficient = null; // focus may have changed while the engine started: apply the current state in full
        UpdateEfficiency();
    }

    /// <summary>The site and Steam's sign-in stay in the window; any other web page opens in the default browser.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return; // launch links and the like are handled by OnLaunchingExternalUriScheme
        if (IsInApp(uri)) return;
        e.Cancel = true;
        OpenExternally(uri.AbsoluteUri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // never a second browser window
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && _siteHosts.Contains(uri.Host))
            _web.CoreWebView2.Navigate(uri.AbsoluteUri);
        else
            OpenExternally(e.Uri);
    }

    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !LaunchSchemes.Contains(uri.Scheme)) return;
        if (!Uri.TryCreate(e.InitiatingOrigin, UriKind.Absolute, out var origin) || !_siteHosts.Contains(origin.Host)) return;
        e.Cancel = true; // skip the prompt: the site only offers launch links it already allow-lists
        OpenExternally(e.Uri);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Info($"Library window: WebView2 {e.ProcessFailedKind} ({e.Reason})");
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            BeginInvoke(Close); // the whole engine is gone; opening the window again starts a fresh one
        else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            _web.CoreWebView2?.Reload();
    }

    /// <summary>F11 toggles fullscreen, like Chrome. The WebView passes keys it doesn't use up to the form.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11)
        {
            SetFullScreen(!_fullScreen);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Borderless and maximized covers the taskbar; leaving restores the window exactly as it was.</summary>
    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;
        if (on)
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = FormWindowState.Normal; // re-maximizing after the border change makes it cover the taskbar
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _stateBeforeFullScreen;
            // Leaving fullscreen with F11 while a video is fullscreen: tell the page too.
            if (_web.CoreWebView2?.ContainsFullScreenElement == true)
                _ = _web.CoreWebView2.ExecuteScriptAsync("document.exitFullscreen && document.exitFullscreen()");
        }
    }

    private bool IsInApp(Uri uri) =>
        _siteHosts.Contains(uri.Host)
        || uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/openid", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("login.steampowered.com", StringComparison.OrdinalIgnoreCase);

    private static void OpenExternally(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Could not open a link from the library window", ex); }
    }

    /// <summary>Minimized: freeze the page and trim its memory. Restored: wake it.</summary>
    private async Task OnResizedAsync()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            if (!_unloaded && !_unloadTimer.Enabled) _unloadTimer.Start();
        }
        else
        {
            _unloadTimer.Stop();
            if (_unloaded)
            {
                // Bring the engine back on the page the window was showing.
                _unloaded = false;
                Controls.Add(_web);
                await InitAsync(_resumeAt ?? _home);
                return;
            }
        }

        var core = _web.CoreWebView2;
        if (core is null) return;
        try
        {
            if (WindowState == FormWindowState.Minimized)
            {
                if (core.IsSuspended) return;
                _web.Visible = false; // suspending needs a hidden WebView
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                if (!await core.TrySuspendAsync())
                    Log.Info("Library window: the page declined to sleep (it may be playing media)");
            }
            else if (!_web.Visible)
            {
                _web.Visible = true; // the memory target follows activation, in UpdateEfficiency
                if (core.IsSuspended) core.Resume();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("Library window could not change its sleep state", ex);
        }
    }

    /// <summary>Minimized for <see cref="UnloadAfter"/>: shut the whole engine down so it costs no memory at all.</summary>
    private void UnloadIfStillMinimized()
    {
        _unloadTimer.Stop();
        if (_unloaded || WindowState != FormWindowState.Minimized || _web.CoreWebView2 is null) return;

        var source = _web.Source;
        _resumeAt = source is not null && IsInApp(source) ? source : _home;
        Controls.Remove(_web);
        _web.Dispose(); // the last WebView of the environment: its browser, GPU and renderer processes exit
        _web = NewWebView(); // not started until the window is restored
        _unloaded = true;
        Log.Info($"Library window unloaded after {UnloadAfter.TotalMinutes:0} minutes minimized; memory freed");
    }

    /// <summary>
    /// Efficiency mode whenever the window isn't the one being used. Minimized counts as not in use even though
    /// Windows can report a minimized window as active, which is why both are checked.
    /// </summary>
    private void UpdateEfficiency()
    {
        var on = !_active || WindowState == FormWindowState.Minimized;
        if (on == _efficient) return;
        _efficient = on;
        Efficiency.SetSelf(on); // the tray process hosts this window, so it idles whenever the window isn't in use
        ApplyEfficiency();
        // Low asks the engine to drop caches and decoded images it can rebuild, e.g. while a game has the screen.
        if (_web.CoreWebView2 is { } core)
            core.MemoryUsageTargetLevel = on ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
    }

    private void ApplyEfficiency()
    {
        var core = _web.CoreWebView2;
        if (core is null || _efficient is not { } on) return;
        try
        {
            foreach (var info in core.Environment.GetProcessInfos())
                Efficiency.Set(info.ProcessId, on);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The engine is shutting down; nothing left to adjust.
        }
    }

    private void LoadBounds()
    {
        var bounds = new Rectangle(0, 0, 1280, 820);
        var maximized = false;
        try
        {
            if (File.Exists(BoundsFile) && JsonSerializer.Deserialize<SavedBounds>(File.ReadAllText(BoundsFile)) is { } saved)
            {
                bounds = new Rectangle(saved.X, saved.Y, Math.Max(saved.Width, 480), Math.Max(saved.Height, 360));
                maximized = saved.Maximized;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }

        // Only reuse the saved spot if it's still on a connected screen; otherwise centre on the primary one.
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds) && Rectangle.Intersect(s.WorkingArea, bounds).Width >= 200))
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 820);
            bounds = new Rectangle(area.X + (area.Width - bounds.Width) / 2, area.Y + (area.Height - bounds.Height) / 2,
                Math.Min(bounds.Width, area.Width), Math.Min(bounds.Height, area.Height));
        }
        Bounds = bounds;
        if (maximized) WindowState = FormWindowState.Maximized;
    }

    private void SaveBounds()
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        try
        {
            File.WriteAllText(BoundsFile, JsonSerializer.Serialize(new SavedBounds(b.X, b.Y, b.Width, b.Height, WindowState == FormWindowState.Maximized)));
        }
        catch (IOException) { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Disposing the only WebView2 ends its browser, GPU and renderer processes, so a closed window costs nothing.
        _unloadTimer.Dispose();
        _web.Dispose();
        Efficiency.SetSelf(true);
        base.OnFormClosed(e);
    }

    private sealed record SavedBounds(int X, int Y, int Width, int Height, bool Maximized);
}

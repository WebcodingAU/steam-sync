using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SteamSync;

internal sealed class TrayApp : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly SyncService _sync;
    private readonly PlayTracker _play;
    private readonly PingHelperClient _pingHelper = new();
    private readonly LivePing _livePing;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _lastSyncItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _syncNowItem = new("Sync now");
    private readonly ToolStripMenuItem _pairItem = new();
    private readonly ToolStripMenuItem _startupItem = new("Start with Windows") { CheckOnClick = true };
    private readonly ToolStripMenuItem _betaItem = new("Beta updates")
    {
        CheckOnClick = true,
        ToolTipText = "Get test versions before everyone else. Turning it off keeps this version until a newer stable one is out.",
    };
    private readonly ToolStripMenuItem _pingHelperItem = new();
    private readonly RegisteredWaitHandle _openWait;
    private readonly Updater _updater = new();
    private readonly ToolStripMenuItem _updateItem = new() { Visible = false };
    private LibraryWindow? _library;
    private readonly Dictionary<SyncState, Icon> _icons = new()
    {
        [SyncState.Synced] = TrayIcons.Create(),
        [SyncState.Syncing] = TrayIcons.Create(),
        [SyncState.Error] = TrayIcons.Create(Color.FromArgb(232, 52, 52)),
        [SyncState.NotPaired] = TrayIcons.Create(Color.FromArgb(150, 150, 150)),
        [SyncState.NotConfigured] = TrayIcons.Create(Color.FromArgb(150, 150, 150)),
    };

    public TrayApp(AppConfig config, bool background, WaitHandle openSignal, bool openLibrary)
    {
        _config = config;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _sync = new SyncService(config);
        _play = new PlayTracker(config);
        _sync.Scanned += _play.UpdateGames;
        _livePing = new LivePing(config, _play, _pingHelper);

        _syncNowItem.Click += async (_, _) => await _sync.SyncNowAsync();
        _pairItem.Click += (_, _) => TogglePairing();
        _startupItem.Checked = StartWithWindows.IsEnabled;
        _startupItem.CheckedChanged += (_, _) => StartWithWindows.Set(_startupItem.Checked);
        _betaItem.Checked = UpdateChannel.IsBeta;
        _betaItem.CheckedChanged += (_, _) =>
        {
            UpdateChannel.Set(_betaItem.Checked);
            Log.Info($"Update channel set to {(_betaItem.Checked ? "beta" : "stable")}");
            _updater.CheckNow();
        };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            new ToolStripMenuItem("Open Library", null, (_, _) => OpenLibrary()) { Font = new Font(menu.Font, FontStyle.Bold) },
            new ToolStripSeparator(),
            _statusItem,
            _lastSyncItem,
            new ToolStripSeparator(),
            _syncNowItem,
            _pairItem,
            _startupItem,
            _betaItem,
            _pingHelperItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem($"Open {new Uri(config.WebsiteUrl).Host} in browser", null, (_, _) => OpenUrl(config.WebsiteUrl)),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenUrl(Log.FilePath)),
            new ToolStripSeparator(),
            _updateItem,
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _pingHelperItem.Click += async (_, _) => await TogglePingHelperAsync();
        menu.Opening += (_, _) => RenderPingHelper();
        RenderPingHelper();

        _updateItem.Click += (_, _) => _updater.RestartToUpdate();
        _updater.UpdateReady += version => _ui.Post(_ =>
        {
            _updateItem.Text = $"Restart to update to {version}";
            _updateItem.Visible = true;
            _tray.ShowBalloonTip(5000, AppInfo.Name, $"Version {version} is ready. It installs next time {AppInfo.Name} starts, or use \"Restart to update\" in this menu.", ToolTipIcon.Info);
        }, null);
        _tray.DoubleClick += (_, _) => OpenLibrary();
        _openWait = ThreadPool.RegisterWaitForSingleObject(openSignal, (_, _) => _ui.Post(_ => OpenLibrary(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        _sync.StatusChanged += s => _ui.Post(_ => Render(s), null);
        Render(_sync.Status);
        _sync.Start();
        _play.Start();
        _pingHelper.Start();
        _livePing.Start();
        _updater.Start();

        StartWithWindows.RefreshPathIfEnabled();
        GameLauncher.Register(); // gamingenjoyer:// for the site's Play button, kept pointing at this exe

        if (_sync.Status.State == SyncState.NotConfigured)
            _tray.ShowBalloonTip(5000, AppInfo.Name, "Supabase settings are missing — see the README.", ToolTipIcon.Warning);
        else if (!_sync.IsPaired && !background)
            _ui.Post(_ => TogglePairing(), null);

        if (openLibrary) _ui.Post(_ => OpenLibrary(), null);
    }

    /// <summary>Shows the library window, starting its browser engine only now; closing it shuts the engine down again.</summary>
    private void OpenLibrary()
    {
        if (_library is null || _library.IsDisposed)
        {
            _library = new LibraryWindow(_config);
            _library.FormClosed += (_, _) => _library = null;
            _library.Show();
        }
        else if (_library.WindowState == FormWindowState.Minimized)
        {
            _library.WindowState = FormWindowState.Normal;
        }
        _library.Activate();
    }

    private void Render(SyncStatus s)
    {
        _tray.Icon = _icons[s.State];
        // Synced reads as a green dot and the word; anything else says what's going on.
        _tray.Text = Truncate($"{AppInfo.Name} — {(s.State == SyncState.Synced && s.LastSuccess is not null ? "🟢 Synced" : s.Message)}", 127);
        _statusItem.Text = s.Message;
        _lastSyncItem.Text = s.LastSuccess is { } t ? $"Last synced {t:HH:mm} ({s.GameCount} games)" : "Not synced yet";
        _syncNowItem.Enabled = _sync.IsPaired && s.State != SyncState.Syncing;
        _pairItem.Text = _sync.IsPaired ? "Unpair this PC" : "Pair this PC…";
        _pairItem.Enabled = s.State != SyncState.NotConfigured;
    }

    private void TogglePairing()
    {
        if (_sync.IsPaired)
        {
            var answer = MessageBox.Show(
                "Stop syncing this PC? It will also need to be removed on the website to delete its data.",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) _sync.Unpair();
            return;
        }

        using var dialog = new PairDialog(_sync, _config.WebsiteUrl);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            if (!StartWithWindows.IsEnabled)
            {
                StartWithWindows.Set(true);
                _startupItem.Checked = true;
            }
            _tray.ShowBalloonTip(4000, AppInfo.Name, "Paired! Your installed Steam games will stay in sync.", ToolTipIcon.Info);
        }
        Render(_sync.Status);
    }

    /// <summary>The ping helper item says what clicking it will do: install, update or remove (hidden in a build without it).</summary>
    private void RenderPingHelper()
    {
        var state = PingHelperClient.State;
        _pingHelperItem.Visible = state != HelperState.NotBundled;
        _pingHelperItem.Text = state switch
        {
            HelperState.NotInstalled => "Install ping helper (UDP games)…",
            HelperState.Outdated => "Update ping helper…",
            _ => _pingHelper.Connected ? "Remove ping helper…" : "Remove ping helper (not running)…",
        };
        _pingHelperItem.ToolTipText = "Lets live ping see the servers of games that use UDP (most shooters). "
            + "A small Windows service; installing, updating or removing it asks for administrator rights.";
    }

    private async Task TogglePingHelperAsync()
    {
        var state = PingHelperClient.State;
        var install = state != HelperState.Installed;
        if (state == HelperState.NotInstalled)
        {
            var answer = MessageBox.Show(
                "On its own, live ping only sees the servers of TCP games. The ping helper is a small Windows service that "
                + "lets it see UDP games too (most shooters). It only counts which servers each program talks to, never what "
                + "is sent.\n\nInstalling it asks for administrator rights once. Install it?",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
        }
        else if (!install)
        {
            var answer = MessageBox.Show("Remove the ping helper? Live ping will only see TCP games.", AppInfo.Name,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
        }

        _pingHelperItem.Enabled = false;
        try
        {
            var problem = await PingHelperClient.RunElevatedAsync(install);
            if (!string.IsNullOrEmpty(problem)) MessageBox.Show(problem, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (problem is null)
                _tray.ShowBalloonTip(4000, AppInfo.Name, install ? "Ping helper installed." : "Ping helper removed.", ToolTipIcon.Info);
        }
        finally
        {
            _pingHelperItem.Enabled = true;
            RenderPingHelper();
        }
    }

    private static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open {target}", ex); }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void ExitThreadCore()
    {
        _openWait.Unregister(null);
        _updater.Dispose();
        _library?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _livePing.Dispose();
        _pingHelper.Dispose();
        _play.Dispose();
        _sync.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>
/// The tray icon is the app logo at the taskbar's icon size (picked from app.ico, so it isn't rescaled), with a
/// coloured dot in the corner when something needs attention. Syncing has no dot: it lasts seconds and would flicker.
/// </summary>
internal static class TrayIcons
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(Color? badge = null)
    {
        using var app = AppInfo.LoadIcon();
        var logo = new Icon(app, SystemInformation.SmallIconSize);
        if (badge is not { } color) return logo;

        using (logo)
        using (var bmp = logo.ToBitmap())
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float size = bmp.Width, dot = size * 0.5f, gap = Math.Max(1f, size / 12f);
                // Cut a transparent ring around the dot so it reads against the logo on light and dark taskbars.
                using (var cut = new GraphicsPath())
                {
                    cut.AddEllipse(size - dot - gap, size - dot - gap, dot + gap * 2, dot + gap * 2);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    using var clear = new SolidBrush(Color.Transparent);
                    g.FillPath(clear, cut);
                    g.CompositingMode = CompositingMode.SourceOver;
                }
                using var fill = new SolidBrush(color);
                g.FillEllipse(fill, size - dot, size - dot, dot, dot);
            }
            var handle = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }
    }
}

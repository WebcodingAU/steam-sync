using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SteamSync;

internal sealed class TrayApp : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly SyncService _sync;
    private readonly PlayTracker _play;
    private readonly PcLoad _pcLoad;            // beta builds only
    private readonly GameRelay _relay;          // menu shown in beta builds that carry the relay helper, or once set up
    private readonly ToolStripMenuItem _relayItem = new("Game relay");
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
    private readonly ToolStripMenuItem _oldHelperItem = new("Remove old ping helper…")
    {
        ToolTipText = "Live ping was a test feature and is gone. This removes the small Windows service it used (asks for administrator rights).",
    };
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
        _relay = new GameRelay(_play);
        _pcLoad = new PcLoad(config, _play, _relay);
        _relayItem.DropDownItems.Add("…"); // rebuilt each time it opens (BuildRelayMenu); this only shows the arrow
        _relayItem.DropDownOpening += (_, _) => BuildRelayMenu();

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
            _relayItem,
            _oldHelperItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem($"Open {new Uri(config.WebsiteUrl).Host} in browser", null, (_, _) => OpenUrl(config.WebsiteUrl)),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenUrl(Log.FilePath)),
            new ToolStripSeparator(),
            _updateItem,
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _oldHelperItem.Click += async (_, _) => await RemoveOldHelperAsync();
        menu.Opening += (_, _) =>
        {
            _oldHelperItem.Visible = OldPingHelper.IsInstalled;
            _relayItem.Visible = GameRelay.IsSetUp || GameRelay.CanSetUp;
        };
        _oldHelperItem.Visible = OldPingHelper.IsInstalled;
        _relayItem.Visible = GameRelay.IsSetUp || GameRelay.CanSetUp;

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
        _pcLoad.Start();
        _relay.Start();
        _updater.Start();

        StartWithWindows.RefreshPathIfEnabled();
        GameLauncher.Register(); // gamingenjoyer:// for the site's Play button, kept pointing at this exe

        if (_sync.Status.State == SyncState.NotConfigured)
            _tray.ShowBalloonTip(5000, AppInfo.Name, "Supabase settings are missing — see the README.", ToolTipIcon.Warning);
        else if (!_sync.IsPaired && !background)
            _ui.Post(_ => TogglePairing(), null);
        // Live ping is gone; a PC that installed its helper is told once per start how to remove it.
        else if (OldPingHelper.IsInstalled)
            _tray.ShowBalloonTip(6000, AppInfo.Name, "Live ping has been removed. Choose \"Remove old ping helper…\" in this menu to remove its Windows service (it asks for administrator rights).", ToolTipIcon.Info);

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

    private async Task RemoveOldHelperAsync()
    {
        _oldHelperItem.Enabled = false;
        try
        {
            var problem = await OldPingHelper.RemoveAsync();
            if (!string.IsNullOrEmpty(problem)) MessageBox.Show(problem, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (problem is null) _tray.ShowBalloonTip(4000, AppInfo.Name, "Old ping helper removed.", ToolTipIcon.Info);
        }
        finally
        {
            _oldHelperItem.Enabled = true;
            _oldHelperItem.Visible = OldPingHelper.IsInstalled;
        }
    }

    /// <summary>
    /// The Game relay submenu: the setup until it has run, then the relay's status, a tick per game (running games
    /// and every game with relay settings; ticked by default, untick to keep a game off the relay), Compare ping for
    /// the running game, and relay.json for typed-in ranges.
    /// </summary>
    private void BuildRelayMenu()
    {
        var items = _relayItem.DropDownItems;
        items.Clear();
        if (!GameRelay.IsSetUp)
        {
            items.Add(new ToolStripMenuItem("Set up game relay…", null, async (_, _) => await SetUpRelayAsync())
            {
                ToolTipText = "Pick your relay's WireGuard client config. Asks for administrator rights once, to install the relay helper.",
            });
            return;
        }

        items.Add(new ToolStripMenuItem(_relay.Status) { Enabled = false });
        if (_relay.HelperOutdated)
            items.Add(new ToolStripMenuItem("Update relay helper…", null, async (_, _) => await RelayAdminAsync(GameRelay.UpdateHelperAsync(), "Relay helper updated.")));
        if (_relay.Pending) items.Add(new ToolStripMenuItem("Changes apply when your game closes") { Enabled = false });
        items.Add(new ToolStripSeparator());
        var games = _relay.Games();
        if (games.Count == 0) items.Add(new ToolStripMenuItem("Every game goes through the relay; start one to see it here") { Enabled = false });
        else items.Add(new ToolStripMenuItem("Untick a game to keep it off the relay") { Enabled = false });
        foreach (var (key, name, enabled, ranges, running) in games)
        {
            var label = $"{name}{(running ? " (running)" : "")} — {(ranges == 0 ? "no servers learned yet" : $"{ranges} range{(ranges == 1 ? "" : "s")}")}";
            var item = new ToolStripMenuItem(label) { Checked = enabled, CheckOnClick = true };
            item.CheckedChanged += (_, _) => _relay.SetEnabled(key, name, item.Checked);
            items.Add(item);
        }
        items.Add(new ToolStripSeparator());
        var backup = new ToolStripMenuItem("Use phone as backup (send every packet twice)")
        {
            Checked = _relay.Duplicate,
            CheckOnClick = true,
            Enabled = _relay.DuplicateAvailable || _relay.Duplicate,
            ToolTipText = _relay.DuplicateAvailable
                ? "Sends each relayed packet over every connection (Starlink and a USB-tethered phone), so a packet one drops still arrives. Only relayed game traffic uses mobile data."
                : "Your relay config has no packet duplication set up ([GamingEnjoyer] DuplicateEndpoint).",
        };
        backup.CheckedChanged += (_, _) => _relay.SetDuplicate(backup.Checked);
        items.Add(backup);
        if (_relay.Duplicate)
            foreach (var line in _relay.PathLines) items.Add(new ToolStripMenuItem("    " + line) { Enabled = false });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Compare ping (relay vs direct)", null, async (_, _) =>
        {
            var result = await _relay.CompareAsync();
            MessageBox.Show(result, $"{AppInfo.Name} — game relay", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }) { Enabled = _play.Running.Count > 0 });
        items.Add(new ToolStripMenuItem("Edit relay ranges (relay.json)", null, (_, _) => OpenUrl(GameRelay.SettingsFile))
        {
            Enabled = File.Exists(GameRelay.SettingsFile),
        });
        items.Add(new ToolStripMenuItem("Remove game relay…", null, async (_, _) =>
        {
            if (MessageBox.Show("Remove the game relay from this PC? Its tunnel, the relay helper service and the stored relay config are removed (asks for administrator rights).",
                    AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                await RelayAdminAsync(GameRelay.RemoveAsync(), "Game relay removed.");
        }));
    }

    private async Task SetUpRelayAsync()
    {
        using var pick = new OpenFileDialog
        {
            Title = "Your relay's WireGuard client config",
            Filter = "WireGuard config (*.conf)|*.conf|All files (*.*)|*.*",
        };
        if (pick.ShowDialog() != DialogResult.OK) return;
        if (await RelayAdminAsync(GameRelay.SetUpAsync(pick.FileName), "Game relay set up. Every game goes through it from its second launch; untick one in the Game relay menu to keep it off."))
            _relay.Start();
    }

    /// <summary>Waits for one of the relay's admin steps; shows what went wrong, or the balloon when it worked.</summary>
    private async Task<bool> RelayAdminAsync(Task<string?> step, string done)
    {
        _relayItem.Enabled = false;
        try
        {
            var problem = await step;
            if (problem == "") return false; // UAC prompt declined
            if (problem is not null)
            {
                MessageBox.Show(problem, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            _tray.ShowBalloonTip(5000, AppInfo.Name, done, ToolTipIcon.Info);
            return true;
        }
        finally { _relayItem.Enabled = true; }
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
        _pcLoad.Dispose();
        _relay.Dispose();
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

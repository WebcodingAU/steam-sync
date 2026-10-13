using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SteamSync;

internal sealed class TrayApp : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly SyncService _sync;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _lastSyncItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _syncNowItem = new("Sync now");
    private readonly ToolStripMenuItem _pairItem = new();
    private readonly ToolStripMenuItem _startupItem = new("Start with Windows") { CheckOnClick = true };
    private readonly Dictionary<SyncState, Icon> _icons = new()
    {
        [SyncState.Synced] = TrayIcons.Create(Color.FromArgb(102, 192, 244)),
        [SyncState.Syncing] = TrayIcons.Create(Color.FromArgb(164, 208, 7)),
        [SyncState.Error] = TrayIcons.Create(Color.FromArgb(232, 84, 84)),
        [SyncState.NotPaired] = TrayIcons.Create(Color.Gray),
        [SyncState.NotConfigured] = TrayIcons.Create(Color.Gray),
    };

    public TrayApp(AppConfig config, bool background)
    {
        _config = config;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _sync = new SyncService(config);

        _syncNowItem.Click += async (_, _) => await _sync.SyncNowAsync();
        _pairItem.Click += (_, _) => TogglePairing();
        _startupItem.Checked = StartWithWindows.IsEnabled;
        _startupItem.CheckedChanged += (_, _) => StartWithWindows.Set(_startupItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            _lastSyncItem,
            new ToolStripSeparator(),
            _syncNowItem,
            _pairItem,
            _startupItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem($"Open {new Uri(config.WebsiteUrl).Host}", null, (_, _) => OpenUrl(config.WebsiteUrl)),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenUrl(Log.FilePath)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenUrl(config.WebsiteUrl);

        _sync.StatusChanged += s => _ui.Post(_ => Render(s), null);
        Render(_sync.Status);
        _sync.Start();

        StartWithWindows.RefreshPathIfEnabled();

        if (_sync.Status.State == SyncState.NotConfigured)
            _tray.ShowBalloonTip(5000, "Steam Sync", "Supabase settings are missing — see the README.", ToolTipIcon.Warning);
        else if (!_sync.IsPaired && !background)
            _ui.Post(_ => TogglePairing(), null);
    }

    private void Render(SyncStatus s)
    {
        _tray.Icon = _icons[s.State];
        _tray.Text = Truncate($"Steam Sync — {s.Message}", 127);
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
                "Steam Sync", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
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
            _tray.ShowBalloonTip(4000, "Steam Sync", "Paired! Your installed Steam games will stay in sync.", ToolTipIcon.Info);
        }
        Render(_sync.Status);
    }

    private static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open {target}", ex); }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _sync.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Draws the tray icon at runtime (a coloured rounded square with an "S") so no .ico asset is needed yet.</summary>
internal static class TrayIcons
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(Color background)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(31 - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, 31 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(background);
            g.FillPath(fill, path);
            using var font = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("S", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
}

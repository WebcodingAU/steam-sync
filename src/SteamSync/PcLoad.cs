using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SteamSync;

/// <summary>
/// Beta builds only: while a game runs, the PC's load once a second (SystemStats: CPU, GPU, memory load, an
/// approximate responsiveness, the game's own CPU, and the GPU and CPU temperatures when they can be read), for the
/// site's PC load badge and page. Sent through
/// report_live_ping, the table live ping used, as a sample with a "system" object and no ping. When the game stops
/// it sends null, which clears the PC's row. A stable build never starts it. While the game goes through the game
/// relay, the sample also carries its live ping (GameRelay.PingSample: ping_ms, jitter_ms, loss_pct, server, relay),
/// and its games list gets each game's FPS and which one is in front (focused). The sample's game, system FPS and
/// top-level ping are the game in front.
/// </summary>
internal sealed class PcLoad : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    // A games[] entry's ping fields, copied to the top level for the game in front.
    private static readonly string[] PerGamePing = ["ping_ms", "jitter_ms", "loss_pct", "window", "ping_method", "server", "unreachable", "edge_ms", "profile", "route"];

    private readonly SupabaseApi? _api;
    private readonly PlayTracker _play;
    private readonly GameRelay _relay;
    private readonly SystemStats _system = new();
    private readonly System.Threading.Timer _timer;
    private int _ticking, _uploading;
    private bool _live;                     // the site holds a sample from this PC, so a stop must be sent
    private string? _lastFocused;           // the game key whose window last had the focus
    private DateTimeOffset _skipUntil = DateTimeOffset.MinValue;

    public PcLoad(AppConfig config, PlayTracker play, GameRelay relay)
    {
        if (config.IsConfigured) _api = new SupabaseApi(config.SupabaseUrl, config.SupabaseAnonKey);
        _play = play;
        _relay = relay;
        _timer = new(_ => _ = TickAsync());
    }

    public void Start()
    {
        if (UpdateChannel.IsBetaBuild) _timer.Change(TimeSpan.FromSeconds(4), Tick);
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            if (_api is null || DateTimeOffset.UtcNow < _skipUntil || DeviceToken.Load() is not { } token) return;

            var games = _play.Running.ToList();
            if (games.Count == 0)
            {
                // The game stopped: clear the PC's row once.
                if (_live && await SendAsync(token, null)) _live = false;
                return;
            }

            // The game in front: its window has the focus, else the one that last had it, else the game the relay times
            // (the main game of its ping). With LOTRO played and Last Epoch in the background, the badge's FPS and ping
            // are LOTRO's; the games list has each game's own ping and FPS.
            var focused = FocusedGame(games);
            if (focused is not null) _lastFocused = focused.Key;
            var game = focused
                ?? games.FirstOrDefault(g => g.Key == _lastFocused)
                ?? games.FirstOrDefault(g => g.Key == _relay.PingGame)
                ?? games[0];
            var system = _system.Sample(games.Select(g => (IReadOnlyCollection<int>)g.Pids).ToList(), games.IndexOf(game), out var fps);
            var sample = new JsonObject
            {
                ["v"] = 1,
                ["at"] = DateTimeOffset.UtcNow.ToString("o"),
                ["game_key"] = game.Key,
                ["game_name"] = game.Name,
                ["method"] = "pc_load",
                ["system"] = system,
                ["app_version"] = AppInfo.Version,
            };
            if (_relay.PingSample is { } ping)
            {
                foreach (var (name, value) in ping.ToList())
                {
                    ping.Remove(name);
                    sample[name] = value;
                }
                // Each game's FPS next to its ping, and which one is in front.
                foreach (var entry in sample["games"] as JsonArray ?? [])
                {
                    if (entry is not JsonObject row || row["game_key"]?.GetValue<string>() is not { } key) continue;
                    var i = games.FindIndex(g => g.Key == key);
                    if (i < 0) continue;
                    var (gameFps, scaled) = fps[i];
                    row["fps"] = gameFps is { } f ? Math.Round(f) : null;
                    row["fps_scaled"] = scaled is { } s ? Math.Round(s) : null;
                    row["fps_scaler"] = scaled is null ? null : "Lossless Scaling";
                    if (key == focused?.Key) row["focused"] = true;
                }
                // The top-level ping is the game in front's too, like the FPS (none when that game has no server seen),
                // rather than the relay's main game: one game's FPS next to another's ping read as nonsense.
                var front = (sample["games"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(r => r["game_key"]?.GetValue<string>() == game.Key);
                foreach (var name in PerGamePing) sample[name] = front?[name]?.DeepClone();
                if (sample["relay"] is JsonObject relay) relay["routed"] = front?["routed"]?.DeepClone();
            }
            if (Interlocked.Exchange(ref _uploading, 1) == 1) return; // the last second's upload is still going: skip this one
            _ = Task.Run(async () =>
            {
                try { if (await SendAsync(token, sample)) _live = true; }
                finally { Volatile.Write(ref _uploading, 0); }
            });
        }
        catch (Exception ex)
        {
            Log.Error("PC load check failed", ex);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>The running game whose window has the focus (the foreground window's process is one of its), or null.</summary>
    private static RunningGame? FocusedGame(IReadOnlyList<RunningGame> games)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0) return null;
        return games.FirstOrDefault(g => g.Pids.Contains((int)pid));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private async Task<bool> SendAsync(string token, JsonObject? sample)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _api!.ReportLivePingAsync(token, sample, timeout.Token);
            return true;
        }
        catch (SupabaseException ex) when (ex.IsUnknownSignature)
        {
            Log.Info("The server can't take PC load (migration not applied); trying again in an hour");
            _skipUntil = DateTimeOffset.UtcNow.AddHours(1);
        }
        catch (OperationCanceledException) { } // slow second: the next one replaces it
        catch (Exception ex)
        {
            Log.Error("Sending PC load failed", ex);
        }
        return false;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _system.Dispose();
    }
}

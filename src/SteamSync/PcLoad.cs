using System.Text.Json.Nodes;

namespace SteamSync;

/// <summary>
/// Beta builds only: while a game runs, the PC's load once a second (SystemStats: CPU, GPU, memory load, an
/// approximate responsiveness and the game's own CPU), for the site's PC load badge and page. Sent through
/// report_live_ping, the table live ping used, as a sample with a "system" object and no ping. When the game stops
/// it sends null, which clears the PC's row. A stable build never starts it.
/// </summary>
internal sealed class PcLoad : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly SupabaseApi? _api;
    private readonly PlayTracker _play;
    private readonly SystemStats _system = new();
    private readonly System.Threading.Timer _timer;
    private int _ticking, _uploading;
    private bool _live;                     // the site holds a sample from this PC, so a stop must be sent
    private DateTimeOffset _skipUntil = DateTimeOffset.MinValue;

    public PcLoad(AppConfig config, PlayTracker play)
    {
        if (config.IsConfigured) _api = new SupabaseApi(config.SupabaseUrl, config.SupabaseAnonKey);
        _play = play;
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

            var games = _play.Running;
            if (games.Count == 0)
            {
                // The game stopped: clear the PC's row once.
                if (_live && await SendAsync(token, null)) _live = false;
                return;
            }

            var game = games[0];
            var sample = new JsonObject
            {
                ["v"] = 1,
                ["at"] = DateTimeOffset.UtcNow.ToString("o"),
                ["game_key"] = game.Key,
                ["game_name"] = game.Name,
                ["method"] = "pc_load",
                ["system"] = _system.Sample(game.Pids),
                ["app_version"] = AppInfo.Version,
            };
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

using Velopack;
using Velopack.Sources;

namespace SteamSync;

/// <summary>
/// Quiet self-updates from the public releases repo. A new version downloads in the background and installs the next
/// time the app starts (Velopack applies downloaded updates on startup); "Restart to update" installs it straight away.
/// Only active in an installed copy, never in a dev build.
/// </summary>
internal sealed class Updater : IDisposable
{
    private const string ReleasesRepo = "https://github.com/WebcodingAU/gaming-enjoyer-releases";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1); // after startup syncing has settled
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly UpdateManager _manager = new(new GithubSource(ReleasesRepo, null, false));
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _ready;

    /// <summary>Raised (on a worker thread) with the new version once it has downloaded.</summary>
    public event Action<string>? UpdateReady;

    public Updater() => _timer = new(_ => _ = CheckAsync());

    public bool IsInstalled => _manager.IsInstalled;

    public void Start()
    {
        if (!_manager.IsInstalled)
        {
            Log.Info("Not running from an installed copy; automatic updates are off");
            return;
        }
        _timer.Change(FirstCheck, Interval);
    }

    private async Task CheckAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (_ready is not null) return; // already downloaded; it installs on the next start
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null) return;

            var version = update.TargetFullRelease.Version.ToString();
            Log.Info($"Downloading update {version}");
            await _manager.DownloadUpdatesAsync(update);
            _ready = update;
            Log.Info($"Update {version} downloaded; it installs on the next start");
            UpdateReady?.Invoke(version);
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex); // offline, rate-limited, etc.: the next check tries again
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Installs the downloaded update now and starts the new version in the background.</summary>
    public void RestartToUpdate()
    {
        if (_ready is null) return;
        Log.Info($"Restarting to install update {_ready.TargetFullRelease.Version}");
        _manager.ApplyUpdatesAndRestart(_ready, ["--background"]);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _gate.Dispose();
    }
}

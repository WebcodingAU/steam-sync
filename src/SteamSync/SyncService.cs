namespace SteamSync;

internal enum SyncState { NotConfigured, NotPaired, Syncing, Synced, Error }

internal sealed record SyncStatus(SyncState State, string Message, DateTimeOffset? LastSuccess, int GameCount);

/// <summary>
/// Watches every Steam library's steamapps folder (plus Steam's shortcuts.vdf and the Xbox game folders) and pushes
/// the full installed list to Supabase shortly after anything changes, plus a periodic safety-net sync that also
/// picks up registry-only changes (Battle.net, Ubisoft, EA, Battlestate installs).
/// </summary>
internal sealed class SyncService : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);      // wait for Steam to stop writing
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);   // but don't wait forever during long downloads
    private static readonly TimeSpan Periodic = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(2);

    private readonly AppConfig _config;
    private readonly SupabaseApi? _api;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _debounceGate = new();
    private readonly System.Threading.Timer _debounceTimer;
    private readonly System.Threading.Timer _periodicTimer;
    private readonly List<FileSystemWatcher> _watchers = [];
    private IReadOnlyList<string> _watchedLibraries = [];
    private IReadOnlyList<string> _watchedShortcutDirs = [];
    private IReadOnlyList<string> _watchedXboxRoots = [];
    private DateTimeOffset? _firstPendingChange;
    private DateTimeOffset? _lastSuccess;
    private int _lastGameCount;
    private bool _nonSteamFailing;   // the last non-Steam scan failed and its one quick retry is already spent

    public event Action<SyncStatus>? StatusChanged;
    public SyncStatus Status { get; private set; }

    public SyncService(AppConfig config)
    {
        _config = config;
        if (config.IsConfigured) _api = new SupabaseApi(config.SupabaseUrl, config.SupabaseAnonKey);
        _debounceTimer = new(_ => { lock (_debounceGate) _firstPendingChange = null; _ = SyncNowAsync(); });
        _periodicTimer = new(_ => _ = SyncNowAsync());
        Status = InitialStatus();
    }

    public bool IsPaired => DeviceToken.Load() is not null;

    public void Start()
    {
        RefreshWatchers();
        _periodicTimer.Change(TimeSpan.FromSeconds(3), Periodic);
    }

    public async Task<string> PairAsync(string code, string deviceName)
    {
        if (_api is null) throw new InvalidOperationException("Steam Sync is not configured with a Supabase project.");
        var token = await _api.ClaimPairingCodeAsync(code, deviceName);
        DeviceToken.Save(token);
        Log.Info($"Paired as '{deviceName}'");
        _ = SyncNowAsync();
        return token;
    }

    public void Unpair()
    {
        DeviceToken.Clear();
        Log.Info("Unpaired");
        Publish(InitialStatus());
    }

    public async Task SyncNowAsync()
    {
        if (!await _syncGate.WaitAsync(0))
        {
            // A sync is mid-flight and may have already read the files; go again once it settles.
            ScheduleSync(Quiet);
            return;
        }

        try
        {
            var token = DeviceToken.Load();
            if (_api is null || token is null)
            {
                Publish(InitialStatus());
                return;
            }

            Publish(new(SyncState.Syncing, "Syncing…", _lastSuccess, _lastGameCount));

            var snapshot = SteamLibrary.Scan();
            var nonSteam = ScanNonSteam(snapshot);
            // Watch the non-Steam folders even when the scan failed, so Steam fixing shortcuts.vdf triggers a sync.
            var shortcutDirs = nonSteam?.ShortcutConfigDirs
                ?? TryFindFolders("Steam shortcut folders", () => NonSteamLibrary.FindShortcutConfigDirs(snapshot.SteamPath, snapshot.SteamId))
                ?? _watchedShortcutDirs;
            var xboxRoots = nonSteam?.XboxRoots ?? TryFindFolders("Xbox game folders", NonSteamLibrary.FindXboxRoots) ?? _watchedXboxRoots;
            if (!snapshot.Libraries.SequenceEqual(_watchedLibraries, StringComparer.OrdinalIgnoreCase)
                || !shortcutDirs.SequenceEqual(_watchedShortcutDirs, StringComparer.OrdinalIgnoreCase)
                || !xboxRoots.SequenceEqual(_watchedXboxRoots, StringComparer.OrdinalIgnoreCase))
            {
                RefreshWatchers(snapshot.Libraries, shortcutDirs, xboxRoots);
            }

            await _api.SyncAsync(token, snapshot, nonSteam?.Games);

            // A failed scan is usually transient (e.g. Steam mid-way through rewriting shortcuts.vdf), so go again
            // soon, but only once per failure streak so a file that stays broken doesn't sync every few seconds.
            if (nonSteam is null && !_nonSteamFailing) ScheduleSync(Quiet);
            _nonSteamFailing = nonSteam is null;

            _lastSuccess = DateTimeOffset.Now;
            _lastGameCount = snapshot.Games.Count;
            Log.Info($"Synced {snapshot.Games.Count} games from {snapshot.Libraries.Count} libraries, "
                     + (nonSteam is null ? "non-Steam games skipped" : $"{nonSteam.Games.Count(g => g.Installed)} non-Steam games installed"));
            Publish(new(SyncState.Synced, $"{snapshot.Games.Count} games synced", _lastSuccess, _lastGameCount));
        }
        catch (SupabaseException ex) when (ex.IsUnknownDevice)
        {
            Log.Error("Server no longer recognises this device; clearing token", ex);
            DeviceToken.Clear();
            Publish(new(SyncState.NotPaired, "This PC was removed — pair it again", _lastSuccess, _lastGameCount));
        }
        catch (Exception ex)
        {
            Log.Error("Sync failed", ex);
            Publish(new(SyncState.Error, $"Sync failed: {ex.Message}", _lastSuccess, _lastGameCount));
            ScheduleSync(RetryAfterError);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>Null when any detector fails, so the server leaves non-Steam data alone instead of unticking games.</summary>
    private static NonSteamSnapshot? ScanNonSteam(SteamSnapshot steam)
    {
        try
        {
            return NonSteamLibrary.Scan(steam);
        }
        catch (Exception ex)
        {
            Log.Error("Non-Steam detection failed; not sending non-Steam games this time", ex);
            return null;
        }
    }

    /// <summary>Folders to watch after a failed non-Steam scan; null (keep the current watchers) if they can't be found either.</summary>
    private static IReadOnlyList<string>? TryFindFolders(string what, Func<IReadOnlyList<string>> find)
    {
        try
        {
            return find();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not find the {what} to watch", ex);
            return null;
        }
    }

    private SyncStatus InitialStatus() =>
        !_config.IsConfigured ? new(SyncState.NotConfigured, "Not configured (missing Supabase settings)", null, 0)
        : DeviceToken.Load() is null ? new(SyncState.NotPaired, "Not paired — right-click to pair", null, 0)
        : new(SyncState.Synced, "Waiting for first sync", _lastSuccess, _lastGameCount);

    private void Publish(SyncStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileName(e.FullPath);
        if (name.EndsWith(".acf", StringComparison.OrdinalIgnoreCase)
            || name.Equals("libraryfolders.vdf", StringComparison.OrdinalIgnoreCase))
        {
            ScheduleSync(Quiet);
        }
    }

    /// <summary>Debounce: fire <paramref name="delay"/> after the latest change, capped at MaxDelay after the first.</summary>
    private void ScheduleSync(TimeSpan delay)
    {
        lock (_debounceGate)
        {
            var now = DateTimeOffset.Now;
            _firstPendingChange ??= now;
            var due = now + delay;
            var cap = _firstPendingChange.Value + MaxDelay;
            if (delay <= MaxDelay && due > cap) due = cap;
            _debounceTimer.Change(due - now, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Rebuilds every watcher. The non-Steam folders come from each sync (found on their own when its scan fails), so
    /// none are watched at startup; they only matter once paired, and pairing syncs straight away.
    /// </summary>
    private void RefreshWatchers(
        IReadOnlyList<string>? libraries = null,
        IReadOnlyList<string>? shortcutDirs = null,
        IReadOnlyList<string>? xboxRoots = null)
    {
        shortcutDirs ??= [];
        xboxRoots ??= [];
        try
        {
            if (libraries is null)
            {
                var steamPath = SteamLibrary.FindSteamPath();
                libraries = steamPath is null ? [] : SteamLibrary.GetLibraryPaths(steamPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read Steam library list", ex);
            libraries = [];
        }

        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();

            foreach (var library in libraries)
            {
                var steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;
                var watcher = new FileSystemWatcher(steamapps)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                watcher.Created += OnFileChanged;
                watcher.Changed += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                watcher.Renamed += OnFileChanged;
                watcher.Error += (_, e) => { Log.Error("File watcher error", e.GetException()); ScheduleSync(Quiet); };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            var libraryWatchers = _watchers.Count;

            // Non-Steam: shortcuts.vdf rewrites, and Xbox game folders appearing, being renamed or removed.
            // Registry-only installs are left to the periodic sync.
            foreach (var dir in shortcutDirs)
                AddNonSteamWatcher(dir, "shortcuts.vdf", NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size);
            foreach (var root in xboxRoots)
                AddNonSteamWatcher(root, "*", NotifyFilters.DirectoryName);

            _watchedLibraries = libraries;
            _watchedShortcutDirs = shortcutDirs;
            _watchedXboxRoots = xboxRoots;
            Log.Info($"Watching {libraryWatchers} Steam libraries: {string.Join(", ", libraries)}");
            if (shortcutDirs.Count + xboxRoots.Count > 0)
                Log.Info($"Watching non-Steam folders: {string.Join(", ", shortcutDirs.Concat(xboxRoots))}");
        }
    }

    private void AddNonSteamWatcher(string folder, string filter, NotifyFilters notifyFilter)
    {
        if (!Directory.Exists(folder)) return;
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(folder, filter) { NotifyFilter = notifyFilter, IncludeSubdirectories = false };
            watcher.Created += OnNonSteamChanged;
            watcher.Changed += OnNonSteamChanged;
            watcher.Deleted += OnNonSteamChanged;
            watcher.Renamed += OnNonSteamChanged;
            watcher.Error += (_, e) => { Log.Error("File watcher error", e.GetException()); ScheduleSync(Quiet); };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            watcher?.Dispose();
            Log.Error($"Could not watch {folder}", ex);
        }
    }

    private void OnNonSteamChanged(object sender, FileSystemEventArgs e) => ScheduleSync(Quiet);

    public void Dispose()
    {
        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
        _debounceTimer.Dispose();
        _periodicTimer.Dispose();
        _api?.Dispose();
    }
}

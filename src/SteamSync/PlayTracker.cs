using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamSync;

/// <summary>One play session as record_play_sessions takes it (snake_case on the wire).</summary>
internal sealed record PlaySession(Guid SessionId, string GameKey, string Name, DateTimeOffset StartedAt, DateTimeOffset EndedAt, bool Running);

/// <summary>
/// Notices games running on this PC and reports each session, for play time on games outside Steam and the
/// site's "Playing now". Steam games are reported too, keyed "steam:&lt;appid&gt;", but the site only uses those for
/// "Playing now": Steam counts their play time itself.
/// <para>
/// A game is running when a process's exe sits inside its install folder (launchers and gamelaunchhelper.exe hand
/// over to the real game exe, so the launched process can't simply be timed). A process whose path can't be read
/// (some anti-cheat-protected games) is matched by exe name against the exes found in each game's folder. Crash
/// reporters, updaters, installers and anti-cheat services don't count.
/// </para>
/// <para>
/// A session starts with the first matching process and is reported as running straight away, so the site's
/// "Playing now" shows within seconds; it ends once none has been seen for 30 seconds. Runs shorter than a minute
/// don't count as play: one already reported is sent back with no length, which clears "Playing now". When the PC sleeps the session ends at the last check before it. Only while paired:
/// nothing is recorded before that. Unsent sessions live in %LocalAppData%\SteamSync\play_sessions.json, so a
/// session played offline, or one cut short by quitting the app, is sent later.
/// </para>
/// </summary>
internal sealed class PlayTracker : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);   // a start shows on the site within seconds
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinSession = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SleepGap = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UploadEvery = TimeSpan.FromSeconds(60);
    private const int MaxPending = 500;
    private const int MaxExesPerGame = 300;

    private static readonly Regex NotTheGame = new(
        @"^(unins.*|.*crash.*|.*report.*|.*updat.*|.*setup.*|.*install.*|.*redist.*|.*prereq.*|dxsetup|"
        + @"easyanticheat.*|eac.*|battleye.*|beservice.*|.*_be|gamelaunchhelper|.*helper)\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string StateFile = Path.Combine(AppInfo.DataDir, "play_sessions.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly SupabaseApi? _api;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private Target[] _targets = [];                                    // longest folder first
    private Dictionary<string, Target> _byExeName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Active> _active = new(StringComparer.Ordinal);
    private readonly List<PlaySession> _pending = [];
    private DateTimeOffset? _lastTick;
    private DateTimeOffset _lastUpload = DateTimeOffset.MinValue;
    private DateTimeOffset _skipUploadsUntil = DateTimeOffset.MinValue;
    private int _ticking;

    private sealed record Target(string Key, string Name, string Folder);

    private sealed class Active
    {
        public Guid Id { get; init; }
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public DateTimeOffset Started { get; init; }
        public DateTimeOffset LastSeen { get; set; }
        public bool Reported { get; set; }   // sent to the site as running at least once
    }

    private sealed record State(List<Active> Active, List<PlaySession> Pending);

    public PlayTracker(AppConfig config)
    {
        if (config.IsConfigured) _api = new SupabaseApi(config.SupabaseUrl, config.SupabaseAnonKey);
        _timer = new(_ => _ = TickAsync());
        LoadState();
    }

    public void Start() => _timer.Change(TimeSpan.FromSeconds(5), Tick);

    /// <summary>The games to watch, from each sync's scan. Building the exe-name fallback reads the game folders, so this
    /// runs on the sync's worker thread, never the UI's.</summary>
    public void UpdateGames(SteamSnapshot steam, NonSteamSnapshot? nonSteam)
    {
        var targets = new List<Target>();
        foreach (var g in steam.Games)
        {
            if (string.IsNullOrWhiteSpace(g.InstallDir)) continue;
            targets.Add(new Target($"steam:{g.AppId}", g.Name, Path.Combine(g.LibraryPath, "steamapps", "common", g.InstallDir)));
        }
        if (nonSteam is not null)
        {
            foreach (var g in nonSteam.Games)
            {
                if (g.Installed && g.InstallPath is { } folder) targets.Add(new Target(g.EntityKey, g.Name, folder));
            }
        }

        // A folder must be a game's own: never a drive root or a top-level folder like G:\Games, which would claim
        // every game installed under it.
        var usable = targets
            .Select(t => t with { Folder = t.Folder.TrimEnd('\\') })
            .Where(t => Path.IsPathFullyQualified(t.Folder) && t.Folder.Count(c => c == '\\') >= 2 && Directory.Exists(t.Folder))
            .GroupBy(t => t.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(t => t.Folder.Length)
            .ToArray();

        var byName = new Dictionary<string, Target?>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in usable)
        {
            foreach (var exe in ExesIn(t.Folder))
            {
                // A name two games share says nothing about which one is running.
                byName[exe] = byName.TryGetValue(exe, out var other) && other is not null && other.Key != t.Key ? null : t;
            }
        }

        lock (_gate)
        {
            _targets = usable;
            _byExeName = byName.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Exe file names in a game folder, three levels deep at most, without the ones that aren't the game.</summary>
    private static IEnumerable<string> ExesIn(string folder)
    {
        var found = new List<string>();
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(folder, "*.exe", options))
            {
                var name = Path.GetFileName(file);
                if (!NotTheGame.IsMatch(name)) found.Add(name);
                if (found.Count >= MaxExesPerGame) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return found;
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            if (DeviceToken.Load() is null) return; // nothing is recorded before pairing

            var now = DateTimeOffset.UtcNow;
            var running = RunningGames();
            var finishedOne = false;
            var startedOne = false;

            lock (_gate)
            {
                // A late tick means the PC slept (or the app was frozen): whatever was running stopped at the last check.
                if (_lastTick is { } last && now - last > SleepGap)
                    finishedOne |= FinishAll(last);
                _lastTick = now;

                foreach (var (key, name) in running)
                {
                    if (_active.TryGetValue(key, out var a)) a.LastSeen = now;
                    else
                    {
                        _active[key] = new Active { Id = Guid.NewGuid(), Key = key, Name = name, Started = now, LastSeen = now };
                        startedOne = true;
                        Log.Info($"Playing {name} ({key})");
                    }
                }
                foreach (var a in _active.Values.Where(a => !running.ContainsKey(a.Key) && now - a.LastSeen > Grace).ToList())
                    finishedOne |= Finish(a, a.LastSeen);

                SaveState();
            }

            // A start or an end goes out at once, for "Playing now"; otherwise once a minute.
            if (startedOne || finishedOne || now - _lastUpload >= UploadEvery) await UploadAsync(now);
        }
        catch (Exception ex)
        {
            Log.Error("Play tracking check failed", ex);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>The games with a process running now: key → name.</summary>
    private Dictionary<string, string> RunningGames()
    {
        Target[] targets;
        Dictionary<string, Target> byName;
        lock (_gate) { targets = _targets; byName = _byExeName; }
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (targets.Length == 0) return found;

        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == self || p.Id <= 4) continue;
                Target? hit = null;
                if (ImagePath(p.Id) is { } path)
                {
                    if (NotTheGame.IsMatch(Path.GetFileName(path))) continue;
                    hit = targets.FirstOrDefault(t => path.StartsWith(t.Folder + "\\", StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // The path is hidden from us (protected or elevated processes): go by the exe's name.
                    string name;
                    try { name = p.ProcessName + ".exe"; } catch (InvalidOperationException) { continue; }
                    byName.TryGetValue(name, out hit);
                }
                if (hit is not null) found[hit.Key] = hit.Name;
            }
        }
        return found;
    }

    private bool FinishAll(DateTimeOffset at)
    {
        var any = false;
        foreach (var a in _active.Values.ToList()) any |= Finish(a, a.LastSeen < at ? a.LastSeen : at);
        return any;
    }

    /// <summary>Ends a session: kept for sending when it lasted a minute or more. A shorter one already reported as
    /// running is sent once more with no length, so the site stops showing it as playing. True when something is
    /// waiting to be sent.</summary>
    private bool Finish(Active a, DateTimeOffset end)
    {
        _active.Remove(a.Key);
        if (end - a.Started < MinSession)
        {
            Log.Info($"Stopped {a.Name} after under a minute; not counted");
            if (!a.Reported) return false;
            _pending.Add(new PlaySession(a.Id, a.Key, a.Name, a.Started, a.Started, Running: false));
            return true;
        }
        Log.Info($"Stopped {a.Name} after {(end - a.Started).TotalMinutes:0} min");
        _pending.Add(new PlaySession(a.Id, a.Key, a.Name, a.Started, end, Running: false));
        if (_pending.Count > MaxPending) _pending.RemoveRange(0, _pending.Count - MaxPending);
        return true;
    }

    /// <summary>Sends every finished session not sent yet, plus every running one.</summary>
    private async Task UploadAsync(DateTimeOffset now)
    {
        if (_api is null || now < _skipUploadsUntil || DeviceToken.Load() is not { } token) return;

        List<PlaySession> batch;
        lock (_gate)
        {
            batch = _pending.ToList();
            batch.AddRange(_active.Values.Select(a => new PlaySession(a.Id, a.Key, a.Name, a.Started, a.LastSeen, Running: true)));
        }
        _lastUpload = now;
        if (batch.Count == 0) return;

        try
        {
            await _api.RecordPlaySessionsAsync(token, batch);
            lock (_gate)
            {
                foreach (var s in batch.Where(s => s.Running))
                    if (_active.TryGetValue(s.GameKey, out var a) && a.Id == s.SessionId) a.Reported = true;
                var sent = batch.Where(s => !s.Running).Select(s => s.SessionId).ToHashSet();
                _pending.RemoveAll(s => sent.Contains(s.SessionId));
                SaveState();
            }
        }
        catch (SupabaseException ex) when (ex.IsUnknownSignature)
        {
            Log.Info("The server can't take play sessions yet (migration not applied); keeping them and trying again in an hour");
            _skipUploadsUntil = now.AddHours(1);
        }
        catch (Exception ex)
        {
            Log.Error("Sending play sessions failed; keeping them for the next try", ex);
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile), Json);
            if (state is null) return;
            _pending.AddRange(state.Pending ?? []);
            // Sessions still open when the app last stopped ended at their last check.
            foreach (var a in state.Active ?? [])
            {
                _active[a.Key] = a;
                Finish(a, a.LastSeen);
            }
        }
        catch (Exception ex) { Log.Error("Could not read saved play sessions", ex); }
    }

    private void SaveState()
    {
        try
        {
            var tmp = StateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new State(_active.Values.ToList(), _pending), Json));
            File.Move(tmp, StateFile, overwrite: true);
        }
        catch (Exception ex) { Log.Error("Could not save play sessions", ex); }
    }

    // ---- Process paths: QueryFullProcessImageName works for most processes without admin rights. ----

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate) SaveState();
        _api?.Dispose();
    }
}

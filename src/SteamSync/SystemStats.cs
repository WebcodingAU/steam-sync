using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SteamSync;

/// <summary>
/// The PC's load for the PC load sample (PcLoad, beta builds), the numbers Process Lasso graphs: CPU, GPU, memory load and system
/// responsiveness, plus the running game's own CPU. None of it needs admin rights.
/// <para>
/// CPU is "% Processor Utility" (what Task Manager and Process Lasso show, frequency included, capped at 100). GPU is
/// the busiest engine of any GPU, each engine's use summed over its processes, the way Task Manager does it. Memory
/// load is Windows' own figure (GlobalMemoryStatusEx).
/// </para>
/// <para>
/// Responsiveness is Process Lasso's own formula, which isn't published, so this is an approximation from how many
/// threads are ready to run but waiting for a CPU (the processor queue, read every 100 ms): none waiting is 100%,
/// two per logical CPU is 50%. Timing this process's own wake-ups wouldn't work: the tray app runs at idle priority
/// in Efficiency mode, so its threads always wait while a game keeps the CPU busy.
/// </para>
/// <para>
/// Only runs while asked for: the queue reader stops when Sample hasn't been called for a few seconds (no game).
/// </para>
/// </summary>
internal sealed class SystemStats : IDisposable
{
    private static readonly TimeSpan QueueEvery = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly System.Threading.Timer _queueTimer;
    private IntPtr _query, _cpu, _gpu, _queueQuery, _queue;
    private bool _running, _disposed;
    private long _lastSampleAt;
    private double _queueSum, _queueMax;
    private int _queueCount;
    private readonly Dictionary<int, TimeSpan> _gameCpu = [];
    private long _gameCpuAt;

    public SystemStats()
    {
        _queueTimer = new(_ => ReadQueue());
    }

    /// <summary>The load since the last call (about a second), as the sample's "system" object.</summary>
    public JsonObject Sample(IReadOnlyCollection<int> gamePids)
    {
        lock (_lock)
        {
            if (_disposed) return new JsonObject();
            _lastSampleAt = Stopwatch.GetTimestamp();
            var started = !_running;
            if (started) StartLocked();

            // Just started: CPU and GPU are rates, so the first numbers come with the next sample.
            double? cpu = null, gpu = null;
            string? gpuEngine = null;
            if (!started && _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
            {
                cpu = Value(_cpu) is { } c ? Math.Min(100, c) : null;
                (gpu, gpuEngine) = BusiestEngine();
            }

            // The queue read every 100 ms since the last sample; with none (the timer was starved), one read now.
            if (_queueCount == 0) ReadQueueLocked();
            double? queue = _queueCount > 0 ? _queueSum / _queueCount : null;
            double? queueMax = _queueCount > 0 ? _queueMax : null;
            _queueSum = _queueMax = 0;
            _queueCount = 0;

            var cores = Environment.ProcessorCount;
            var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            var haveMemory = GlobalMemoryStatusEx(ref memory);

            return new JsonObject
            {
                ["cpu_pct"] = Round(cpu),
                ["gpu_pct"] = Round(gpu),
                ["gpu_engine"] = gpuEngine,
                ["mem_pct"] = haveMemory ? (int)memory.MemoryLoad : null,
                ["mem_used_gb"] = haveMemory ? Round((memory.TotalPhys - memory.AvailPhys) / 1073741824.0) : null,
                ["mem_total_gb"] = haveMemory ? Round(memory.TotalPhys / 1073741824.0) : null,
                ["responsiveness_pct"] = queue is { } q ? Round(100 / (1 + q / (2.0 * cores))) : null,
                ["ready_queue"] = Round(queue),
                ["ready_queue_max"] = Round(queueMax),
                ["cores"] = cores,
                ["game_cpu_pct"] = Round(GameCpu(gamePids, cores)),
            };
        }
    }

    private void StartLocked()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
        if (_query != IntPtr.Zero)
        {
            if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpu) != 0) _cpu = IntPtr.Zero;
            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpu) != 0) _gpu = IntPtr.Zero;
            PdhCollectQueryData(_query); // rates need two reads: this is the first
        }
        if (PdhOpenQuery(null, IntPtr.Zero, out _queueQuery) != 0) _queueQuery = IntPtr.Zero;
        if (_queueQuery != IntPtr.Zero && PdhAddEnglishCounter(_queueQuery, @"\System\Processor Queue Length", IntPtr.Zero, out _queue) != 0) _queue = IntPtr.Zero;
        _gameCpu.Clear();
        _gameCpuAt = 0;
        _running = true;
        _queueTimer.Change(QueueEvery, QueueEvery);
        Log.Info("System stats started");
    }

    private void StopLocked()
    {
        _queueTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        if (_queueQuery != IntPtr.Zero) PdhCloseQuery(_queueQuery);
        _query = _cpu = _gpu = _queueQuery = _queue = IntPtr.Zero;
        _queueSum = _queueMax = 0;
        _queueCount = 0;
        _running = false;
    }

    private void ReadQueue()
    {
        lock (_lock)
        {
            if (!_running) return;
            if (Stopwatch.GetElapsedTime(_lastSampleAt) > IdleAfter)
            {
                StopLocked(); // the game stopped: nothing asks any more
                return;
            }
            ReadQueueLocked();
        }
    }

    private void ReadQueueLocked()
    {
        if (_queueQuery == IntPtr.Zero || PdhCollectQueryData(_queueQuery) != 0 || Value(_queue) is not { } q) return;
        _queueSum += q;
        _queueMax = Math.Max(_queueMax, q);
        _queueCount++;
    }

    /// <summary>
    /// The busiest GPU engine, in %, and its type (3D, Copy, VideoDecode...). Instances are one per process per engine
    /// ("pid_123_luid_0x0_0x1A2B_phys_0_eng_0_engtype_3D"), so the process part is dropped and the rest summed.
    /// </summary>
    private unsafe (double?, string?) BusiestEngine()
    {
        if (_gpu == IntPtr.Zero) return (null, null);
        uint size = 0;
        if (PdhGetFormattedCounterArray(_gpu, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, IntPtr.Zero) != PdhMoreData || size == 0)
            return (null, null);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_gpu, PdhFmtDouble | PdhFmtNoCap100, ref size, out var count, buffer) != 0) return (null, null);
            var engines = new Dictionary<string, double>();
            var items = (CounterValueItem*)buffer;
            for (var i = 0; i < count; i++)
            {
                if (items[i].Value.Status != 0) continue; // a process that just appeared has no rate yet
                var name = Marshal.PtrToStringUni(items[i].Name) ?? "";
                var engine = name.IndexOf("luid_", StringComparison.Ordinal) is var at and >= 0 ? name[at..] : name;
                engines[engine] = engines.GetValueOrDefault(engine) + items[i].Value.Double;
            }
            if (engines.Count == 0) return (0, null);
            var busiest = engines.MaxBy(e => e.Value);
            var type = busiest.Key.IndexOf("engtype_", StringComparison.Ordinal) is var t and >= 0 ? busiest.Key[(t + 8)..] : null;
            return (Math.Min(100, busiest.Value), busiest.Value > 0 ? type : null);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The game's processes' CPU since the last sample, as a share of the whole PC (like Task Manager).</summary>
    private double? GameCpu(IReadOnlyCollection<int> pids, int cores)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _gameCpuAt == 0 ? 0 : Stopwatch.GetElapsedTime(_gameCpuAt, now).TotalSeconds;
        _gameCpuAt = now;
        var used = TimeSpan.Zero;
        var measured = false;
        var seen = new HashSet<int>();
        foreach (var pid in pids)
        {
            TimeSpan total;
            try { using var p = Process.GetProcessById(pid); total = p.TotalProcessorTime; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
            seen.Add(pid);
            if (_gameCpu.TryGetValue(pid, out var before))
            {
                used += total - before;
                measured = true;
            }
            _gameCpu[pid] = total;
        }
        foreach (var gone in _gameCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _gameCpu.Remove(gone);
        return measured && seconds > 0 ? Math.Clamp(used.TotalSeconds / (seconds * cores) * 100, 0, 100) : null;
    }

    private static double? Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero || PdhGetFormattedCounterValue(counter, PdhFmtDouble | PdhFmtNoCap100, out _, out var v) != 0 || v.Status != 0)
            return null;
        return v.Double;
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_running) StopLocked();
            _disposed = true;
        }
        _queueTimer.Dispose();
    }

    private const uint PdhFmtDouble = 0x200, PdhFmtNoCap100 = 0x8000, PdhMoreData = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint Status; public double Double; } // the union's double is 8-aligned, after padding

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValueItem { public IntPtr Name; public CounterValue Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}

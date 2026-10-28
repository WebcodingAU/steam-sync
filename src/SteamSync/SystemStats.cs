using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SteamSync;

/// <summary>
/// The PC's load for the PC load sample (PcLoad, beta builds), the numbers Process Lasso graphs: CPU, GPU, memory load and system
/// responsiveness, plus the running game's own CPU. None of it needs admin rights.
/// <para>
/// CPU is "% Processor Utility" (what Task Manager and Process Lasso show, frequency included, capped at 100). GPU is
/// the busiest engine of any GPU, each engine's use summed over its processes, the way Task Manager does it. Memory
/// load is Windows' own figure (GlobalMemoryStatusEx). The CPU's speed is Task Manager's too (base clock times
/// "% Processor Performance"); the GPU's clock comes from NVIDIA's driver (GpuClock), so other GPUs have none. The
/// game's FPS comes from RivaTuner, when it runs (FrameRate).
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
    private IntPtr _query, _cpu, _cpuPerf, _cpuBase, _gpu, _queueQuery, _queue;
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
            double? cpu = null, gpu = null, cpuGhz = null;
            string? gpuEngine = null;
            if (!started && _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
            {
                cpu = Value(_cpu) is { } c ? Math.Min(100, c) : null;
                // The CPU's speed now, like Task Manager's "Speed": its base clock times how fast it runs against it.
                if (Value(_cpuBase) is { } baseMhz and > 0 && Value(_cpuPerf) is { } perf) cpuGhz = baseMhz * perf / 100 / 1000;
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
                ["cpu_ghz"] = cpuGhz is { } ghz ? Math.Round(ghz, 2) : null,
                ["gpu_mhz"] = GpuClock.GraphicsMhz(),
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
                ["fps"] = FrameRate.Read(gamePids) is { } fps ? Math.Round(fps) : null, // from RivaTuner (FrameRate): null without it
            };
        }
    }

    private void StartLocked()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
        if (_query != IntPtr.Zero)
        {
            if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpu) != 0) _cpu = IntPtr.Zero;
            if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _cpuPerf) != 0) _cpuPerf = IntPtr.Zero;
            if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _cpuBase) != 0) _cpuBase = IntPtr.Zero;
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
        _query = _cpu = _cpuPerf = _cpuBase = _gpu = _queueQuery = _queue = IntPtr.Zero;
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

/// <summary>
/// The GPU's graphics clock in MHz, from NVIDIA's management library (nvml.dll, installed in System32 with every NVIDIA
/// driver). The first NVIDIA GPU; null without one, or when the library can't be loaded (then it isn't tried again).
/// </summary>
internal static class GpuClock
{
    private const int NvmlClockGraphics = 0;
    private static bool _tried, _failed;
    private static IntPtr _device;

    public static int? GraphicsMhz()
    {
        if (_failed) return null;
        try
        {
            if (!_tried)
            {
                _tried = true;
                if (nvmlInit_v2() != 0 || nvmlDeviceGetHandleByIndex_v2(0, out _device) != 0)
                {
                    _failed = true;
                    return null;
                }
                Log.Info("GPU clock: reading it from NVIDIA's driver");
            }
            return nvmlDeviceGetClockInfo(_device, NvmlClockGraphics, out var mhz) == 0 ? (int)mhz : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _failed = true; // no NVIDIA driver: no clock
            return null;
        }
    }

    [DllImport("nvml.dll")]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint clock);
}

/// <summary>
/// The game's frame rate from RivaTuner Statistics Server (installed with MSI Afterburner), which counts every frame
/// of each process it hooks and publishes it in its shared memory ("RTSSSharedMemoryV2", readable without admin
/// rights). Per process: frames over RTSS's own window (about a second), so FPS = frames / that window. (Its
/// dwFrameTime isn't used: it sat at the same 4.22 ms for AION 2 at 102 fps and for ExitLag's window.) Null without
/// RTSS, when it doesn't hook the game, or when the game's entry stopped updating (minimised, loading) for 3 s. With
/// several of the game's processes hooked, the one drawing the most frames.
/// </summary>
internal static class FrameRate
{
    private const uint Signature = 0x52545353; // "RTSS"
    private const int NameSize = 260;           // MAX_PATH
    // RTSS_SHARED_MEMORY_APP_ENTRY (v2.x): dwProcessID, szName[MAX_PATH], dwFlags, dwTime0, dwTime1, dwFrames, ...
    private const int Time0 = 4 + NameSize + 4, Time1 = Time0 + 4, Frames = Time1 + 4;

    public static double? Read(IReadOnlyCollection<int> pids)
    {
        if (pids.Count == 0) return null;
        try
        {
            using var map = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != Signature || view.ReadUInt32(4) < 0x00020000) return null;
            uint entrySize = view.ReadUInt32(8), arrayOffset = view.ReadUInt32(12), count = view.ReadUInt32(16);
            var now = (uint)Environment.TickCount; // RTSS's times are GetTickCount milliseconds
            double? best = null;
            for (uint i = 0; i < count; i++)
            {
                long entry = arrayOffset + i * (long)entrySize;
                if (entry + Frames + 4 > view.Capacity) break;
                if (!pids.Contains((int)view.ReadUInt32(entry))) continue;
                uint t0 = view.ReadUInt32(entry + Time0), t1 = view.ReadUInt32(entry + Time1), frames = view.ReadUInt32(entry + Frames);
                if (t1 <= t0 || now - t1 > 3000) continue; // nothing counted lately
                var fps = 1000.0 * frames / (t1 - t0);
                if (best is null || fps > best) best = fps;
            }
            return best;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null; // RTSS isn't running
        }
    }
}

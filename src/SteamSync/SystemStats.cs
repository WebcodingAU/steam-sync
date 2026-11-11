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
/// "% Processor Performance"); the GPU's clock and temperature come from NVIDIA's driver (NvidiaGpu), so other GPUs
/// have none. The CPU's temperature comes from PawnIO through the relay helper when the user turned that on, else it's
/// borrowed from MSI Afterburner or HWiNFO when one is running (CpuTemperature). The
/// game's FPS is counted from Windows' own present events (FrameCounter), or read from RivaTuner when this PC can't
/// run that trace (FrameRate).
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
    private readonly FrameCounter _frames = new();
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

    /// <summary>The load since the last call (about a second), as the sample's "system" object, with the game cpu and
    /// FPS of games[foreground] (the one in front). Fps: every game's frame rate (and Lossless Scaling's, for the one in
    /// front), for the sample's games list.</summary>
    public JsonObject Sample(IReadOnlyList<IReadOnlyCollection<int>> games, int foreground, out (double? Fps, double? Scaled)[] fps)
    {
        lock (_lock)
        {
            fps = new (double? Fps, double? Scaled)[games.Count];
            if (_disposed) return new JsonObject();
            var gamePids = games[foreground];
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
            fps = _frames.Read(games, scaledFor: foreground);
            var (frontFps, scaledFps) = fps[foreground];
            frontFps ??= FrameRate.Read(gamePids);
            var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            var haveMemory = GlobalMemoryStatusEx(ref memory);

            return new JsonObject
            {
                ["cpu_pct"] = Round(cpu),
                ["cpu_ghz"] = cpuGhz is { } ghz ? Math.Round(ghz, 2) : null,
                ["cpu_temp_c"] = CpuTemperature.Read(),
                ["gpu_mhz"] = NvidiaGpu.GraphicsMhz(),
                ["gpu_pct"] = Round(gpu),
                ["gpu_temp_c"] = NvidiaGpu.TemperatureC(),
                ["gpu_engine"] = gpuEngine,
                ["mem_pct"] = haveMemory ? (int)memory.MemoryLoad : null,
                ["mem_used_gb"] = haveMemory ? Round((memory.TotalPhys - memory.AvailPhys) / 1073741824.0) : null,
                ["mem_total_gb"] = haveMemory ? Round(memory.TotalPhys / 1073741824.0) : null,
                ["responsiveness_pct"] = queue is { } q ? Round(100 / (1 + q / (2.0 * cores))) : null,
                ["ready_queue"] = Round(queue),
                ["ready_queue_max"] = Round(queueMax),
                ["cores"] = cores,
                ["game_cpu_pct"] = Round(GameCpu(gamePids, cores)),
                ["fps"] = frontFps is { } f ? Math.Round(f) : null,
                // What reaches the screen when Lossless Scaling scales the game (its frame generation included); fps stays the game's own.
                ["fps_scaled"] = scaledFps is { } s ? Math.Round(s) : null,
                ["fps_scaler"] = scaledFps is null ? null : "Lossless Scaling",
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
        _frames.Dispose();
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
/// The GPU's graphics clock in MHz and its temperature in °C, from NVIDIA's management library (nvml.dll, installed in
/// System32 with every NVIDIA driver, no admin rights needed). The first NVIDIA GPU; null without one, or when the
/// library can't be loaded (then it isn't tried again). The temperature is the GPU's main sensor: hotspot and memory
/// temperatures exist only on some cards.
/// </summary>
internal static class NvidiaGpu
{
    private const int NvmlClockGraphics = 0, NvmlTemperatureGpu = 0;
    private static bool _tried, _failed;
    private static IntPtr _device;

    public static int? GraphicsMhz() => Read(d => nvmlDeviceGetClockInfo(d, NvmlClockGraphics, out var mhz) == 0 ? (int)mhz : null);

    public static int? TemperatureC() =>
        Read(d => nvmlDeviceGetTemperature(d, NvmlTemperatureGpu, out var c) == 0 && c is > 0 and < 150 ? (int)c : null);

    private static int? Read(Func<IntPtr, int?> read)
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
                Log.Info("GPU clock and temperature: reading them from NVIDIA's driver");
            }
            return read(_device);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _failed = true; // no NVIDIA driver: no clock or temperature
            return null;
        }
    }

    [DllImport("nvml.dll")]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint clock);

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temperature);
}

/// <summary>
/// The CPU's temperature in °C. Windows has no proper way to read it: reading the CPU's sensors takes a kernel driver
/// (the common free one, WinRing0, is on Defender's vulnerable-driver list, so the app never ships one). In order:
/// <list type="bullet">
/// <item>PawnIO (PawnIo), when the user turned "CPU temperature" on in the PC load menu: the relay helper reads Tctl/Tdie
/// on AMD and CPU Package on Intel.</item>
/// <item>MSI Afterburner ("MAHMSharedMemory"): its "CPU temperature" source, or the hottest CPU temperature source.</item>
/// <item>HWiNFO ("Global\HWiNFO_SENS_SM2", only with its "Shared Memory Support" setting on): CPU (Tctl/Tdie) on AMD,
/// CPU Package on Intel.</item>
/// </list>
/// The two tools publish their readings in shared memory that any program can read without admin rights. Null when
/// none of the three has a reading.
/// </summary>
internal static class CpuTemperature
{
    private static string? _source;

    public static double? Read()
    {
        var (c, source) = PawnIo.Read() is { } p ? (p, "PawnIO")
            : Afterburner() is { } a ? (a, "MSI Afterburner")
            : HwInfo() is { } h ? (h, "HWiNFO")
            : ((double?)null, (string?)null);
        if (source != _source)
        {
            _source = source;
            Log.Info(source is null ? "CPU temperature: no sensor tool is sharing it" : $"CPU temperature: reading it from {source}");
        }
        return c is { } x ? Math.Round(x, 1) : null;
    }

    private const uint MahmSignature = 0x4D41484D; // "MAHM"
    private const int MaxPath = 260;
    private const uint MahmCpuTemperature = 0x80;  // MONITORING_SOURCE_ID_CPU_TEMPERATURE

    /// <summary>MAHM v2: a header (dwSignature, dwVersion, dwHeaderSize, dwNumEntries, dwEntrySize, ...), then entries of
    /// five MAX_PATH strings (name, units, localised name and units, format), float data (FLT_MAX: no reading),
    /// minLimit, maxLimit, dwFlags, dwGpu, dwSrcId.</summary>
    private static double? Afterburner(string name = "MAHMSharedMemory")
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != MahmSignature || view.ReadUInt32(4) < 0x00020000) return null;
            uint headerSize = view.ReadUInt32(8), count = view.ReadUInt32(12), entrySize = view.ReadUInt32(16);
            const int Data = 5 * MaxPath, SrcId = Data + 4 * 4 + 4;
            if (entrySize < SrcId + 4) return null;
            double? named = null, hottest = null;
            for (uint i = 0; i < count; i++)
            {
                long entry = headerSize + i * (long)entrySize;
                if (entry + SrcId + 4 > view.Capacity) break;
                if (view.ReadUInt32(entry + SrcId) != MahmCpuTemperature) continue;
                var c = view.ReadSingle(entry + Data);
                if (c is not (> 0 and < 150)) continue; // FLT_MAX: not read yet
                if (AnsiString(view, entry, MaxPath) == "CPU temperature") named = c;
                hottest = Math.Max(hottest ?? c, c);
            }
            return named ?? hottest;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null; // Afterburner isn't running
        }
    }

    private const uint HwInfoSignature = 0x53695748; // "HWiS"
    private const int HwInfoTemperature = 1;          // SENSOR_READING_TYPE SENSOR_TYPE_TEMP
    private const int LabelSize = 128, UnitSize = 16;

    // Best first: the package/die figure each vendor's own tools show, then the hottest core.
    private static readonly string[] HwInfoLabels = ["CPU (Tctl/Tdie)", "CPU Package", "CPU (Tdie)", "CPU (Tctl)", "CPU Die (average)", "Core Max"];

    /// <summary>HWiNFO's packed header: dwSignature, dwVersion, dwRevision, poll_time (64-bit), then the sensor section's
    /// offset, element size and count, and the reading section's offset (32), element size (36) and count (40). A reading
    /// is tReading, dwSensorIndex, dwReadingID, szLabelOrig[128], szLabelUser[128], szUnit[16], then double Value.</summary>
    private static double? HwInfo(string name = @"Global\HWiNFO_SENS_SM2")
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != HwInfoSignature) return null; // "DEAD": HWiNFO closed or sharing stopped
            uint offset = view.ReadUInt32(32), size = view.ReadUInt32(36), count = view.ReadUInt32(40);
            const int Label = 12, Unit = Label + 2 * LabelSize, Value = Unit + UnitSize;
            if (size < Value + 8) return null;
            var best = int.MaxValue;
            double? found = null;
            for (uint i = 0; i < count; i++)
            {
                long reading = offset + i * (long)size;
                if (reading + Value + 8 > view.Capacity) break;
                if (view.ReadInt32(reading) != HwInfoTemperature) continue;
                var rank = Array.IndexOf(HwInfoLabels, AnsiString(view, reading + Label, LabelSize));
                if (rank < 0 || rank >= best) continue;
                var c = view.ReadDouble(reading + Value);
                if (AnsiString(view, reading + Unit, UnitSize).EndsWith('F')) c = (c - 32) * 5 / 9; // HWiNFO set to °F
                if (c is not (> 0 and < 150)) continue;
                best = rank;
                found = c;
            }
            return found;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null; // HWiNFO isn't running, or its shared memory support is off
        }
    }

    private static string AnsiString(MemoryMappedViewAccessor view, long at, int size)
    {
        var bytes = new byte[size];
        view.ReadArray(at, bytes, 0, size);
        var end = Array.IndexOf(bytes, (byte)0);
        return System.Text.Encoding.Latin1.GetString(bytes, 0, end < 0 ? size : end);
    }
}

/// <summary>
/// The fallback for FrameCounter, on PCs where this user can't start a trace: the game's frame rate from RivaTuner
/// Statistics Server (installed with MSI Afterburner), which counts every frame
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

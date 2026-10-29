using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamSync;

/// <summary>
/// The game's frame rate counted by Windows itself, so RivaTuner isn't needed: a real-time ETW session on the
/// graphics stack's own present events, the same ones PresentMon reads. Nothing is injected into the game.
/// <para>
/// Each frame a game shows is one present call. DXGI (Direct3D 10/11/12) logs Present (event 42) or
/// PresentMultiplaneOverlay (55) in the game's process, D3D9 logs Present (1), and the kernel (DxgKrnl) logs Present
/// (184) or PresentHistoryDetailed (215) for every API, Vulkan and OpenGL included. Per process, the frame count is
/// the larger of the API's and the kernel's (they match one to one for DXGI games), and the frame rate is frames over
/// the time between the first and last present seen since the previous read, from the events' own timestamps, so
/// ETW's delivery delay doesn't skew it. With several of the game's processes presenting, the one drawing the most.
/// </para>
/// <para>
/// Starting a trace session needs admin rights or the Performance Log Users group (the tray app runs as the user, so
/// it's the group). Without it, Read returns null and SystemStats falls back to RivaTuner (FrameRate). The session
/// starts on the first read and stops when nothing has asked for a few seconds (no game running). The provider and
/// event ids are filtered in the kernel, so only these few events per frame are delivered.
/// </para>
/// </summary>
internal sealed unsafe class FrameCounter : IDisposable
{
    private const string SessionName = "GamingEnjoyerFrames";
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private static readonly Guid Dxgi = new("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
    private static readonly Guid D3d9 = new("783aca0a-790e-4d7f-8451-aa850511c6b9");
    private static readonly Guid DxgKrnl = new("802ec45a-1e99-4b83-9920-87c98277ba9d");
    private const ushort DxgiPresent = 42, DxgiPresentMpo = 55, D3d9Present = 1, KernelPresent = 184, KernelPresentHistory = 215;
    private const ulong KernelKeywordPresent = 0x8000000;

    private readonly object _lock = new();
    private readonly GCHandle _self;
    private readonly System.Threading.Timer _idleTimer;
    private readonly Dictionary<uint, Frames> _frames = [];
    private ulong _session;
    private ulong _consumer = InvalidHandle;
    private IntPtr _properties;
    private Thread? _thread;
    private long _lastReadAt, _retryAt;
    private bool _disposed;

    /// <summary>Presents per process since the last read: by the API (DXGI/D3D9) and by the kernel, with the first and
    /// last timestamps (QueryPerformanceCounter ticks) of each.</summary>
    private sealed class Frames
    {
        public Span Api, Kernel;
        public struct Span
        {
            public int Count;
            public long First, Last;
            public void Add(long at)
            {
                if (Count++ == 0) First = at;
                Last = at;
            }
            public readonly double? Fps => Count >= 2 && Last > First ? (Count - 1) * (double)Stopwatch.Frequency / (Last - First) : null;
        }
    }

    public FrameCounter()
    {
        _self = GCHandle.Alloc(this);
        _idleTimer = new(_ => StopIfIdle());
    }

    /// <summary>The frame rate of the busiest of these processes since the last read, or null (no presents counted,
    /// the session couldn't start, or it has only just started).</summary>
    public double? Read(IReadOnlyCollection<int> pids)
    {
        lock (_lock)
        {
            if (_disposed || pids.Count == 0) return null;
            _lastReadAt = Stopwatch.GetTimestamp();
            if (_session == 0)
            {
                if (_lastReadAt < _retryAt) return null;
                if (!StartLocked())
                {
                    _retryAt = _lastReadAt + (long)(RetryAfter.TotalSeconds * Stopwatch.Frequency);
                    return null;
                }
                return null; // the first frames arrive with the next read
            }

            double? best = null;
            foreach (var pid in pids)
            {
                if (!_frames.TryGetValue((uint)pid, out var f)) continue;
                var fps = f.Api.Count >= f.Kernel.Count ? f.Api.Fps : f.Kernel.Fps;
                if (fps is { } v && (best is null || v > best)) best = v;
            }
            _frames.Clear();
            return best;
        }
    }

    private bool StartLocked()
    {
        _properties = _properties == IntPtr.Zero ? NewProperties() : NewPropertiesInto(_properties);
        var error = StartTraceW(out _session, SessionName, _properties);
        if (error == ErrorAlreadyExists)
        {
            // Left over from a copy that didn't stop cleanly: stop it and start fresh.
            ControlTraceW(0, SessionName, NewPropertiesInto(_properties), ControlStop);
            error = StartTraceW(out _session, SessionName, NewPropertiesInto(_properties));
        }
        if (error != 0)
        {
            _session = 0;
            Log.Info(error == ErrorAccessDenied
                ? "FPS: Windows won't start a trace for this user (not in Performance Log Users); using RivaTuner if it runs"
                : $"FPS: couldn't start the frame trace ({error}); using RivaTuner if it runs");
            return false;
        }

        var enabled = Enable(Dxgi, 0, [DxgiPresent, DxgiPresentMpo])
            | Enable(D3d9, 0, [D3d9Present])
            | Enable(DxgKrnl, KernelKeywordPresent, [KernelPresent, KernelPresentHistory]);
        if (!enabled)
        {
            Log.Info("FPS: none of the graphics providers could be turned on; using RivaTuner if it runs");
            StopLocked();
            return false;
        }

        // EVENT_TRACE_LOGFILEW is 448 bytes on x64; only these fields matter for a real-time event-record consumer.
        var logfile = stackalloc byte[LogFileSize];
        new Span<byte>(logfile, LogFileSize).Clear();
        var name = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            *(IntPtr*)(logfile + LogFileLoggerName) = name;
            *(uint*)(logfile + LogFileProcessTraceMode) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord;
            *(IntPtr*)(logfile + LogFileEventRecordCallback) = (IntPtr)(delegate* unmanaged<EventRecord*, void>)&OnEvent;
            *(IntPtr*)(logfile + LogFileContext) = GCHandle.ToIntPtr(_self);
            _consumer = OpenTraceW(logfile);
        }
        finally { Marshal.FreeHGlobal(name); }
        if (_consumer == InvalidHandle)
        {
            Log.Info($"FPS: couldn't open the frame trace ({Marshal.GetLastPInvokeError()}); using RivaTuner if it runs");
            StopLocked();
            return false;
        }

        _frames.Clear();
        _thread = new Thread(Pump) { IsBackground = true, Name = "FPS trace" };
        _thread.Start();
        _idleTimer.Change(IdleAfter, IdleAfter);
        Log.Info("FPS: counting frames from Windows' present events");
        return true;
    }

    /// <summary>Turns on one provider for just these event ids, filtered in the kernel (ENABLE_TRACE_PARAMETERS v2
    /// with an EVENT_FILTER_TYPE_EVENT_ID descriptor). False when Windows refuses it.</summary>
    private bool Enable(Guid provider, ulong keywords, ushort[] ids)
    {
        // EVENT_FILTER_EVENT_ID: FilterIn, Reserved, Count, then the ids.
        var size = 4 + ids.Length * 2;
        var filterIds = stackalloc byte[size];
        filterIds[0] = 1;
        filterIds[1] = 0;
        *(ushort*)(filterIds + 2) = (ushort)ids.Length;
        for (var i = 0; i < ids.Length; i++) *(ushort*)(filterIds + 4 + i * 2) = ids[i];
        // EVENT_FILTER_DESCRIPTOR: Ptr, Size, Type.
        var filter = stackalloc byte[16];
        *(ulong*)filter = (ulong)filterIds;
        *(uint*)(filter + 8) = (uint)size;
        *(uint*)(filter + 12) = 0x80000200; // EVENT_FILTER_TYPE_EVENT_ID
        // ENABLE_TRACE_PARAMETERS (x64, 48 bytes): Version, EnableProperty, ControlFlags, SourceId, EnableFilterDesc, FilterDescCount.
        var parameters = stackalloc byte[48];
        new Span<byte>(parameters, 48).Clear();
        *(uint*)parameters = 2;
        *(IntPtr*)(parameters + 32) = (IntPtr)filter;
        *(uint*)(parameters + 40) = 1;

        var error = EnableTraceEx2(_session, &provider, EnableProvider, LevelVerbose, keywords, 0, 0, (IntPtr)parameters);
        if (error != 0) Log.Info($"FPS: provider {provider} unavailable ({error})");
        return error == 0;
    }

    private void Pump()
    {
        var handle = _consumer;
        var error = ProcessTrace(&handle, 1, IntPtr.Zero, IntPtr.Zero);
        if (error != 0 && error != ErrorCancelled) Log.Info($"FPS: frame trace ended ({error})");
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(EventRecord* record)
    {
        try
        {
            if (record->UserContext != IntPtr.Zero)
                ((FrameCounter)GCHandle.FromIntPtr(record->UserContext).Target!).Handle(record);
        }
        catch { } // never let an exception unwind into ETW
    }

    private void Handle(EventRecord* record)
    {
        bool api;
        if (record->ProviderId == DxgKrnl && record->EventId is KernelPresent or KernelPresentHistory) api = false;
        else if (record->ProviderId == Dxgi && record->EventId is DxgiPresent or DxgiPresentMpo) api = true;
        else if (record->ProviderId == D3d9 && record->EventId == D3d9Present) api = true;
        else return;

        lock (_lock)
        {
            if (!_frames.TryGetValue(record->ProcessId, out var f)) _frames[record->ProcessId] = f = new Frames();
            if (api) f.Api.Add(record->TimeStamp);
            else f.Kernel.Add(record->TimeStamp);
        }
    }

    private void StopIfIdle()
    {
        lock (_lock)
        {
            if (_session != 0 && Stopwatch.GetElapsedTime(_lastReadAt) > IdleAfter)
            {
                StopLocked(); // the game stopped: nothing asks any more
                Log.Info("FPS: frame trace stopped");
            }
        }
    }

    private void StopLocked()
    {
        _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_session != 0)
        {
            // Stopping the session ends ProcessTrace on the consumer thread.
            ControlTraceW(_session, null, NewPropertiesInto(_properties), ControlStop);
            _session = 0;
        }
        if (_consumer != InvalidHandle)
        {
            CloseTrace(_consumer);
            _consumer = InvalidHandle;
        }
        _thread = null; // a background thread that ends by itself once the session stops
        _frames.Clear();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            StopLocked();
            _disposed = true;
        }
        _idleTimer.Dispose();
        if (_properties != IntPtr.Zero) Marshal.FreeHGlobal(_properties);
        _properties = IntPtr.Zero;
        // The GCHandle stays allocated: a last event may still be in flight on the consumer thread.
    }

    // ---- EVENT_TRACE_PROPERTIES: 120 bytes, then room for the session name that StartTrace copies in. ----

    private const int PropertiesSize = 120, PropertiesWithName = PropertiesSize + 2048;

    private static IntPtr NewProperties() => NewPropertiesInto(Marshal.AllocHGlobal(PropertiesWithName));

    /// <summary>(Re)fills the buffer: StartTrace and ControlTrace both write back into it, so every call gets it fresh.</summary>
    private static IntPtr NewPropertiesInto(IntPtr buffer)
    {
        var p = (byte*)buffer;
        new Span<byte>(p, PropertiesWithName).Clear();
        *(uint*)(p + 0) = PropertiesWithName;           // Wnode.BufferSize
        *(uint*)(p + 40) = 1;                           // Wnode.ClientContext: QueryPerformanceCounter timestamps
        *(uint*)(p + 44) = 0x00020000;                  // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        *(uint*)(p + 48) = 64;                          // BufferSize (KB)
        *(uint*)(p + 52) = 4;                           // MinimumBuffers
        *(uint*)(p + 56) = 16;                          // MaximumBuffers
        *(uint*)(p + 64) = 0x00000100;                  // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        *(uint*)(p + 68) = 1;                           // FlushTimer (s): events reach us within a second
        *(uint*)(p + 116) = PropertiesSize;             // LoggerNameOffset
        return buffer;
    }

    // ---- advapi32 ----

    private const uint ErrorAccessDenied = 5, ErrorAlreadyExists = 183, ErrorCancelled = 1223;
    private const uint ControlStop = 1, EnableProvider = 1;
    private const byte LevelVerbose = 5;
    private const ulong InvalidHandle = ulong.MaxValue;
    private const uint ProcessTraceModeRealTime = 0x00000100, ProcessTraceModeEventRecord = 0x10000000;
    private const int LogFileSize = 448, LogFileLoggerName = 8, LogFileProcessTraceMode = 28,
        LogFileEventRecordCallback = 424, LogFileContext = 440;

    /// <summary>EVENT_RECORD (x64): the EVENT_HEADER fields used here, then UserContext.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct EventRecord
    {
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long TimeStamp;
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort EventId;
        [FieldOffset(104)] public IntPtr UserContext;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong handle, string? name, IntPtr properties, uint control);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong handle, Guid* provider, uint control, byte level,
        ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(byte* logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong handle);
}

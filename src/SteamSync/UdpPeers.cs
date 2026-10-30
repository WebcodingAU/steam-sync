using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamSync;

/// <summary>
/// The UDP servers the games' processes talk to, which the TCP table can't show (Tarkov's raids: UDP to Gcore servers,
/// 92.38.165.141:17014, about 11 packets a second each way). A real-time ETW session on Microsoft-Windows-TCPIP's UDP
/// send (1169) and deliver (1170) events, which carry each batch's remote address and the owning process id. Like
/// FrameCounter it needs admin rights or the Performance Log Users group (the tray app runs as the user, so it's the
/// group), and it uses the same plumbing; see FrameCounter for the EVENT_TRACE_PROPERTIES and ENABLE_TRACE_PARAMETERS
/// layouts. The session starts on the first read and stops when nothing has asked for a while (no game running).
/// </summary>
internal sealed unsafe class UdpPeers : IDisposable
{
    private const string SessionName = "GamingEnjoyerUdp";
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly Guid TcpIp = new("2f07e2ee-15db-40f1-90ef-9d7ba282188a");
    private const ushort UdpSend = 1169, UdpDeliver = 1170;

    /// <summary>One process's traffic with one UDP server: batches sent and delivered, and when it was first and last
    /// seen (Stopwatch timestamps).</summary>
    public readonly record struct Peer(int Pid, uint Remote, int Port, long Sent, long Received, long FirstSeen, long LastSeen)
    {
        public TimeSpan Age => Stopwatch.GetElapsedTime(FirstSeen);
        public TimeSpan Quiet => Stopwatch.GetElapsedTime(LastSeen);
    }

    private sealed class Counts
    {
        public long Sent, Received, FirstSeen, LastSeen;
    }

    private readonly object _lock = new();
    private readonly GCHandle _self;
    private readonly System.Threading.Timer _idleTimer;
    private readonly Dictionary<(int Pid, uint Remote, int Port), Counts> _peers = [];
    private ulong _session;
    private ulong _consumer = InvalidHandle;
    private IntPtr _properties;
    private long _lastReadAt, _retryAt;
    private bool _disposed;

    public UdpPeers()
    {
        _self = GCHandle.Alloc(this);
        _idleTimer = new(_ => StopIfIdle());
    }

    /// <summary>These processes' UDP servers heard from in the last 30 seconds, busiest first. Empty while the session
    /// can't start (no Performance Log Users rights) or has only just started.</summary>
    public IReadOnlyList<Peer> Read(IReadOnlyCollection<int> pids)
    {
        lock (_lock)
        {
            if (_disposed || pids.Count == 0) return [];
            _lastReadAt = Stopwatch.GetTimestamp();
            if (_session == 0)
            {
                if (_lastReadAt >= _retryAt && !StartLocked())
                    _retryAt = _lastReadAt + (long)(RetryAfter.TotalSeconds * Stopwatch.Frequency);
                return [];
            }
            foreach (var gone in _peers.Where(p => Stopwatch.GetElapsedTime(p.Value.LastSeen) > ForgetAfter).Select(p => p.Key).ToList())
                _peers.Remove(gone);
            return _peers.Where(p => pids.Contains(p.Key.Pid))
                .Select(p => new Peer(p.Key.Pid, p.Key.Remote, p.Key.Port, p.Value.Sent, p.Value.Received, p.Value.FirstSeen, p.Value.LastSeen))
                .OrderByDescending(p => p.Sent + p.Received).ToList();
        }
    }

    private bool StartLocked()
    {
        _properties = _properties == IntPtr.Zero ? NewProperties() : NewPropertiesInto(_properties);
        var error = StartTraceW(out _session, SessionName, _properties);
        if (error == ErrorAlreadyExists)
        {
            ControlTraceW(0, SessionName, NewPropertiesInto(_properties), ControlStop); // left over from a copy that didn't stop cleanly
            error = StartTraceW(out _session, SessionName, NewPropertiesInto(_properties));
        }
        if (error != 0)
        {
            _session = 0;
            Log.Info(error == ErrorAccessDenied
                ? "Relay: Windows won't start a UDP trace for this user (not in Performance Log Users); only TCP game servers are learned"
                : $"Relay: couldn't start the UDP trace ({error})");
            return false;
        }

        // Only the two UDP events, filtered in the kernel (EVENT_FILTER_EVENT_ID in ENABLE_TRACE_PARAMETERS v2).
        var filterIds = stackalloc byte[8];
        filterIds[0] = 1;
        filterIds[1] = 0;
        *(ushort*)(filterIds + 2) = 2;
        *(ushort*)(filterIds + 4) = UdpSend;
        *(ushort*)(filterIds + 6) = UdpDeliver;
        var filter = stackalloc byte[16];
        *(ulong*)filter = (ulong)filterIds;
        *(uint*)(filter + 8) = 8;
        *(uint*)(filter + 12) = 0x80000200; // EVENT_FILTER_TYPE_EVENT_ID
        var parameters = stackalloc byte[48];
        new Span<byte>(parameters, 48).Clear();
        *(uint*)parameters = 2;
        *(IntPtr*)(parameters + 32) = (IntPtr)filter;
        *(uint*)(parameters + 40) = 1;
        var provider = TcpIp;
        error = EnableTraceEx2(_session, &provider, EnableProvider, LevelVerbose, ulong.MaxValue, 0, 0, (IntPtr)parameters);
        if (error != 0)
        {
            Log.Info($"Relay: the TCP/IP provider is unavailable for the UDP trace ({error})");
            StopLocked();
            return false;
        }

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
            Log.Info($"Relay: couldn't open the UDP trace ({Marshal.GetLastPInvokeError()})");
            StopLocked();
            return false;
        }

        _peers.Clear();
        new Thread(Pump) { IsBackground = true, Name = "UDP trace" }.Start();
        _idleTimer.Change(IdleAfter, IdleAfter);
        Log.Info("Relay: watching the games' UDP servers");
        return true;
    }

    private void Pump()
    {
        var handle = _consumer;
        ProcessTrace(&handle, 1, IntPtr.Zero, IntPtr.Zero); // returns once the session stops
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(EventRecord* record)
    {
        try
        {
            if (record->UserContext != IntPtr.Zero)
                ((UdpPeers)GCHandle.FromIntPtr(record->UserContext).Target!).Handle(record);
        }
        catch { } // never let an exception unwind into ETW
    }

    /// <summary>
    /// Event 1169/1170 v1: Endpoint (8 bytes), NumMessages (4), NumBytes (4), LocalSockAddr's length (4) and the
    /// SOCKADDR, RemoteSockAddr's length (4) and the SOCKADDR, Pid (4), ProcessStartKey (8). The event header's process
    /// is often System for deliveries, so the payload's Pid is the one used. IPv4 only.
    /// </summary>
    private void Handle(EventRecord* record)
    {
        if (record->ProviderId != TcpIp || record->EventId is not (UdpSend or UdpDeliver)) return;
        var d = (byte*)record->UserData;
        int length = record->UserDataLength;
        if (length < 24) return;
        var remoteAt = 20 + *(int*)(d + 16);
        if (remoteAt + 4 > length) return;
        var remoteLength = *(int*)(d + remoteAt);
        var remote = d + remoteAt + 4;
        if (remoteLength < 8 || remoteAt + 4 + remoteLength + 4 > length || *(ushort*)remote != 2) return; // AF_INET
        var key = (*(int*)(remote + remoteLength), *(uint*)(remote + 4), remote[2] << 8 | remote[3]);
        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (!_peers.TryGetValue(key, out var c)) _peers[key] = c = new Counts { FirstSeen = now };
            if (record->EventId == UdpSend) c.Sent++;
            else c.Received++;
            c.LastSeen = now;
        }
    }

    private void StopIfIdle()
    {
        lock (_lock)
            if (_session != 0 && Stopwatch.GetElapsedTime(_lastReadAt) > IdleAfter)
            {
                StopLocked();
                Log.Info("Relay: UDP trace stopped");
            }
    }

    private void StopLocked()
    {
        _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_session != 0)
        {
            ControlTraceW(_session, null, NewPropertiesInto(_properties), ControlStop); // ends ProcessTrace on its thread
            _session = 0;
        }
        if (_consumer != InvalidHandle)
        {
            CloseTrace(_consumer);
            _consumer = InvalidHandle;
        }
        _peers.Clear();
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

    // ---- EVENT_TRACE_PROPERTIES and advapi32, as in FrameCounter ----

    private const int PropertiesSize = 120, PropertiesWithName = PropertiesSize + 2048;

    private static IntPtr NewProperties() => NewPropertiesInto(Marshal.AllocHGlobal(PropertiesWithName));

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
        *(uint*)(p + 68) = 1;                           // FlushTimer (s)
        *(uint*)(p + 116) = PropertiesSize;             // LoggerNameOffset
        return buffer;
    }

    private const uint ErrorAccessDenied = 5, ErrorAlreadyExists = 183;
    private const uint ControlStop = 1, EnableProvider = 1;
    private const byte LevelVerbose = 5;
    private const ulong InvalidHandle = ulong.MaxValue;
    private const uint ProcessTraceModeRealTime = 0x00000100, ProcessTraceModeEventRecord = 0x10000000;
    private const int LogFileSize = 448, LogFileLoggerName = 8, LogFileProcessTraceMode = 28,
        LogFileEventRecordCallback = 424, LogFileContext = 440;

    /// <summary>EVENT_RECORD (x64): the EVENT_HEADER fields used here, the user data, then UserContext.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct EventRecord
    {
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort EventId;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public IntPtr UserData;
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

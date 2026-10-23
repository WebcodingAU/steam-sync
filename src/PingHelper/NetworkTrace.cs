using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PingHelper;

/// <summary>
/// A real-time ETW session on Microsoft-Windows-Kernel-Network: one event per TCP or UDP packet a process sends or
/// receives, with the process id and the remote address and port. Windows lets only SYSTEM, Administrators and
/// LOCAL SERVICE turn this provider on, which is why the helper is a LocalService service.
/// <para>
/// Plain P/Invoke against advapi32 (StartTrace, EnableTraceEx2, OpenTrace, ProcessTrace) rather than a tracing
/// library, so the exe stays small and trims safely. The payload layouts are the provider's manifest (the same fields
/// tracerpt shows): PID, size, daddr, saddr, dport, sport, ... with ports in network byte order. daddr is the remote
/// end except on UDP receives (see Handle).
/// </para>
/// </summary>
internal sealed unsafe class NetworkTrace : IDisposable
{
    private const string SessionName = "GamingEnjoyerPing";
    private static readonly Guid KernelNetwork = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    private const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;

    // Event ids from the provider's manifest.
    private const ushort TcpV4Send = 10, TcpV4Recv = 11, TcpV6Send = 26, TcpV6Recv = 27;
    private const ushort UdpV4Send = 42, UdpV4Recv = 43, UdpV6Send = 58, UdpV6Recv = 59;

    private readonly FlowTable _flows;
    private readonly GCHandle _self;
    private ulong _session;
    private ulong _consumer = InvalidHandle;
    private IntPtr _properties;
    private Thread? _thread;

    public NetworkTrace(FlowTable flows)
    {
        _flows = flows;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>Starts the session and the thread that reads it. Throws with the Windows error when it can't.</summary>
    public void Start()
    {
        _properties = NewProperties();
        var error = StartTraceW(out _session, SessionName, _properties);
        if (error == ErrorAlreadyExists)
        {
            // Left over from a run that didn't stop cleanly: stop it and start fresh.
            ControlTraceW(0, SessionName, NewPropertiesInto(_properties), ControlStop);
            error = StartTraceW(out _session, SessionName, NewPropertiesInto(_properties));
        }
        if (error != 0) throw new System.ComponentModel.Win32Exception((int)error, $"StartTrace failed ({error})");

        var provider = KernelNetwork;
        error = EnableTraceEx2(_session, &provider, EnableProvider, LevelVerbose, KeywordIpv4 | KeywordIpv6, 0, 0, IntPtr.Zero);
        if (error != 0)
        {
            Stop();
            throw new System.ComponentModel.Win32Exception((int)error, $"EnableTraceEx2 failed ({error})");
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
            var openError = Marshal.GetLastPInvokeError();
            Stop();
            throw new System.ComponentModel.Win32Exception(openError, $"OpenTrace failed ({openError})");
        }

        _thread = new Thread(Pump) { IsBackground = true, Name = "ETW consumer" };
        _thread.Start();
    }

    private void Pump()
    {
        var handle = _consumer;
        var error = ProcessTrace(&handle, 1, IntPtr.Zero, IntPtr.Zero);
        if (error != 0 && error != ErrorCancelled) Log.Write($"ProcessTrace ended: {error}");
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(EventRecord* record)
    {
        try
        {
            if (record->ProviderId != KernelNetwork || record->UserContext == IntPtr.Zero) return;
            var trace = (NetworkTrace)GCHandle.FromIntPtr(record->UserContext).Target!;
            trace.Handle(record);
        }
        catch { } // never let an exception unwind into ETW
    }

    private void Handle(EventRecord* record)
    {
        var data = (byte*)record->UserData;
        var length = record->UserDataLength;
        var id = record->EventId;
        bool udp, send, v6;
        switch (id)
        {
            case UdpV4Send: udp = true; send = true; v6 = false; break;
            case UdpV4Recv: udp = true; send = false; v6 = false; break;
            case UdpV6Send: udp = true; send = true; v6 = true; break;
            case UdpV6Recv: udp = true; send = false; v6 = true; break;
            case TcpV4Send: udp = false; send = true; v6 = false; break;
            case TcpV4Recv: udp = false; send = false; v6 = false; break;
            case TcpV6Send: udp = false; send = true; v6 = true; break;
            case TcpV6Recv: udp = false; send = false; v6 = true; break;
            default: return;
        }

        // PID, size, daddr, saddr, dport, sport: 4 + 4 + (4|16) * 2 + 2 + 2 bytes.
        int addr = v6 ? 16 : 4, need = 8 + addr * 2 + 4;
        if (data is null || length < need) return;

        var pid = *(uint*)data;
        var size = *(uint*)(data + 4);
        var ports = data + 8 + addr * 2;
        // daddr/dport are the remote end, except on a UDP receive, where they are this PC (seen in practice: the
        // receive events name the local address as daddr), so the remote end is saddr/sport there.
        var swap = udp && !send;
        var remote = swap ? data + 8 + addr : data + 8;
        var remotePort = (ushort)(swap ? ports[2] << 8 | ports[3] : ports[0] << 8 | ports[1]);
        var localPort = (ushort)(swap ? ports[0] << 8 | ports[1] : ports[2] << 8 | ports[3]);

        ulong hi, lo;
        if (v6)
        {
            hi = BigEndian(remote);
            lo = BigEndian(remote + 8);
        }
        else
        {
            // IPv4 kept as an IPv4-mapped IPv6 address, so both fit one key.
            hi = 0;
            lo = 0x0000FFFF00000000UL | (uint)(remote[0] << 24 | remote[1] << 16 | remote[2] << 8 | remote[3]);
        }
        _flows.Add(new FlowKey(pid, udp, hi, lo, remotePort, localPort), send, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong BigEndian(byte* p)
    {
        ulong v = 0;
        for (var i = 0; i < 8; i++) v = v << 8 | p[i];
        return v;
    }

    public void Stop()
    {
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
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        if (_properties != IntPtr.Zero) Marshal.FreeHGlobal(_properties);
        _properties = IntPtr.Zero;
        if (_self.IsAllocated) _self.Free();
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
        *(uint*)(p + 56) = 32;                          // MaximumBuffers
        *(uint*)(p + 64) = 0x00000100;                  // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        *(uint*)(p + 68) = 1;                           // FlushTimer (s): events reach us within a second
        *(uint*)(p + 116) = PropertiesSize;             // LoggerNameOffset
        return buffer;
    }

    // ---- advapi32 ----

    private const uint ErrorAlreadyExists = 183, ErrorCancelled = 1223;
    private const uint ControlStop = 1, EnableProvider = 1;
    private const byte LevelVerbose = 5;
    private const ulong InvalidHandle = ulong.MaxValue;
    private const uint ProcessTraceModeRealTime = 0x00000100, ProcessTraceModeEventRecord = 0x10000000;
    private const int LogFileSize = 448, LogFileLoggerName = 8, LogFileProcessTraceMode = 28,
        LogFileEventRecordCallback = 424, LogFileContext = 440;

    /// <summary>EVENT_RECORD (x64): the EVENT_HEADER fields used here, then UserDataLength, UserData, UserContext.</summary>
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

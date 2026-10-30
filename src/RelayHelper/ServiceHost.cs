using System.Runtime.InteropServices;

namespace RelayHelper;

/// <summary>Runs Engine as a Windows service through the service control manager (plain advapi32, no ServiceBase).</summary>
internal static unsafe class ServiceHost
{
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint StateStopped = 1, StateStartPending = 2, StateStopPending = 3, StateRunning = 4;
    private const uint ControlStop = 1, ControlInterrogate = 4, ControlShutdown = 5;
    private const uint AcceptStop = 1, AcceptShutdown = 4;
    private const int ErrorFailedServiceControllerConnect = 1063;

    private static IntPtr _status;
    private static readonly ManualResetEventSlim Stop = new();

    /// <summary>Hands the process to the service control manager. False when it wasn't started as a service.</summary>
    public static bool Run()
    {
        var name = Marshal.StringToHGlobalUni(Program.ServiceName);
        var table = stackalloc ServiceTableEntry[2];
        table[0] = new ServiceTableEntry { Name = name, Proc = (IntPtr)(delegate* unmanaged<uint, IntPtr, void>)&ServiceMain };
        table[1] = default;
        var ok = StartServiceCtrlDispatcherW(table);
        var error = Marshal.GetLastPInvokeError();
        Marshal.FreeHGlobal(name);
        if (!ok && error != ErrorFailedServiceControllerConnect) Log.Write($"StartServiceCtrlDispatcher failed: {error}");
        return ok;
    }

    [UnmanagedCallersOnly]
    private static void ServiceMain(uint argc, IntPtr argv)
    {
        _status = RegisterServiceCtrlHandlerExW(Program.ServiceName, &Handler, IntPtr.Zero);
        if (_status == IntPtr.Zero) return;
        Report(StateStartPending, waitHint: 10_000);

        Engine? engine = null;
        try
        {
            engine = new Engine();
            engine.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not start: {ex.Message}");
            engine?.Dispose();
            Report(StateStopped, exitCode: 1064); // ERROR_EXCEPTION_IN_SERVICE
            return;
        }

        Report(StateRunning);
        Stop.Wait();
        Report(StateStopPending, waitHint: 10_000);
        engine.Dispose();
        Report(StateStopped);
    }

    [UnmanagedCallersOnly]
    private static uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ControlStop:
            case ControlShutdown:
                Report(StateStopPending, waitHint: 10_000);
                Stop.Set();
                return 0;
            case ControlInterrogate:
                return 0;
            default:
                return 120; // ERROR_CALL_NOT_IMPLEMENTED
        }
    }

    private static uint _checkPoint;

    private static void Report(uint state, uint exitCode = 0, uint waitHint = 0)
    {
        var pending = state is StateStartPending or StateStopPending;
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == StateRunning ? AcceptStop | AcceptShutdown : 0,
            Win32ExitCode = exitCode,
            CheckPoint = pending ? ++_checkPoint : 0,
            WaitHint = waitHint,
        };
        SetServiceStatus(_status, &status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry { public IntPtr Name; public IntPtr Proc; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcherW(ServiceTableEntry* table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string name, delegate* unmanaged<uint, uint, IntPtr, IntPtr, uint> handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ServiceStatus* status);
}

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SteamSync;

/// <summary>
/// Ctrl+Shift + a mouse side button (back or forward), from any app: the Library window's shortcut. Windows can't
/// register a mouse button as a hotkey, so this is a low-level mouse hook. Windows waits for a hook before it moves
/// the pointer, so the hook runs on its own thread at top priority and opted out of Efficiency mode: the tray's UI
/// thread can be at idle priority (Efficiency.SetSelf), and a hook there would make the mouse lag. The callback only
/// looks at side-button presses. When Ctrl and Shift are held it swallows the press and its release, so the app
/// underneath doesn't also go back or forward.
/// </summary>
internal sealed class MouseShortcut : IDisposable
{
    private const int WH_MOUSE_LL = 14, WM_QUIT = 0x0012, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;
    private const int ThreadPowerThrottling = 3;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern bool SetThreadInformation(IntPtr thread, int infoClass, ref PowerThrottlingState info, int size);

    private readonly Action _action;
    private readonly HookProc _proc;                // kept alive: Windows calls it for as long as the hook is set
    private readonly ManualResetEventSlim _started = new();
    private uint _threadId;
    private IntPtr _hook;
    private bool _swallowRelease;

    /// <summary>Whether the hook is in place (false if Windows refused it; that's logged).</summary>
    public bool Active => _hook != IntPtr.Zero;

    /// <param name="action">Runs on the hook's thread: post it to the UI thread.</param>
    public MouseShortcut(Action action)
    {
        _action = action;
        _proc = OnMouse;
        new Thread(Run) { IsBackground = true, Name = "Mouse shortcut", Priority = ThreadPriority.Highest }.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        // Full speed even while the process is in Efficiency mode (EcoQoS would otherwise apply to this thread too).
        var speed = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 0 };
        SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, ref speed, Marshal.SizeOf<PowerThrottlingState>());
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Info($"Mouse shortcut unavailable: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        _started.Set();
        if (_hook == IntPtr.Zero) return;
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } // the hook is called from this loop
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = (int)wParam;
            if (message == WM_XBUTTONDOWN && GetAsyncKeyState(VK_CONTROL) < 0 && GetAsyncKeyState(VK_SHIFT) < 0)
            {
                _swallowRelease = true;
                ThreadPool.QueueUserWorkItem(_ => _action());
                return 1;
            }
            if (message == WM_XBUTTONUP && _swallowRelease)
            {
                _swallowRelease = false;
                return 1;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint thread, uint attachTo, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);

    /// <summary>Puts a window in front. A hook's input doesn't count as input to this app, so Windows would only flash
    /// the window's taskbar button. Joining the foreground window's input queue for a moment lets the app take the
    /// front. Call it on the UI thread.</summary>
    public static void BringToFront(Form form)
    {
        var front = GetForegroundWindow();
        if (front == form.Handle) return;
        var frontThread = GetWindowThreadProcessId(front, out _);
        var me = GetCurrentThreadId();
        var attached = frontThread != 0 && frontThread != me && AttachThreadInput(me, frontThread, true);
        SetForegroundWindow(form.Handle);
        BringWindowToTop(form.Handle);
        if (attached) AttachThreadInput(me, frontThread, false);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}

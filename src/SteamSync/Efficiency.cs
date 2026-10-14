using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamSync;

/// <summary>
/// Windows "Efficiency mode" (what Task Manager shows with the leaf icon): idle priority plus EcoQoS, which keeps a
/// process on efficient cores at low clock speeds. Used so the tray app and its library window never compete with a game.
/// </summary>
internal static class Efficiency
{
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;
    private const uint ProcessSetInformation = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static void SetSelf(bool on) => Set(Environment.ProcessId, on);

    /// <summary>Best effort: a process that has already exited, or one we may not touch, is simply skipped.</summary>
    public static void Set(int processId, bool on)
    {
        try
        {
            using (var process = Process.GetProcessById(processId))
                process.PriorityClass = on ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal;

            var handle = OpenProcess(ProcessSetInformation, false, processId);
            if (handle == IntPtr.Zero) return;
            try
            {
                var state = new PowerThrottlingState
                {
                    Version = PowerThrottlingCurrentVersion,
                    ControlMask = PowerThrottlingExecutionSpeed,
                    StateMask = on ? PowerThrottlingExecutionSpeed : 0,
                };
                SetProcessInformation(handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Exited or not ours to change.
        }
    }
}

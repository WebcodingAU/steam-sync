using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RelayHelper;

/// <summary>
/// The CPU's temperature through PawnIO (pawnio.eu), the Microsoft-signed driver LibreHardwareMonitor and Fan Control
/// use in place of WinRing0. PawnIO runs only small modules signed by its author, so this loads one of two from
/// PawnIO.Modules' official release (LGPL-2.1), shipped next to this exe: AMDFamily17.bin or IntelMSR.bin. The readings
/// follow LibreHardwareMonitor's:
/// <list type="bullet">
/// <item>AMD Zen (family 17h, 19h, 1Ah): Tctl from SMN register 0x59800, bits 31:21 in eighths of a degree, less 49 °C
/// when RANGE_SEL (bit 19) or both TJ_SEL bits (17:16) are set. The Zen 1 / Zen+ parts whose Tctl runs hot by a fixed
/// offset (Linux k10temp's table) get it taken off, so this is HWiNFO's "CPU (Tctl/Tdie)" / Tdie.</item>
/// <item>Intel: TjMax (MSR 0x1A2 bits 23:16; 100 when that can't be read) less the package sensor's distance to it (MSR
/// 0x1B1 bits 22:16), which is HWiNFO's "CPU Package". CPUs without a package sensor use their hottest core (MSR
/// 0x19C on each core).</item>
/// </list>
/// PawnIO's device is open to SYSTEM and Administrators only, which is why this runs in the service and the tray app
/// asks for the reading over the pipe ("sensors"). Nothing is loaded until the first request. When PawnIO isn't there
/// it tries again at most once a minute, so installing PawnIO later needs no restart.
/// </summary>
internal sealed class CpuSensor : IDisposable
{
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    private const uint DeviceType = 41394u << 16;
    private const uint IoctlLoadBinary = DeviceType | (0x821 << 2);
    private const uint IoctlExecute = DeviceType | (0x841 << 2);
    private const int FunctionNameLength = 32;
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private SafeFileHandle? _module;
    private bool _amd;
    private double _amdOffset;      // Zen 1 / Zen+ Tctl offset (negative), else 0
    private int _tjMax;
    private bool _intelPackage;
    private DateTime _nextTry = DateTime.MinValue;
    private string _state = "not read yet";

    /// <summary>The CPU's temperature in °C, or null; and how PawnIO is doing ("ok", or why there's no reading).</summary>
    public (double? Celsius, string State) Read()
    {
        lock (_gate)
        {
            if (_module is null)
            {
                if (DateTime.UtcNow < _nextTry) return (null, _state);
                _nextTry = DateTime.UtcNow + RetryEvery;
                Open();
                if (_module is null) return (null, _state);
            }
            try
            {
                var c = _amd ? ReadAmd() : ReadIntel();
                SetState(c is null ? "no valid reading" : "ok");
                return (c, _state);
            }
            catch (Win32Exception ex)
            {
                Close();
                SetState($"reading failed: {ex.Message}");
                return (null, _state);
            }
        }
    }

    private void SetState(string state)
    {
        if (state == _state) return;
        _state = state;
        Log.Write($"CPU temperature (PawnIO): {state}");
    }

    private void Open()
    {
        if (!X86Base.IsSupported) { SetState("not an x86 CPU"); return; }
        var (vendor, family, brand) = Identify();
        string module;
        if (vendor == "AuthenticAMD" && family is 0x17 or 0x19 or 0x1A)
        {
            module = "AMDFamily17.bin";
            _amd = true;
            // linux/drivers/hwmon/k10temp.c tctl_offset_table, as LibreHardwareMonitor applies it.
            _amdOffset = brand.Contains("1600X") || brand.Contains("1700X") || brand.Contains("1800X") ? -20
                : brand.Contains("Threadripper 19") || brand.Contains("Threadripper 29") ? -27
                : brand.Contains("2700X") ? -10
                : 0;
        }
        else if (vendor == "GenuineIntel")
        {
            module = "IntelMSR.bin";
            _amd = false;
            _intelPackage = (X86Base.CpuId(6, 0).Eax & 0x40) != 0; // CPUID.06H:EAX[6]: package thermal management
        }
        else
        {
            SetState($"no PawnIO reading for this CPU ({vendor}, family {family:X}h)");
            return;
        }

        var blobPath = Path.Combine(AppContext.BaseDirectory, "pawnio", module);
        if (!File.Exists(blobPath)) { SetState($"{module} is missing next to the helper"); return; }

        var handle = CreateFileW(DevicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            SetState(error is 2 or 3 ? "PawnIO isn't installed" : $"couldn't open PawnIO ({new Win32Exception(error).Message})");
            return;
        }

        var blob = File.ReadAllBytes(blobPath);
        if (!Ioctl(handle, IoctlLoadBinary, blob, blob.Length, [], 0, out _))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            SetState($"PawnIO wouldn't load {module} ({new Win32Exception(error).Message})");
            return;
        }
        _module = handle;

        if (!_amd)
        {
            var target = Execute("ioctl_read_msr", 0x1A2);
            _tjMax = target is { } t && ((t >> 16) & 0xFF) is var tj and > 0 ? (int)tj : 100;
        }
        Log.Write($"CPU temperature (PawnIO): loaded {module} for {brand.Trim()}");
    }

    private double? ReadAmd()
    {
        var value = Execute("ioctl_read_smn", 0x59800) ?? throw new Win32Exception(Marshal.GetLastPInvokeError());
        var raw = (uint)value;
        var c = (raw >> 21) * 0.125;
        if ((raw & 0x80000) != 0 || (raw & 0x30000) == 0x30000) c -= 49;
        return c + _amdOffset;
    }

    private double? ReadIntel()
    {
        if (_intelPackage)
        {
            var status = Execute("ioctl_read_msr", 0x1B1) ?? throw new Win32Exception(Marshal.GetLastPInvokeError());
            return (status & 0x80000000) != 0 ? _tjMax - (double)((status >> 16) & 0x7F) : null;
        }

        // No package sensor: the hottest core. The MSR is per core, so run on each logical CPU in turn (group 0).
        double? hottest = null;
        var thread = GetCurrentThread();
        var cpus = Math.Min(Environment.ProcessorCount, 64);
        var previous = SetThreadAffinityMask(thread, 1);
        try
        {
            for (var i = 0; i < cpus; i++)
            {
                if (SetThreadAffinityMask(thread, (UIntPtr)(1UL << i)) == UIntPtr.Zero) continue;
                if (Execute("ioctl_read_msr", 0x19C) is { } status && (status & 0x80000000) != 0)
                {
                    var c = _tjMax - (double)((status >> 16) & 0x7F);
                    if (hottest is null || c > hottest) hottest = c;
                }
            }
        }
        finally { if (previous != UIntPtr.Zero) SetThreadAffinityMask(thread, previous); }
        return hottest;
    }

    /// <summary>Runs one of the loaded module's functions with one 64-bit argument and one 64-bit result. Null when
    /// PawnIO refuses (the module denies that register, or the CPU doesn't have it).</summary>
    private ulong? Execute(string function, ulong argument)
    {
        var input = new byte[FunctionNameLength + 8];
        Encoding.ASCII.GetBytes(function, 0, Math.Min(function.Length, FunctionNameLength - 1), input, 0);
        BitConverter.TryWriteBytes(input.AsSpan(FunctionNameLength), argument);
        var output = new byte[8];
        if (!Ioctl(_module!, IoctlExecute, input, input.Length, output, output.Length, out var written) || written < 8) return null;
        return BitConverter.ToUInt64(output);
    }

    private static unsafe bool Ioctl(SafeFileHandle handle, uint code, byte[] input, int inputLength, byte[] output, int outputLength, out uint written)
    {
        fixed (byte* i = input, o = output)
            return DeviceIoControl(handle, code, i, (uint)inputLength, outputLength == 0 ? null : o, (uint)outputLength, out written, IntPtr.Zero);
    }

    /// <summary>CPUID's vendor ("AuthenticAMD", "GenuineIntel"), display family and brand string.</summary>
    private static (string Vendor, int Family, string Brand) Identify()
    {
        var (_, b, c, d) = X86Base.CpuId(0, 0);
        var vendor = Encoding.ASCII.GetString([.. BitConverter.GetBytes(b), .. BitConverter.GetBytes(d), .. BitConverter.GetBytes(c)]);
        var eax = X86Base.CpuId(1, 0).Eax;
        int baseFamily = (eax >> 8) & 0xF, family = baseFamily == 0xF ? baseFamily + ((eax >> 20) & 0xFF) : baseFamily;
        var brand = new StringBuilder();
        if ((uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax >= 0x80000004)
        {
            for (var leaf = 0x80000002u; leaf <= 0x80000004u; leaf++)
            {
                var (r0, r1, r2, r3) = X86Base.CpuId(unchecked((int)leaf), 0);
                foreach (var r in new[] { r0, r1, r2, r3 }) brand.Append(Encoding.ASCII.GetString(BitConverter.GetBytes(r)));
            }
        }
        return (vendor, family, brand.ToString().TrimEnd('\0'));
    }

    private void Close()
    {
        _module?.Dispose();
        _module = null;
    }

    public void Dispose()
    {
        lock (_gate) Close();
    }

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, FileShareDelete = 4, OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle device, uint code, byte* input, uint inputSize, byte* output, uint outputSize, out uint written, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
}

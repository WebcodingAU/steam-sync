using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PingHelper;

/// <summary>
/// GamingEnjoyerPing.exe: the Gaming Enjoyer ping helper.
/// <list type="bullet">
/// <item>(no arguments, started by Windows): the service. Watches network traffic per process (NetworkTrace) and
/// serves it once a second on \\.\pipe\GamingEnjoyer.Ping (PipeServer), for the tray app's live ping of UDP games.</item>
/// <item><c>install</c> (as administrator): copies this exe to Program Files\Gaming Enjoyer and installs or updates
/// the service there, running as NT AUTHORITY\LocalService with only the privileges it needs.</item>
/// <item><c>uninstall</c> (as administrator): stops and removes the service and its folder.</item>
/// <item><c>console</c> (as administrator): runs the same thing in this window until Ctrl+C, for testing.</item>
/// </list>
/// The service's exe must stay in Program Files, which only administrators can change: a service running an exe from
/// a folder the user can write to would let any program the user runs swap it and run as the service.
/// </summary>
internal static class Program
{
    public const string ServiceName = "GamingEnjoyerPing";
    private const string DisplayName = "Gaming Enjoyer ping helper";
    private const string Description = "Lets the Gaming Enjoyer app see which game servers your games talk to, so it can show their ping. "
        + "Reads network traffic counts only, never their contents.";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Gaming Enjoyer");
    public static string InstalledExe => Path.Combine(InstallDir, "GamingEnjoyerPing.exe");

    private static int Main(string[] args)
    {
        switch (args.FirstOrDefault()?.ToLowerInvariant())
        {
            case null:
                if (ServiceHost.Run()) return 0;
                Console.Error.WriteLine("This is the Gaming Enjoyer ping helper service. Commands: install, uninstall, console (as administrator).");
                return 2;
            case "install":
                return RequireAdmin() ?? Install();
            case "uninstall":
                return RequireAdmin() ?? Uninstall();
            case "console":
                return RequireAdmin() ?? RunConsole();
            case "--version":
                Console.WriteLine(Version);
                return 0;
            default:
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                return 2;
        }
    }

    private static int? RequireAdmin()
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return null;
        Console.Error.WriteLine("Run this as administrator.");
        return 5;
    }

    private static int RunConsole()
    {
        using var engine = new Engine();
        engine.Start();
        Console.WriteLine($"Ping helper {Version} running on \\\\.\\pipe\\{PipeServer.PipeName}. Ctrl+C to stop.");
        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        done.Wait();
        return 0;
    }

    private static int Install()
    {
        try
        {
            var exists = Sc("query", ServiceName) == 0;
            if (exists) StopService();

            Directory.CreateDirectory(InstallDir);
            var source = Environment.ProcessPath!;
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                // The old exe can stay locked for a moment after the service stops.
                for (var attempt = 1; ; attempt++)
                {
                    try { File.Copy(source, InstalledExe, overwrite: true); break; }
                    catch (IOException) when (attempt < 10) { Thread.Sleep(500); }
                }
            }

            var binPath = $"\"{InstalledExe}\"";
            var code = exists
                ? Sc("config", ServiceName, "binPath=", binPath, "obj=", @"NT AUTHORITY\LocalService", "password=", "", "start=", "auto", "DisplayName=", DisplayName)
                : Sc("create", ServiceName, "binPath=", binPath, "obj=", @"NT AUTHORITY\LocalService", "start=", "auto", "DisplayName=", DisplayName);
            if (code != 0) throw new InvalidOperationException($"sc {(exists ? "config" : "create")} failed ({code})");

            Sc("description", ServiceName, Description);
            // Only what it needs: no SeImpersonatePrivilege and friends that LocalService would otherwise carry.
            Sc("privs", ServiceName, "SeChangeNotifyPrivilege");
            Sc("sidtype", ServiceName, "unrestricted");
            Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/60000");

            code = Sc("start", ServiceName);
            if (code != 0 && code != 1056) throw new InvalidOperationException($"sc start failed ({code})"); // 1056: already running
            Console.WriteLine($"Installed {DisplayName} {Version}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Install failed: {ex.Message}");
            return 1;
        }
    }

    private static int Uninstall()
    {
        if (Sc("query", ServiceName) == 0)
        {
            StopService();
            var code = Sc("delete", ServiceName);
            if (code != 0) { Console.Error.WriteLine($"sc delete failed ({code})"); return 1; }
        }
        try
        {
            if (File.Exists(InstalledExe)) File.Delete(InstalledExe);
            if (Directory.Exists(InstallDir) && !Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Removed the service, but not its file: {ex.Message}");
        }
        Console.WriteLine($"Uninstalled {DisplayName}.");
        return 0;
    }

    private static void StopService()
    {
        Sc("stop", ServiceName);
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until && !ScOutput("query", ServiceName).Contains("STOPPED")) Thread.Sleep(300);
    }

    private static int Sc(params string[] args) => RunSc(args, out _);

    private static string ScOutput(params string[] args)
    {
        RunSc(args, out var output);
        return output;
    }

    private static int RunSc(string[] args, out string output)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}

/// <summary>The trace plus the pipe, started and stopped together by the service or the console.</summary>
internal sealed class Engine : IDisposable
{
    private readonly FlowTable _flows = new();
    private NetworkTrace? _trace;
    private PipeServer? _pipe;

    public void Start()
    {
        _trace = new NetworkTrace(_flows);
        _trace.Start();
        _pipe = new PipeServer(_flows, Program.Version);
        _pipe.Start();
        Log.Write($"Started {Program.Version}");
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _trace?.Dispose();
        Log.Write("Stopped");
    }
}

/// <summary>Errors go to the Windows Application event log (source: the service name), which LocalService can write.</summary>
internal static class Log
{
    public static unsafe void Write(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
            var source = RegisterEventSourceW(null, Program.ServiceName);
            if (source == IntPtr.Zero) return;
            try
            {
                var text = Marshal.StringToHGlobalUni(message);
                try
                {
                    var strings = stackalloc IntPtr[1];
                    strings[0] = text;
                    ReportEventW(source, 4 /* EVENTLOG_INFORMATION_TYPE */, 0, 0, IntPtr.Zero, 1, 0, strings, IntPtr.Zero);
                }
                finally { Marshal.FreeHGlobal(text); }
            }
            finally { DeregisterEventSource(source); }
        }
        catch { }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterEventSourceW(string? server, string source);

    [DllImport("advapi32.dll")]
    private static extern unsafe bool ReportEventW(IntPtr log, ushort type, ushort category, uint eventId, IntPtr sid,
        ushort numStrings, uint dataSize, IntPtr* strings, IntPtr rawData);

    [DllImport("advapi32.dll")]
    private static extern bool DeregisterEventSource(IntPtr log);
}

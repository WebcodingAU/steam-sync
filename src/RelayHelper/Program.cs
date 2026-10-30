using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RelayHelper;

/// <summary>
/// GamingEnjoyerRelay.exe: the Gaming Enjoyer relay helper.
/// <list type="bullet">
/// <item>(no arguments, started by Windows): the service. Creates the relay's WireGuard adapter (Tunnel) and takes
/// the tray app's commands on \\.\pipe\GamingEnjoyer.Relay (PipeServer): which game ranges go through it, up, down.</item>
/// <item><c>install [client.conf]</c> (as administrator): copies this exe and wireguard.dll to Program Files\Gaming
/// Enjoyer, stores the relay's WireGuard client config (if given; else keeps the one already stored) in
/// %ProgramData%\GamingEnjoyer\relay, readable only by SYSTEM and Administrators since it holds the private key, and
/// installs or updates the service (LocalSystem: creating a network adapter needs it).</item>
/// <item><c>uninstall</c> (as administrator): stops and removes the service, its files and the stored config.</item>
/// <item><c>console</c> (as administrator): runs the same thing in this window until Ctrl+C, for testing.</item>
/// </list>
/// The service's exe and wireguard.dll must stay in Program Files, which only administrators can change: a service
/// running files from a folder the user can write to would let any program the user runs swap them and run as SYSTEM.
/// </summary>
internal static class Program
{
    public const string ServiceName = "GamingEnjoyerRelay";
    private const string DisplayName = "Gaming Enjoyer relay helper";
    private const string Description = "Runs the Gaming Enjoyer game relay: a WireGuard tunnel to your own server that carries only the "
        + "game servers you switch on in the Gaming Enjoyer app.";
    private static readonly string[] Payload = ["GamingEnjoyerRelay.exe", "wireguard.dll", "wireguard-nt-LICENSE.txt"];

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Gaming Enjoyer");
    public static string InstalledExe => Path.Combine(InstallDir, "GamingEnjoyerRelay.exe");
    public static string ConfigDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GamingEnjoyer", "relay");
    public static string ConfigFile => Path.Combine(ConfigDir, "relay.conf");

    private static int Main(string[] args)
    {
        switch (args.FirstOrDefault()?.ToLowerInvariant())
        {
            case null:
                if (ServiceHost.Run()) return 0;
                Console.Error.WriteLine("This is the Gaming Enjoyer relay helper service. Commands: install [client.conf], uninstall, console (as administrator).");
                return 2;
            case "install":
                return RequireAdmin() ?? Install(args.Length > 1 ? args[1] : null);
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
        Console.WriteLine($"Relay helper {Version} running on \\\\.\\pipe\\{PipeServer.PipeName}. Ctrl+C to stop.");
        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        done.Wait();
        return 0;
    }

    private static int Install(string? clientConfig)
    {
        try
        {
            // Check the config before touching anything.
            string? config = null;
            if (clientConfig is not null)
            {
                config = File.ReadAllText(clientConfig);
                RelayConfig.Parse(config); // throws with what's wrong
            }
            else if (!File.Exists(ConfigFile))
            {
                throw new InvalidOperationException("No relay config: run install with the path to your relay's WireGuard client config.");
            }

            var exists = Sc("query", ServiceName) == 0;
            if (exists) StopService();

            Directory.CreateDirectory(InstallDir);
            var sourceDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
            if (!string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(InstallDir), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var file in Payload)
                {
                    // The old files can stay locked for a moment after the service stops.
                    for (var attempt = 1; ; attempt++)
                    {
                        try { File.Copy(Path.Combine(sourceDir, file), Path.Combine(InstallDir, file), overwrite: true); break; }
                        catch (IOException) when (attempt < 10) { Thread.Sleep(500); }
                    }
                }
            }

            if (config is not null)
            {
                var dir = Directory.CreateDirectory(ConfigDir);
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                dir.SetAccessControl(acl);
                File.WriteAllText(ConfigFile, config);
            }

            var binPath = $"\"{InstalledExe}\"";
            var code = exists
                ? Sc("config", ServiceName, "binPath=", binPath, "obj=", "LocalSystem", "start=", "auto", "DisplayName=", DisplayName)
                : Sc("create", ServiceName, "binPath=", binPath, "obj=", "LocalSystem", "start=", "auto", "DisplayName=", DisplayName);
            if (code != 0) throw new InvalidOperationException($"sc {(exists ? "config" : "create")} failed ({code})");

            Sc("description", ServiceName, Description);
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
            foreach (var file in Payload)
            {
                var path = Path.Combine(InstallDir, file);
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(InstallDir) && !Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir);
            if (Directory.Exists(ConfigDir)) Directory.Delete(ConfigDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Removed the service, but not all its files: {ex.Message}");
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

    private static int Sc(params string[] args) => Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), args, out _);

    private static string ScOutput(params string[] args)
    {
        Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), args, out var output);
        return output;
    }

    public static int Run(string exe, IEnumerable<string> args, out string output)
    {
        var psi = new ProcessStartInfo(exe)
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

/// <summary>The tunnel plus the pipe, started and stopped together by the service or the console.</summary>
internal sealed class Engine : IDisposable
{
    private Tunnel? _tunnel;
    private PipeServer? _pipe;

    public void Start()
    {
        _tunnel = new Tunnel(RelayConfig.Parse(File.ReadAllText(Program.ConfigFile)));
        _tunnel.Start();
        _pipe = new PipeServer(_tunnel, Program.Version);
        _pipe.Start();
        Log.Write($"Started {Program.Version}");
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _tunnel?.Dispose();
        Log.Write("Stopped");
    }
}

/// <summary>Messages go to the Windows Application event log (source: the service name).</summary>
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

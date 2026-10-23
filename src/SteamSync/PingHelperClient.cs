using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using Microsoft.Win32;

namespace SteamSync;

/// <summary>One second of one conversation, as the ping helper reports it.</summary>
internal sealed record HelperFlow(int Pid, bool Udp, IPAddress Ip, int RemotePort, int LocalPort, int PacketsOut, int PacketsIn, long BytesOut, long BytesIn)
{
    public int Packets => PacketsOut + PacketsIn;
}

/// <summary>A connection that was redirected (ExitLag's proxy): the program asked for Ip:Port, but its socket on LocalPort
/// talks to 127.0.0.1. From helper 1.1.0.</summary>
internal sealed record HelperRedirect(int Pid, int LocalPort, IPAddress Ip, int Port);

internal enum HelperState { NotBundled, NotInstalled, Outdated, Installed }

/// <summary>
/// The tray app's side of the ping helper (src\PingHelper): installs, updates and removes it (one UAC prompt each),
/// and reads its pipe, \\.\pipe\GamingEnjoyer.Ping, which sends the last second's flows once a second. The helper
/// ships next to SteamSync.exe; installing copies it into Program Files, where the service runs it.
/// </summary>
internal sealed class PingHelperClient : IDisposable
{
    private const string PipeName = "GamingEnjoyer.Ping";
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\GamingEnjoyerPing";
    private static readonly string BundledExe = Path.Combine(AppContext.BaseDirectory, "GamingEnjoyerPing.exe");
    private static readonly string InstalledExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Gaming Enjoyer", "GamingEnjoyerPing.exe");

    private readonly CancellationTokenSource _stop = new();
    private (DateTimeOffset At, IReadOnlyList<HelperFlow> Flows, IReadOnlyList<HelperRedirect> Redirects)? _latest;
    private Task? _reader;

    /// <summary>True while lines are arriving from the helper.</summary>
    public bool Connected => _latest is { } l && DateTimeOffset.UtcNow - l.At < TimeSpan.FromSeconds(3);

    /// <summary>The last second's flows, or null when the helper isn't running (or not installed).</summary>
    public IReadOnlyList<HelperFlow>? Latest => Connected ? _latest!.Value.Flows : null;

    /// <summary>Redirected connections still in use; empty from a helper older than 1.1.0, null when it isn't running.</summary>
    public IReadOnlyList<HelperRedirect>? LatestRedirects => Connected ? _latest!.Value.Redirects : null;

    public void Start() => _reader = Task.Run(() => ReadLoop(_stop.Token));

    public static HelperState State
    {
        get
        {
            if (!File.Exists(BundledExe)) return HelperState.NotBundled;
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
            if (key is null || !File.Exists(InstalledExe)) return HelperState.NotInstalled;
            // Only a newer bundled helper is an update. Offering any different one (as up to 0.4.0-beta.9) let a tray
            // that shipped 1.0.0 "update" an installed 1.1.0 back to 1.0.0, which has no redirects.
            var installed = HelperVersion(InstalledExe);
            var bundled = HelperVersion(BundledExe);
            return installed is not null && bundled is not null && bundled > installed ? HelperState.Outdated : HelperState.Installed;
        }
    }

    /// <summary>The helper's own version (1.1.0 from "1.1.0+commit"), or null when it can't be read.</summary>
    private static Version? HelperVersion(string path)
    {
        try
        {
            var text = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+', '-')[0];
            return Version.TryParse(text, out var v) ? v : null;
        }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>Runs the bundled helper elevated with install or uninstall. Null when it worked, else what went wrong.</summary>
    public static async Task<string?> RunElevatedAsync(bool install)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(BundledExe, install ? "install" : "uninstall")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            await p.WaitForExitAsync();
            Log.Info($"Ping helper {(install ? "install" : "uninstall")} exited with {p.ExitCode}");
            return p.ExitCode == 0 ? null : $"The ping helper {(install ? "install" : "removal")} failed (code {p.ExitCode}). See the Application event log.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // the UAC prompt was declined
        {
            return "";
        }
        catch (Exception ex)
        {
            Log.Error("Could not run the ping helper", ex);
            return ex.Message;
        }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.In, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, ct);
                Log.Info("Connected to the ping helper");
                using var reader = new StreamReader(pipe);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    var (flows, redirects) = Parse(line);
                    _latest = (DateTimeOffset.UtcNow, flows, redirects);
                }
                Log.Info("The ping helper closed its pipe");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is TimeoutException or IOException or JsonException or UnauthorizedAccessException) { }
            catch (Exception ex) { Log.Error("Reading the ping helper failed", ex); }

            // Not installed, stopped, or restarting: look again in a while.
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static (List<HelperFlow> Flows, List<HelperRedirect> Redirects) Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var flows = new List<HelperFlow>();
        var redirects = new List<HelperRedirect>();
        if (doc.RootElement.TryGetProperty("flows", out var array))
        {
            foreach (var f in array.EnumerateArray())
            {
                if (!IPAddress.TryParse(f.GetProperty("ip").GetString(), out var ip)) continue;
                flows.Add(new HelperFlow(
                    f.GetProperty("pid").GetInt32(),
                    f.GetProperty("p").GetString() == "udp",
                    Unmap(ip),
                    f.GetProperty("rp").GetInt32(),
                    f.GetProperty("lp").GetInt32(),
                    f.GetProperty("po").GetInt32(),
                    f.GetProperty("pi").GetInt32(),
                    f.GetProperty("bo").GetInt64(),
                    f.GetProperty("bi").GetInt64()));
            }
        }
        // Helper 1.1.0+.
        if (doc.RootElement.TryGetProperty("redirects", out array))
        {
            foreach (var r in array.EnumerateArray())
            {
                if (!IPAddress.TryParse(r.GetProperty("ip").GetString(), out var ip)) continue;
                redirects.Add(new HelperRedirect(r.GetProperty("pid").GetInt32(), r.GetProperty("lp").GetInt32(), Unmap(ip), r.GetProperty("rp").GetInt32()));
            }
        }
        return (flows, redirects);
    }

    private static IPAddress Unmap(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    public void Dispose()
    {
        _stop.Cancel();
        try { _reader?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}

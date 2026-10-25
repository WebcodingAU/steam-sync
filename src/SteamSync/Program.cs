using System.Text.Json;
using Velopack;

namespace SteamSync;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Installer hooks first: Velopack runs the exe with its own arguments during install, update and uninstall,
        // handles them here and exits. Uninstalling also removes the start-with-Windows entry it would leave behind.
        VelopackApp.Build()
            .OnFirstRun(_ => StartWithWindows.Set(true)) // a sync app should be running; the tray menu can turn it off
            .OnBeforeUninstallFastCallback(_ =>
            {
                StartWithWindows.Set(false);
                GameLauncher.Unregister();
            })
            .Run();

        // Diagnostics: SteamSync.exe --dump out.json writes the local scan and exits (no network).
        if (args is ["--dump", var outPath])
        {
            var snapshot = SteamLibrary.Scan();
            object nonSteam;
            try { nonSteam = NonSteamLibrary.Scan(snapshot); }
            catch (Exception ex) { nonSteam = new { Error = ex.ToString() }; } // what the tray app would send as p_nonsteam = null
            var dump = new { snapshot.SteamPath, snapshot.SteamId, snapshot.Libraries, snapshot.Games, NonSteam = nonSteam };
            File.WriteAllText(outPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        // Diagnostics: SteamSync.exe --cloud-region <ip> out.txt writes which cloud region the address is in and five
        // timings of that region's endpoint (live ping's estimate for servers that drop ping), and exits.
        if (args is ["--cloud-region", var regionIp, var regionOut] && System.Net.IPAddress.TryParse(regionIp, out var address))
        {
            using var cloud = new CloudRegions();
            using var output = new StreamWriter(regionOut);
            CloudRegion? region = null;
            for (var i = 0; i < 60 && (region = cloud.Find(address)) is null; i++) Thread.Sleep(500); // the ranges load on first use
            output.WriteLine(region is null ? "not in a known cloud" : $"{region.Provider} {region.Code} {region.Name} {region.Endpoint}");
            if (region is { IsGlobal: false })
                for (var i = 0; i < 5; i++, Thread.Sleep(2100))
                    output.WriteLine($"{cloud.ProbeAsync(region).GetAwaiter().GetResult():0.0} ms");
            return 0;
        }

        // Diagnostics: SteamSync.exe --probe <ip> <port> <steam|other>[-udp][-noping] out.txt [seconds] runs live ping's measuring
        // of one server (ping, then the fallbacks for servers that ignore it) once a second (default 20 s), and exits.
        // -noping treats the server as ignoring ping, to try the fallbacks on one that answers it.
        if (args.Length is 5 or 6 && args[0] == "--probe" && System.Net.IPAddress.TryParse(args[1], out var probeIp) && int.TryParse(args[2], out var probePort))
        {
            var seconds = args.Length == 6 && int.TryParse(args[5], out var s) ? s : 20;
            using var helper = new PingHelperClient();
            using var live = new LivePing(AppConfig.Load(), new PlayTracker(AppConfig.Load()), helper);
            using var output = new StreamWriter(args[4]);
            for (var i = 0; i < seconds; i++, Thread.Sleep(1000))
            {
                output.WriteLine(live.ProbeNowAsync(probeIp, probePort, args[3].StartsWith("steam"), udp: args[3].Contains("-udp"), skipPing: args[3].EndsWith("-noping")).GetAwaiter().GetResult());
                output.Flush();
            }
            return 0;
        }

        // Diagnostics: SteamSync.exe --live-ping out.jsonl [seconds] writes what live ping would send, once a second while
        // a game runs (default 10 s), and exits. Nothing is sent.
        if (args.Length is 2 or 3 && args[0] == "--live-ping")
        {
            var seconds = args.Length == 3 && int.TryParse(args[2], out var s) ? s : 10;
            var steam = SteamLibrary.Scan();
            NonSteamSnapshot? others = null;
            try { others = NonSteamLibrary.Scan(steam); } catch (Exception ex) { Log.Error("Non-Steam scan failed", ex); }
            var play = new PlayTracker(AppConfig.Load()); // never disposed: that would save its state file over the real app's
            play.UpdateGames(steam, others);
            using var helper = new PingHelperClient();
            helper.Start();
            using var live = new LivePing(AppConfig.Load(), play, helper);
            Thread.Sleep(2500); // let the helper's first line arrive
            using var output = new StreamWriter(args[1]);
            for (var i = 0; i < seconds; i++)
            {
                var games = play.DetectNow();
                output.WriteLine(games.Count == 0 ? "null" : live.MeasureNowAsync(games).GetAwaiter().GetResult().ToJsonString());
                output.Flush();
                Thread.Sleep(1000);
            }
            return 0;
        }

        // Diagnostics: SteamSync.exe --system-stats out.jsonl [seconds] [pid...] writes the PC's load as live ping would
        // send it, once a second (default 10 s), and exits. The pids stand in for a game's processes.
        if (args.Length >= 2 && args[0] == "--system-stats")
        {
            var seconds = args.Length >= 3 && int.TryParse(args[2], out var s) ? s : 10;
            var pids = args.Skip(3).Select(a => int.TryParse(a, out var p) ? p : 0).Where(p => p > 0).ToList();
            using var stats = new SystemStats();
            using var output = new StreamWriter(args[1]);
            for (var i = 0; i <= seconds; i++, Thread.Sleep(1000))
            {
                var line = stats.Sample(pids).ToJsonString();
                if (i == 0) continue; // the first sample only starts the counters
                output.WriteLine(line);
                output.Flush();
            }
            return 0;
        }

        // The site's Play button: Windows runs SteamSync.exe --play gamingenjoyer://play/<key>. Handled here, before the
        // one-copy check, so it works whether or not the tray app is running; this process starts the game and exits.
        if (args is ["--play", var link])
        {
            if (GameLauncher.Play(link) is { } problem)
                MessageBox.Show(problem, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        // Any launch by a person (Start menu, the exe, first run after install) opens the Library; only start-with-
        // Windows and post-update restarts pass --background. A second launch asks the running app to show its
        // window, so there is only ever one copy of the app and one browser engine.
        var openLibrary = args.Contains("--open-library") || !args.Contains("--background");
        using var openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SteamSync.OpenLibrary");
        using var mutex = new Mutex(true, @"Local\SteamSync.TrayApp", out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (openLibrary) openSignal.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        Log.Info($"{AppInfo.Name} {AppInfo.Version} starting");
        Efficiency.SetSelf(true); // a tray app: stay out of the way until the library window is in use
        Application.Run(new TrayApp(AppConfig.Load(), background: args.Contains("--background"), openSignal, openLibrary));
        return 0;
    }
}

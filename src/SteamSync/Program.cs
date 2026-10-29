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

        // Diagnostics: SteamSync.exe --system-stats out.jsonl [seconds] [pid...] writes the PC's load as a beta build
        // sends it, once a second (default 10 s), and exits. The pids stand in for a game's processes.
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

        // The game relay's one-off setup (GameRelay.Setup). The tray starts it elevated as --relay-setup <client.conf>
        // <user sid> <result file>; run by hand with just the config, it asks for elevation itself.
        if (args is ["--relay-setup", var relayConfig, var userSid, var resultFile])
        {
            string? problem;
            try { problem = GameRelay.Setup(Path.GetFullPath(relayConfig), userSid); }
            catch (Exception ex) { problem = ex.Message; }
            File.WriteAllText(resultFile, problem ?? "");
            return problem is null ? 0 : 1;
        }
        if (args is ["--relay-setup", var config])
        {
            var problem = GameRelay.RunSetupElevatedAsync(Path.GetFullPath(config)).GetAwaiter().GetResult();
            if (problem != "")
                MessageBox.Show(problem ?? "Game relay set up. Switch games on in the tray's Game relay menu.", AppInfo.Name,
                    MessageBoxButtons.OK, problem is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return problem is null ? 0 : 1;
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

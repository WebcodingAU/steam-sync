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
            .OnBeforeUninstallFastCallback(_ => StartWithWindows.Set(false))
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

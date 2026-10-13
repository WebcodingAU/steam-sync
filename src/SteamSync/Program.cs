using System.Text.Json;

namespace SteamSync;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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

        using var mutex = new Mutex(true, @"Local\SteamSync.TrayApp", out var isFirstInstance);
        if (!isFirstInstance) return 0;

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        Log.Info($"Steam Sync {AppInfo.Version} starting");
        Application.Run(new TrayApp(AppConfig.Load(), background: args.Contains("--background")));
        return 0;
    }
}

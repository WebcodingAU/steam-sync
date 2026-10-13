using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SteamSync;

internal sealed record InstalledGame(
    [property: JsonPropertyName("appid")] int AppId,
    string Name,
    string? InstallDir,
    string LibraryPath,
    long? SizeOnDisk,
    int StateFlags,
    DateTimeOffset? LastPlayed,
    DateTimeOffset? LastUpdated,
    long? BuildId);

internal sealed record SteamSnapshot(
    string SteamPath,
    string? SteamId,
    IReadOnlyList<string> Libraries,
    IReadOnlyList<InstalledGame> Games);

/// <summary>Reads the installed game list straight from Steam's own files; no drive crawling.</summary>
internal static class SteamLibrary
{
    // Not games: Steamworks Common Redistributables.
    private static readonly HashSet<int> ExcludedAppIds = [228980];

    public static string? FindSteamPath()
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                   ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Path.GetFullPath(path.Replace('/', '\\'));
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>All library roots, from steamapps\libraryfolders.vdf (always includes the Steam folder itself).</summary>
    public static IReadOnlyList<string> GetLibraryPaths(string steamPath)
    {
        var paths = new List<string> { steamPath };
        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            var root = Vdf.ParseFile(vdfPath);
            var folders = root.GetObject("libraryfolders") ?? root;
            foreach (var entry in folders.Values.OfType<VdfObject>())
            {
                if (entry.GetString("path") is { Length: > 0 } p)
                    paths.Add(Path.GetFullPath(p));
            }
        }
        return paths
            .Select(p => p.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>SteamID64 of the most recently signed-in account, from config\loginusers.vdf.</summary>
    public static string? FindSteamId(string steamPath)
    {
        var file = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(file)) return null;
        var users = Vdf.ParseFile(file).GetObject("users");
        if (users is null) return null;

        return users
            .Where(kv => kv.Value is VdfObject)
            .Select(kv => (Id: kv.Key, User: (VdfObject)kv.Value))
            .OrderByDescending(u => u.User.GetString("MostRecent") == "1")
            .ThenByDescending(u => u.User.GetLong("Timestamp") ?? 0)
            .Select(u => u.Id)
            .FirstOrDefault();
    }

    public static SteamSnapshot Scan()
    {
        var steamPath = FindSteamPath() ?? throw new InvalidOperationException("Steam is not installed (no SteamPath in registry).");
        var libraries = GetLibraryPaths(steamPath);
        var games = new Dictionary<int, InstalledGame>();

        foreach (var library in libraries)
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps))
            {
                Log.Info($"Library not reachable, skipping: {library}");
                continue;
            }

            foreach (var manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    if (ReadManifest(manifest, library) is { } game && !ExcludedAppIds.Contains(game.AppId))
                        games.TryAdd(game.AppId, game);
                }
                catch (Exception ex)
                {
                    Log.Error($"Could not read {manifest}", ex);
                }
            }
        }

        return new SteamSnapshot(
            steamPath,
            SafeFindSteamId(steamPath),
            libraries,
            games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string? SafeFindSteamId(string steamPath)
    {
        try { return FindSteamId(steamPath); }
        catch (Exception ex) { Log.Error("Could not read loginusers.vdf", ex); return null; }
    }

    private static InstalledGame? ReadManifest(string file, string library)
    {
        var state = Vdf.ParseFile(file).GetObject("AppState");
        if (state is null || !int.TryParse(state.GetString("appid"), out var appId)) return null;

        return new InstalledGame(
            appId,
            state.GetString("name") is { Length: > 0 } name ? name : $"App {appId}",
            state.GetString("installdir"),
            library,
            state.GetLong("SizeOnDisk"),
            (int)(state.GetLong("StateFlags") ?? 0),
            FromUnix(state.GetLong("LastPlayed")),
            FromUnix(state.GetLong("LastUpdated")),
            state.GetLong("buildid"));
    }

    private static DateTimeOffset? FromUnix(long? seconds) =>
        seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
}

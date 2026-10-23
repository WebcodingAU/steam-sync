using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteamSync;

/// <summary>
/// Starts non-Steam games from the website's Play button without adding them to Steam. The site links to
/// gamingenjoyer://play/&lt;entity key&gt; (the key sync_device stores, e.g. "xbox:&lt;package family&gt;" or "bsg:&lt;id&gt;");
/// Windows hands that link to this exe as <c>--play &lt;link&gt;</c>.
/// <para>
/// A link is only a choice between games this PC has: the PC is scanned again, the key must belong to an installed
/// game found by that scan, and the game is started the way its own launcher starts it. Nothing from the link is ever
/// run, opened or passed on, so a link on any other website can at most start one of your installed games.
/// </para>
/// </summary>
internal static class GameLauncher
{
    public const string Scheme = "gamingenjoyer";
    private const string ClassesKey = @"Software\Classes\" + Scheme;
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // Entity keys as NonSteamLibrary builds them: a lowercase source, a colon, then no spaces, quotes or slashes.
    private static readonly Regex KeyPattern = new(@"^[a-z0-9_]{1,20}:[^\s""'<>/\\]{1,300}$", RegexOptions.CultureInvariant);

    /// <summary>Registers gamingenjoyer:// for this user (no admin), pointing at the running exe. Run at every start,
    /// so an update that moves the exe keeps the links working.</summary>
    public static void Register()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ClassesKey);
            key.SetValue("", $"URL:{AppInfo.Name}");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{Environment.ProcessPath}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{Environment.ProcessPath}\" --play \"%1\"");
        }
        catch (Exception ex) { Log.Error("Could not register the gamingenjoyer:// link", ex); }
    }

    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(ClassesKey, throwOnMissingSubKey: false); }
        catch (Exception ex) { Log.Error("Could not remove the gamingenjoyer:// link", ex); }
    }

    /// <summary>Handles one gamingenjoyer:// link. Returns null when the game was started, else what to tell the person.</summary>
    public static string? Play(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("play", StringComparison.OrdinalIgnoreCase))
            return "That link isn't one Gaming Enjoyer can open.";

        var key = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')).Trim().ToLowerInvariant();
        if (!KeyPattern.IsMatch(key)) return "That link isn't one Gaming Enjoyer can open.";

        NonSteamGame? game;
        try
        {
            var steam = SteamLibrary.Scan();
            game = NonSteamLibrary.Scan(steam).Games.FirstOrDefault(g => g.Installed && g.Keys.Contains(key));
        }
        catch (Exception ex)
        {
            Log.Error("Could not scan this PC to start a game", ex);
            return "Gaming Enjoyer couldn't check which games are on this PC just now. Please try again.";
        }
        if (game is null)
            return "That game isn't installed on this PC. Play starts games on the PC you're browsing from.";

        var target = LaunchTarget(game);
        if (target is null)
            return $"Gaming Enjoyer doesn't know how to start {game.Name} yet. Start it from its own launcher, or add it to Steam as a non-Steam game.";

        try
        {
            Log.Info($"Starting {game.Name} ({key}) via {target.Value.Describe}");
            Process.Start(target.Value.Start);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start {game.Name}", ex);
            return $"{game.Name} couldn't be started. Try starting it from its own launcher.";
        }
    }

    /// <summary>How to start a game: its launcher's own link, the Xbox app's entry, the Battlestate launcher, or its
    /// Steam shortcut, in that order. Null when there's no safe way.</summary>
    private static (ProcessStartInfo Start, string Describe)? LaunchTarget(NonSteamGame game)
    {
        if (game.LaunchUri is { } launchUri)
            return (new ProcessStartInfo(launchUri) { UseShellExecute = true }, launchUri.Split(':')[0]);

        if (game.Keys.Any(k => k.StartsWith("xbox:", StringComparison.Ordinal))
            && game.InstallPath is { } xboxFolder && NonSteamLibrary.XboxAppUserModelId(xboxFolder) is { } aumid)
            return (new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}") { UseShellExecute = false }, "the Xbox app");

        // Battlestate games refuse to start without their launcher signing in first, so Play opens the launcher.
        if (game.Keys.Any(k => k.StartsWith("bsg:", StringComparison.Ordinal)) && BattlestateLauncher() is { } bsg)
            return (new ProcessStartInfo(bsg) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(bsg) }, "the Battlestate launcher");

        if (game.LaunchId is { } shortcutId)
            return (new ProcessStartInfo($"steam://rungameid/{shortcutId}") { UseShellExecute = true }, "its Steam shortcut");

        return null;
    }

    /// <summary>BsgLauncher.exe from the Battlestate Games Launcher's Programs-list entry, or null.</summary>
    private static string? BattlestateLauncher()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry is null) continue;
                var publisher = (entry.GetValue("Publisher") as string) ?? "";
                var display = (entry.GetValue("DisplayName") as string) ?? "";
                if (!publisher.StartsWith("Battlestate Games", StringComparison.OrdinalIgnoreCase)
                    || !display.Contains("Launcher", StringComparison.OrdinalIgnoreCase)) continue;
                var location = (entry.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                if (string.IsNullOrEmpty(location)) continue;
                var exe = Path.Combine(location, "BsgLauncher.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SteamSync;

/// <summary>One non-Steam game on this PC, sent to sync_device as an element of p_nonsteam (snake_case on the wire).</summary>
internal sealed record NonSteamGame(
    string EntityKey,
    IReadOnlyList<string> Keys,
    string Name,
    string Source,
    string? InstallPath,
    string? LaunchId,
    string? LaunchUri,
    bool Installed);

/// <summary>A "non-Steam game" the user added to Steam (userdata\&lt;id&gt;\config\shortcuts.vdf).</summary>
internal sealed record SteamShortcut(string GameId, string AppName, string Exe, string? ExePath, bool ExeExists)
{
    public string Key => $"shortcut:{GameId}";

    /// <summary>The exe exists, or isn't a checkable path at all (explorer.exe + LaunchOptions, a URL…) and is trusted.</summary>
    public bool Installed => ExeExists || ExePath is null;
}

/// <summary>A game found through a launcher: an Xbox package or an allowlisted Programs-list entry.</summary>
internal sealed record LauncherGame(string Key, string Source, string Name, string? InstallPath, string? LaunchUri, bool Installed);

internal sealed record NonSteamSnapshot(
    IReadOnlyList<NonSteamGame> Games,
    IReadOnlyList<SteamShortcut> Shortcuts,
    IReadOnlyList<LauncherGame> LauncherGames,
    IReadOnlyList<string> ShortcutConfigDirs,
    IReadOnlyList<string> XboxRoots);

/// <summary>
/// Finds non-Steam games: Steam shortcuts, Xbox/Game Pass packages and an allowlist of launchers in the Programs list
/// (Battle.net, Ubisoft, EA, Battlestate), merged into one entity per game. Any failure throws, so the caller never
/// sends a partial list (the site would untick the missing games).
/// </summary>
internal static class NonSteamLibrary
{
    private const ulong SteamId64Base = 76561197960265728;
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    // Which key becomes entity_key when detections merge; earlier wins.
    private static readonly string[] SourcePreference = ["xbox", "battlenet", "ubisoft", "ea", "bsg", "shortcut"];

    // Battle.net uids that are the launcher itself, not games.
    private static readonly HashSet<string> BattleNetNonGames = new(StringComparer.OrdinalIgnoreCase) { "battle.net", "bna", "agent" };

    // WoW flavours share one install folder (and its Uninstall InstallLocation); each lives in its own subfolder.
    private static readonly Dictionary<string, string> WowFlavourFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wow"] = "_retail_",
        ["wow_classic"] = "_classic_",
        ["wow_classic_era"] = "_classic_era_",
        ["wow_anniversary"] = "_anniversary_",
        ["wow_classic_anniversary"] = "_anniversary_",
        ["wow_classic_beta"] = "_classic_beta_",
        ["wow_beta"] = "_beta_",
        ["wow_ptr"] = "_ptr_",
        ["wow_classic_ptr"] = "_classic_ptr_",
    };

    private static readonly Regex UbisoftSubkey = new(@"^Uplay Install ([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BattleNetUid = new(@"--uid=(""[^""]*""|[^\s""]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static NonSteamSnapshot Scan(SteamSnapshot steam)
    {
        var shortcutDirs = FindShortcutConfigDirs(steam.SteamPath, steam.SteamId);
        var shortcuts = ReadShortcuts(shortcutDirs);
        var xboxRoots = FindXboxRoots();

        var launcherGames = ScanXbox(xboxRoots)
            .Concat(ScanProgramsList(steam.Libraries))
            .GroupBy(g => g.Key)
            .Select(g => g.OrderByDescending(x => x.Installed).First())
            .ToList();

        return new NonSteamSnapshot(Merge(launcherGames, shortcuts), shortcuts, launcherGames, shortcutDirs, xboxRoots);
    }

    /// <summary>userdata\&lt;accountId&gt;\config of the signed-in account, or every account's when that's unknown.</summary>
    public static IReadOnlyList<string> FindShortcutConfigDirs(string steamPath, string? steamId)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return [];

        if (ulong.TryParse(steamId, NumberStyles.None, CultureInfo.InvariantCulture, out var id64) && id64 > SteamId64Base)
        {
            var dir = Path.Combine(userdata, (id64 - SteamId64Base).ToString(CultureInfo.InvariantCulture), "config");
            return Directory.Exists(dir) ? [dir] : [];
        }

        return Directory.EnumerateDirectories(userdata)
            .Where(d => Path.GetFileName(d).All(char.IsAsciiDigit))
            .Select(d => Path.Combine(d, "config"))
            .Where(Directory.Exists)
            .ToList();
    }

    private static List<SteamShortcut> ReadShortcuts(IReadOnlyList<string> configDirs)
    {
        var shortcuts = new List<SteamShortcut>();
        var seen = new HashSet<string>();
        foreach (var dir in configDirs)
        {
            var file = Path.Combine(dir, "shortcuts.vdf");
            if (!File.Exists(file)) continue;

            var list = BinaryVdf.ParseFile(file).GetObject("shortcuts")
                       ?? throw new FormatException($"{file} has no 'shortcuts' map.");
            foreach (var entry in list.Values.OfType<VdfObject>())
            {
                if (ReadShortcut(entry) is { } shortcut && seen.Add(shortcut.GameId))
                    shortcuts.Add(shortcut);
            }
        }
        return shortcuts;
    }

    private static SteamShortcut? ReadShortcut(VdfObject entry)
    {
        // Always use the stored appid: on real installs it often doesn't match the documented crc32(exe + name) rule.
        uint? appId = entry.TryGetValue("appid", out var raw)
            ? raw switch { int i => (uint)i, long l => (uint)l, ulong u => (uint)u, _ => null }
            : null;
        if (appId is null)
        {
            Log.Info($"Skipping Steam shortcut without an appid: {entry.GetString("AppName")}");
            return null;
        }

        var gameId = (((ulong)appId.Value << 32) | 0x02000000).ToString(CultureInfo.InvariantCulture);
        var exe = (entry.GetString("Exe") ?? "").Trim().Trim('"').Trim();
        var exePath = CheckableExePath(exe);
        var name = entry.GetString("AppName") is { Length: > 0 } n ? n
            : exe.Length > 0 ? Path.GetFileNameWithoutExtension(exe)
            : $"Shortcut {gameId}";

        return new SteamShortcut(gameId, name, exe, exePath, exePath is not null && File.Exists(exePath));
    }

    /// <summary>The exe as a full path, or null when it can't be checked on disk (a URL, or a Windows stub like explorer.exe).</summary>
    private static string? CheckableExePath(string exe)
    {
        if (exe.Contains("://")) return null;
        var path = NormalisePath(exe);
        if (path is null) return null;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 && IsUnder(path, windows) ? null : path;
    }

    /// <summary>Xbox app game folders (e.g. C:\XboxGames, G:\Xbox), from each fixed drive's .GamingRoot.</summary>
    public static IReadOnlyList<string> FindXboxRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var file = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
            if (!File.Exists(file)) continue;

            foreach (var folder in ReadGamingRoot(File.ReadAllBytes(file), file))
            {
                if (NormalisePath(Path.Combine(drive.RootDirectory.FullName, folder)) is { } root && Directory.Exists(root))
                    roots.Add(root);
            }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>.GamingRoot: "RGBX", a uint32 count, then that many NUL-terminated UTF-16LE folder names relative to the drive.</summary>
    private static List<string> ReadGamingRoot(byte[] data, string file)
    {
        if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual("RGBX"u8))
            throw new FormatException($"{file} is not in the expected format.");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var folders = new List<string>();
        var pos = 8;
        for (var i = 0; i < count && pos < data.Length; i++)
        {
            var end = pos;
            while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0)) end += 2;
            if (end > pos) folders.Add(Encoding.Unicode.GetString(data, pos, end - pos));
            pos = end + 2;
        }
        return folders;
    }

    private static List<LauncherGame> ScanXbox(IReadOnlyList<string> roots)
    {
        var games = new List<LauncherGame>();
        string[]? registered = null;

        foreach (var root in roots)
        {
            foreach (var folder in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (folder.Name.Equals("GameSave", StringComparison.OrdinalIgnoreCase) || folder.Attributes.HasFlag(FileAttributes.Hidden))
                    continue;
                var content = Path.Combine(folder.FullName, "Content");
                if (!Directory.Exists(content)) continue;
                // The casing varies per game (MicrosoftGame.Config is common).
                var config = Directory.EnumerateFiles(content, "*.config")
                    .FirstOrDefault(f => Path.GetFileName(f).Equals("MicrosoftGame.config", StringComparison.OrdinalIgnoreCase));
                if (config is null) continue;

                var game = LoadXml(config).Root;
                var identity = Child(game, "Identity");
                var identityName = identity?.Attribute("Name")?.Value.Trim();
                if (string.IsNullOrEmpty(identityName)) throw new FormatException($"{config} has no Identity Name.");
                var displayName = Child(game, "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value.Trim();
                if (string.IsNullOrEmpty(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    displayName = folder.Name;

                // Installed only once the package is registered for this user. Downloads preallocate full-size files,
                // so file presence or size says nothing, and the GamingServices registry keeps stale entries.
                registered ??= RegisteredPackages();
                var fullName = registered.FirstOrDefault(p => p.StartsWith(identityName + "_", StringComparison.OrdinalIgnoreCase));
                var publisherId = fullName is not null
                    ? fullName[(fullName.LastIndexOf('_') + 1)..]
                    : PublisherId(identity!.Attribute("Publisher")?.Value ?? throw new FormatException($"{config} has no Identity Publisher."));

                games.Add(new LauncherGame(
                    $"xbox:{identityName}_{publisherId}".ToLowerInvariant(),
                    "xbox",
                    displayName,
                    NormalisePath(folder.FullName),
                    LaunchUri: null,
                    Installed: fullName is not null));
            }
        }
        return games;
    }

    /// <summary>PackageFullNames (Name_Version_Arch_ResourceId_PublisherId) registered for the current user.</summary>
    private static string[] RegisteredPackages()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PackagesKey)
                        ?? throw new InvalidOperationException("The AppModel package repository is missing from the registry.");
        return key.GetSubKeyNames();
    }

    /// <summary>
    /// The PublisherId part of a PackageFamilyName, for packages that aren't registered (yet): the first 8 bytes of
    /// SHA-256(UTF-16LE publisher), as 13 base32 characters (65 bits, zero-padded).
    /// </summary>
    private static string PublisherId(string publisher)
    {
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var bits = BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.Unicode.GetBytes(publisher)));
        var id = new StringBuilder(13);
        for (var i = 0; i < 13; i++)
        {
            var shift = 59 - 5 * i;
            id.Append(alphabet[(int)((shift >= 0 ? bits >> shift : bits << -shift) & 0x1F)]);
        }
        return id.ToString();
    }

    private static List<LauncherGame> ScanProgramsList(IReadOnlyList<string> steamLibraries)
    {
        var games = new List<LauncherGame>();
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall is null) continue;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                if (name.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)) continue;
                using var entry = uninstall.OpenSubKey(name);
                if (entry is not null && ReadProgramsEntry(name, entry, steamLibraries) is { } game)
                    games.Add(game);
            }
        }
        return games;
    }

    /// <summary>Allowlist only: Battle.net, Ubisoft, EA and Battlestate entries. Everything else returns null.</summary>
    private static LauncherGame? ReadProgramsEntry(string subkey, RegistryKey entry, IReadOnlyList<string> steamLibraries)
    {
        // Value names are case-insensitive, so this also finds EA's 'installlocation'.
        var location = NormalisePath(entry.GetValue("InstallLocation") as string);
        if (location is not null && steamLibraries.Any(l => IsUnder(location, l) || location.Equals(l, StringComparison.OrdinalIgnoreCase)))
            return null; // a Steam copy; SteamLibrary already reports it

        var displayName = (entry.GetValue("DisplayName") as string)?.Trim() is { Length: > 0 } dn ? dn : null;
        var publisher = (entry.GetValue("Publisher") as string)?.Trim() ?? "";

        if (publisher.Equals("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase))
        {
            var match = BattleNetUid.Match(entry.GetValue("UninstallString") as string ?? "");
            if (!match.Success) return null;
            var uid = match.Groups[1].Value.Trim('"').Trim().ToLowerInvariant();
            if (uid.Length == 0 || BattleNetNonGames.Contains(uid)) return null;

            var installed = uid.StartsWith("wow", StringComparison.Ordinal)
                // An unknown WoW flavour counts as not installed rather than guessing its folder.
                ? location is not null && WowFlavourFolders.TryGetValue(uid, out var flavour) && HasExe(Path.Combine(location, flavour))
                : HasExe(location);
            return new LauncherGame($"battlenet:{uid}", "battlenet", displayName ?? uid, location, $"battlenet://game/{uid}", installed);
        }

        if (UbisoftSubkey.Match(subkey) is { Success: true } ubisoft)
        {
            var id = ubisoft.Groups[1].Value;
            var installed = location is not null && Directory.Exists(location)
                && (File.Exists(Path.Combine(location, "uplay_install.state")) || File.Exists(Path.Combine(location, "uplay_install.manifest")));
            return new LauncherGame($"ubisoft:{id}", "ubisoft", displayName ?? subkey, location, $"uplay://launch/{id}", installed);
        }

        if (publisher.StartsWith("Battlestate Games", StringComparison.OrdinalIgnoreCase))
        {
            if (displayName?.Contains("Launcher", StringComparison.OrdinalIgnoreCase) == true) return null;
            return new LauncherGame($"bsg:{subkey.ToLowerInvariant()}", "bsg", displayName ?? subkey, location, LaunchUri: null, HasExe(location));
        }

        var installerData = location is null ? null : Path.Combine(location, "__Installer", "installerdata.xml");
        if (installerData is not null && File.Exists(installerData))
        {
            var contentId = (ReadEaContentId(installerData) ?? subkey).ToLowerInvariant();
            var launchUri = contentId.All(char.IsAsciiDigit) ? $"origin2://game/launch/?offerIds={contentId}" : null;
            return new LauncherGame($"ea:{contentId}", "ea", displayName ?? contentId, location, launchUri, Directory.Exists(location));
        }

        return null;
    }

    /// <summary>The first &lt;contentID&gt; in EA's installerdata.xml, or null when the file can't be parsed.</summary>
    private static string? ReadEaContentId(string file)
    {
        try
        {
            return LoadXml(file).Descendants()
                .Where(e => e.Name.LocalName.Equals("contentID", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value.Trim())
                .FirstOrDefault(v => v.Length > 0);
        }
        catch (XmlException ex)
        {
            Log.Error($"Could not parse {file}; using the Uninstall key name instead", ex);
            return null;
        }
    }

    /// <summary>Folds launcher detections and Steam shortcuts into one entity per game.</summary>
    private static List<NonSteamGame> Merge(IReadOnlyList<LauncherGame> launcherGames, IReadOnlyList<SteamShortcut> shortcuts)
    {
        // 1. Launcher detections in the same folder are one game (e.g. two WoW Classic flavours). Installed and
        //    not-installed ones stay apart, so a removed flavour's key never rides along on an installed entity.
        var entities = new List<Entity>();
        foreach (var game in launcherGames)
        {
            var entity = game.InstallPath is null ? null : entities.Find(e =>
                e.Installed == game.Installed && string.Equals(e.InstallPath, game.InstallPath, StringComparison.OrdinalIgnoreCase));
            if (entity is null) entities.Add(entity = new Entity(game.InstallPath, game.Installed));
            entity.Launchers.Add(game);
        }

        foreach (var shortcut in shortcuts)
        {
            Entity? target;
            if (shortcut.ExeExists)
            {
                // 2. The installed launcher game whose folder holds the exe; the longest folder wins. Never one the
                //    launcher calls not installed: the exe exists, so the game is playable and must not be unticked.
                target = entities
                    .Where(e => e.Installed && e.Launchers.Count > 0 && e.InstallPath is not null && IsUnder(shortcut.ExePath!, e.InstallPath))
                    .OrderByDescending(e => e.InstallPath!.Length)
                    .FirstOrDefault();
            }
            else
            {
                // 3. A stale (or uncheckable) exe: an installed launcher game with the same name. Rescues shortcuts
                //    left behind when a game moved drives.
                var name = NormaliseName(shortcut.AppName);
                target = name.Length == 0 ? null : entities.FirstOrDefault(e =>
                    e.Installed && e.Launchers.Any(l => NormaliseName(l.Name) == name));
            }

            // 4. Otherwise the shortcut is a game of its own.
            if (target is null)
                entities.Add(target = new Entity(shortcut.ExePath is null ? null : Path.GetDirectoryName(shortcut.ExePath), shortcut.Installed));
            target.Shortcuts.Add(shortcut);
        }

        return entities.Select(e => e.ToGame()).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed class Entity(string? installPath, bool installed)
    {
        public string? InstallPath { get; } = installPath;
        public bool Installed { get; } = installed;
        public List<LauncherGame> Launchers { get; } = [];
        public List<SteamShortcut> Shortcuts { get; } = [];

        public NonSteamGame ToGame()
        {
            var launchers = Launchers
                .OrderBy(l => Array.IndexOf(SourcePreference, l.Source))
                .ThenBy(l => l.Key, StringComparer.Ordinal)
                .ToList();
            var primary = launchers.FirstOrDefault();
            var keys = launchers.Select(l => l.Key).Concat(Shortcuts.Select(s => s.Key)).Distinct().ToList();
            // 5. Launch through the shortcut whose exe exists, else the first one.
            var launch = Shortcuts.FirstOrDefault(s => s.Installed) ?? Shortcuts.FirstOrDefault();

            return new NonSteamGame(
                keys[0],
                keys,
                primary?.Name ?? Shortcuts[0].AppName,
                primary?.Source ?? "shortcut",
                InstallPath,
                launch?.GameId,
                launchers.Select(l => l.LaunchUri).FirstOrDefault(u => u is not null),
                Installed);
        }
    }

    /// <summary>Lowercase; drops ™®© and punctuation, with '-' and ':' becoming spaces; single spaces.</summary>
    private static string NormaliseName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is '-' or ':' || char.IsWhiteSpace(c)) sb.Append(' ');
            else if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Trims quotes, uses '\', resolves to a full path and drops the trailing '\'; null if not an absolute path.</summary>
    private static string? NormalisePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"').Trim().Replace('/', '\\');
        if (!Path.IsPathFullyQualified(path)) return null;
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return path.Length > (Path.GetPathRoot(path)?.Length ?? 0) ? path.TrimEnd('\\') : path;
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/>; G:\Games\EFT Arena\x.exe is not inside G:\Games\EFT.</summary>
    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool HasExe(string? folder) =>
        folder is not null && Directory.Exists(folder)
        && Directory.EnumerateFiles(folder, "*.exe").Any(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static XDocument LoadXml(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader);
    }
}

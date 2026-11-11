using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SteamSync;

/// <summary>
/// The CPU temperature through PawnIO (pawnio.eu), beta builds only and only once the user turns it on ("PC load" in
/// the tray menu). PawnIO is the Microsoft-signed driver LibreHardwareMonitor and Fan Control use in place of WinRing0;
/// reading the CPU's sensors with it needs administrator rights, so the relay helper (a LocalSystem service) does the
/// reading and this asks it over its pipe ("sensors", about once a second while a game runs). Without a relay the helper
/// is installed for this alone.
/// <para>
/// Turning it on never installs anything silently: it runs PawnIO's own signed installer, with its own UAC prompt,
/// after asking. That installer (GPL-2.0, so not shipped in this app) is downloaded from PawnIO's official GitHub
/// releases, which pawnio.eu links to, and only run when its SHA-256 is the one pinned here. A newer PawnIO means a new
/// pin. If the helper is missing or too old for "sensors", it's installed or updated too (a second UAC prompt).
/// </para>
/// </summary>
internal static class PawnIo
{
    private const string SetupUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
    private const string SetupSha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";
    private static readonly Version HelperWithSensors = new(1, 2, 0);
    private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(5);
    private static readonly string SettingFile = Path.Combine(AppInfo.DataDir, "cpu-temperature.txt");

    private static readonly object Gate = new();
    private static double? _celsius;
    private static string _state = "not read yet";
    private static DateTime _readAt = DateTime.MinValue, _askedAt = DateTime.MinValue;
    private static int _asking;

    /// <summary>The user turned the CPU temperature through PawnIO on (and it's a beta build).</summary>
    public static bool Enabled
    {
        get
        {
            if (!UpdateChannel.IsBetaBuild) return false;
            try { return File.Exists(SettingFile) && File.ReadAllText(SettingFile).Trim() == "pawnio"; }
            catch (IOException) { return false; }
        }
        set
        {
            if (value) File.WriteAllText(SettingFile, "pawnio");
            else File.Delete(SettingFile);
            Log.Info($"CPU temperature through PawnIO {(value ? "on" : "off")}");
        }
    }

    /// <summary>PawnIO is installed on this PC (by this app or another, like LibreHardwareMonitor or Fan Control).</summary>
    public static bool IsInstalled
    {
        get
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key?.GetValue("DisplayVersion") is string;
        }
    }

    /// <summary>The latest reading in °C, when it's on and the helper answered in the last few seconds; null otherwise.
    /// Asks the helper for a new one in the background, so a reading is about a second old.</summary>
    public static double? Read()
    {
        if (!Enabled || !GameRelay.HelperInstalled) return null;
        lock (Gate)
        {
            if (DateTime.UtcNow - _askedAt >= AskEvery && Interlocked.CompareExchange(ref _asking, 1, 0) == 0)
            {
                _askedAt = DateTime.UtcNow;
                _ = AskAsync();
            }
            return DateTime.UtcNow - _readAt < FreshFor ? _celsius : null;
        }
    }

    /// <summary>For the menu: the latest reading, or why there isn't one.</summary>
    public static string Status
    {
        get
        {
            Read();
            if (!GameRelay.HelperInstalled) return "the relay helper isn't installed";
            lock (Gate) return _celsius is { } c && DateTime.UtcNow - _readAt < FreshFor ? $"{c:0.0} °C" : _state;
        }
    }

    private static async Task AskAsync()
    {
        try
        {
            var reply = await RelayClient.SendAsync(new JsonObject { ["cmd"] = "sensors" });
            lock (Gate)
            {
                if (reply is null) _state = "the relay helper isn't answering";
                else if (!reply.ContainsKey("cpu_temp_state")) _state = "the relay helper is too old: turn this off and on again to update it";
                else
                {
                    _celsius = (double?)reply["cpu_temp_c"];
                    _state = (string?)reply["cpu_temp_state"] ?? "no reading";
                    _readAt = DateTime.UtcNow;
                }
            }
        }
        finally { Volatile.Write(ref _asking, 0); }
    }

    /// <summary>Turns it on: installs PawnIO if it isn't there (its own installer, its own UAC prompt), then the relay
    /// helper if it's missing or older than 1.2.0. Null when it's on, "" when a UAC prompt was declined, else what went
    /// wrong.</summary>
    public static async Task<string?> EnableAsync()
    {
        if (!IsInstalled)
        {
            var problem = await InstallAsync();
            if (problem is not null) return problem;
            if (!IsInstalled) return "PawnIO's installer closed without installing PawnIO.";
        }

        var status = await RelayClient.SendAsync(new JsonObject { ["cmd"] = "status" });
        var current = status is not null && Version.TryParse((string?)status["version"], out var v) && v >= HelperWithSensors;
        if (!current)
        {
            var problem = await GameRelay.InstallHelperAsync();
            if (problem is not null) return problem;
        }

        Enabled = true;
        Read();
        return null;
    }

    /// <summary>Downloads PawnIO's official installer, checks it's exactly the pinned one, and runs it with "-install"
    /// (as LibreHardwareMonitor does). Its manifest asks Windows for administrator rights, so the UAC prompt shows
    /// PawnIO's own signature.</summary>
    private static async Task<string?> InstallAsync()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"GamingEnjoyer-PawnIO-{Guid.NewGuid():N}"));
        var file = Path.Combine(dir.FullName, "PawnIO_setup.exe");
        try
        {
            byte[] bytes;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                bytes = await http.GetByteArrayAsync(SetupUrl);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return $"PawnIO's installer couldn't be downloaded: {ex.Message}";
            }
            await File.WriteAllBytesAsync(file, bytes);

            // Held open (others may only read) from the check until it has run, so nothing can swap it in between.
            await using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(hold)).Equals(SetupSha256, StringComparison.Ordinal))
            {
                Log.Info("PawnIO: the downloaded installer doesn't match the pinned SHA-256; not run");
                return "The PawnIO installer that downloaded isn't the version this app expects, so it wasn't run. A newer Gaming Enjoyer beta will know the new one.";
            }

            Log.Info("PawnIO: running its installer");
            try
            {
                using var process = Process.Start(new ProcessStartInfo(file, "-install") { UseShellExecute = true });
                if (process is null) return "PawnIO's installer didn't start.";
                await process.WaitForExitAsync();
                Log.Info($"PawnIO: installer exited with {process.ExitCode}");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return ""; // UAC prompt declined
            }
            return null;
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SteamSync;

internal static class AppInfo
{
    /// <summary>The name people see (installer, Start menu, tray, windows). The exe and data folder stay SteamSync.</summary>
    public const string Name = "Gaming Enjoyer";

    /// <summary>The app icon (embedded app.ico) for windows; the tray keeps its coloured status icons.</summary>
    public static Icon LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamSync.app.ico")!;
        return new Icon(stream);
    }

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string DataDir { get; } = Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamSync")).FullName;
}

internal sealed record AppConfig(string SupabaseUrl, string SupabaseAnonKey, string WebsiteUrl)
{
    public bool IsConfigured =>
        Uri.IsWellFormedUriString(SupabaseUrl, UriKind.Absolute)
        && !SupabaseUrl.Contains("YOUR-PROJECT-REF")
        && !string.IsNullOrWhiteSpace(SupabaseAnonKey)
        && !SupabaseAnonKey.StartsWith("YOUR-");

    /// <summary>Built-in appsettings.json, overridden by %LOCALAPPDATA%\SteamSync\appsettings.json if present.</summary>
    public static AppConfig Load()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var overridePath = Path.Combine(AppInfo.DataDir, "appsettings.json");
        if (File.Exists(overridePath))
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(overridePath), options)!;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamSync.appsettings.json")!;
        return JsonSerializer.Deserialize<AppConfig>(stream, options)!;
    }
}

/// <summary>The device token, encrypted with DPAPI so only this Windows user can read it.</summary>
internal static class DeviceToken
{
    private static readonly string FilePath = Path.Combine(AppInfo.DataDir, "device.bin");

    public static string? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException ex)
        {
            Log.Error("Stored device token could not be decrypted", ex);
            return null;
        }
    }

    public static void Save(string token) =>
        File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    public static void Clear()
    {
        // Called from error handling, so it must not throw itself.
        try { File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Could not delete the device token", ex); }
    }
}

/// <summary>Per-user "start with Windows" entry under HKCU\...\Run.</summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SteamSync";

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Keeps the Run entry pointing at the current exe after an update moves it.</summary>
    public static void RefreshPathIfEnabled()
    {
        if (IsEnabled) Set(true);
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogDir = Directory.CreateDirectory(Path.Combine(AppInfo.DataDir, "logs")).FullName;
    public static string FilePath { get; } = Path.Combine(LogDir, "steamsync.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
            // Logging must never throw: a failure here used to escape into the sync's own error handling, which
            // logs too, so the sync ended mid-way (after the data was sent) and the tray never left "Syncing…".
            catch (Exception) { }
        }
    }
}

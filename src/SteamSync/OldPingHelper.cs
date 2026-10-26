using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace SteamSync;

/// <summary>
/// The ping helper from the live ping betas (0.4.0-beta.6 to beta.17): a Windows service in Program Files. Live ping is
/// gone, so a PC that still has it gets "Remove old ping helper…" in the tray. Removing runs the installed helper's own
/// uninstall from a temporary copy (so it can delete its file), which asks for administrator rights once.
/// </summary>
internal static class OldPingHelper
{
    private const string ServiceName = "GamingEnjoyerPing";
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;
    private static readonly string InstalledExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Gaming Enjoyer", "GamingEnjoyerPing.exe");

    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
            return key is not null || File.Exists(InstalledExe);
        }
    }

    /// <summary>Null when it worked, "" when the admin prompt was declined, else what went wrong.</summary>
    public static async Task<string?> RemoveAsync()
    {
        string? copy = null;
        try
        {
            ProcessStartInfo start;
            if (File.Exists(InstalledExe))
            {
                copy = Path.Combine(Path.GetTempPath(), $"GamingEnjoyerPing-uninstall-{Guid.NewGuid():N}.exe");
                File.Copy(InstalledExe, copy);
                start = new ProcessStartInfo(copy, "uninstall");
            }
            else
            {
                // The service is registered but its file is gone: stop and delete it directly.
                start = new ProcessStartInfo("cmd.exe", $"/c sc stop {ServiceName} & sc delete {ServiceName}");
            }
            start.UseShellExecute = true;
            start.Verb = "runas";
            start.WindowStyle = ProcessWindowStyle.Hidden;
            using var p = Process.Start(start)!;
            await p.WaitForExitAsync();
            Log.Info($"Old ping helper removal exited with {p.ExitCode}");
            return IsInstalled ? $"The old ping helper couldn't be removed (code {p.ExitCode}). See the log." : null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // the UAC prompt was declined
        {
            return "";
        }
        catch (Exception ex)
        {
            Log.Error("Could not remove the old ping helper", ex);
            return ex.Message;
        }
        finally
        {
            if (copy is not null) try { File.Delete(copy); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

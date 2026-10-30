using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteamSync;

/// <summary>
/// An on/off switch and presets for Equalizer APO, the system-wide equalizer that Peace is a front end for. Equalizer
/// APO only processes the playback devices its Device Selector was pointed at, and reloads config\config.txt whenever it
/// changes, so this only edits text: each preset is an Equalizer APO config file in config\GamingEnjoyer, and config.txt
/// includes the chosen one when on, or keeps that line commented out when off (so switching back on remembers it).
/// The config folder is writable by every user (the installer sets it up that way), so none of this needs admin.
/// </summary>
internal sealed partial class Equalizer
{
    private const string PresetFolder = "GamingEnjoyer";
    private const string ApoClsid = "EACD2258-FCAC-4FF4-B36D-419E924A6D79";
    private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

    private readonly string _configDir;
    private readonly string? _installDir;

    public Equalizer(string configDir, string? installDir = null)
    {
        _configDir = configDir;
        _installDir = installDir;
    }

    /// <summary>The installed Equalizer APO, or null when it isn't installed.</summary>
    public static Equalizer? Find()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SOFTWARE\EqualizerAPO");
        return key?.GetValue("ConfigPath") is string dir && Directory.Exists(dir)
            ? new Equalizer(dir, key.GetValue("InstallPath") as string)
            : null;
    }

    public string PresetDir => Path.Combine(_configDir, PresetFolder);
    private string ConfigFile => Path.Combine(_configDir, "config.txt");
    private string PeacePreset => Path.Combine(_configDir, "peace.txt");
    private string PeaceSettings => Path.Combine(_configDir, "peace.ini");

    // Our line in config.txt: "Include: GamingEnjoyer\<preset>.txt", with "# " in front when off.
    [GeneratedRegex(@"^[ \t]*(#[ \t]*)?Include:[ \t]*GamingEnjoyer\\(.+?)\.txt[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex OurInclude();

    // Our preamp line, commented out along with the Include when off; and the marker that prevent clipping was turned off.
    [GeneratedRegex(@"^[ \t]*(?:#[ \t]*)?Preamp:[ \t]*(-?[\d.]+)[ \t]*dB", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex OurPreamp();
    private const string ClippingOffLine = "# Prevent clipping: off";

    /// <summary>
    /// What config.txt says: on or off, with which preset. <paramref name="OtherApp"/> is true when it isn't ours
    /// (Peace's "Include: peace.txt", say): switching on here then takes over, keeping a copy of what was there.
    /// <paramref name="Preamp"/> is the volume drop prevent clipping wrote (0 when it isn't needed or is off).
    /// </summary>
    public record struct EqState(bool On, string? Preset, bool OtherApp, bool PreventClipping, double Preamp);

    public EqState Read()
    {
        var text = File.Exists(ConfigFile) ? File.ReadAllText(ConfigFile) : "";
        if (OurInclude().Match(text) is { Success: true } m)
        {
            var preamp = OurPreamp().Match(text) is { Success: true } p ? double.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            return new EqState(On: !m.Groups[1].Success, m.Groups[2].Value, OtherApp: false,
                PreventClipping: !text.Contains(ClippingOffLine, StringComparison.OrdinalIgnoreCase), preamp);
        }
        var hasSettings = text.Split('\n').Any(l => l.Trim() is { Length: > 0 } t && !t.StartsWith('#'));
        return new EqState(On: hasSettings, null, OtherApp: hasSettings, PreventClipping: true, 0);
    }

    /// <summary>The preset names, from the .txt files in the preset folder. A ";" would split Equalizer APO's Include line, so those are skipped.</summary>
    public IReadOnlyList<string> Presets() =>
        Directory.Exists(PresetDir)
            ? Directory.GetFiles(PresetDir, "*.txt").Select(f => Path.GetFileNameWithoutExtension(f)).Where(n => !n.Contains(';')).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    /// <summary>
    /// Points config.txt at a preset, on or off. With prevent clipping (kept as it was when null; on by default), a
    /// preamp lowers the volume by the preset's loudest boost, worked out again each time, so editing a preset file is
    /// picked up the next time it's chosen. Equalizer APO picks the change up by itself within a moment.
    /// </summary>
    public void Set(bool on, string preset, bool? preventClipping = null)
    {
        var file = Path.Combine(PresetDir, preset + ".txt");
        if (!File.Exists(file)) throw new FileNotFoundException($"No preset called {preset}");
        var state = Read();
        var clip = preventClipping ?? state.PreventClipping;
        // The first time, keep what another app (Peace) had in config.txt, so it can be put back by hand.
        var backup = Path.Combine(PresetDir, "config.txt.before");
        if (state.OtherApp && !File.Exists(backup)) File.Copy(ConfigFile, backup);

        var off = on ? "" : "# ";
        var lines = new List<string> { "# Written by Gaming Enjoyer (tray menu > Equalizer). When it's off, its lines are commented out." };
        var headroom = clip ? Headroom(File.ReadAllText(file)) : 0;
        if (!clip) lines.Add(ClippingOffLine);
        else if (headroom > 0) lines.Add($"{off}Preamp: {(-headroom).ToString("0.0", CultureInfo.InvariantCulture)} dB");
        lines.Add($"{off}Include: {PresetFolder}\\{preset}.txt");
        File.WriteAllLines(ConfigFile, lines);
        Log.Info($"Equalizer {(on ? "on" : "off")} ({preset}{(headroom > 0 ? $", preamp -{headroom:0.0} dB" : "")})");
    }

    /// <summary>
    /// How far (dB) an Equalizer APO config boosts its loudest frequency, rounded up to 0.1 dB: the preamp that keeps a
    /// full-scale sound from clipping. It adds up the real response of the filters (overlapping boosts and cuts net out),
    /// the config's own Preamp lines and any GraphicEQ curve, from 10 Hz to 20 kHz. Filters for different channels are
    /// all added together, which can only overestimate. Filter types that never boost (band-pass, notch...) are skipped.
    /// </summary>
    public static double Headroom(string config)
    {
        const double rate = 48000;
        var freqs = Enumerable.Range(0, 1000).Select(i => 10 * Math.Pow(2000, i / 999.0)).ToArray(); // 10 Hz to 20 kHz
        var gain = new double[freqs.Length];
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;
            if (Regex.Match(line, @"^Preamp:\s*(-?[\d.]+)\s*dB", RegexOptions.IgnoreCase) is { Success: true } pre)
            {
                var db = Num(pre.Groups[1].Value);
                for (var i = 0; i < gain.Length; i++) gain[i] += db;
            }
            else if (Regex.Match(line, @"^GraphicEQ:(.*)$", RegexOptions.IgnoreCase) is { Success: true } geq)
            {
                // "f g; f g; ...", interpolated over log frequency like Equalizer APO does, held flat past the ends.
                var points = geq.Groups[1].Value.Split(';').Select(p => p.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(p => p.Length == 2).Select(p => (F: Math.Log(Num(p[0])), G: Num(p[1]))).Where(p => double.IsFinite(p.F)).OrderBy(p => p.F).ToArray();
                if (points.Length == 0) continue;
                for (var i = 0; i < gain.Length; i++)
                {
                    var lf = Math.Log(freqs[i]);
                    var k = Array.FindIndex(points, p => p.F >= lf);
                    gain[i] += k < 0 ? points[^1].G : k == 0 ? points[0].G
                        : points[k - 1].G + (points[k].G - points[k - 1].G) * (lf - points[k - 1].F) / (points[k].F - points[k - 1].F);
                }
            }
            else if (Regex.Match(line, @"^Filter\s*\d*\s*:\s*ON\s+([A-Z]+)\b(.*)$", RegexOptions.IgnoreCase) is { Success: true } f)
            {
                var type = f.Groups[1].Value.ToUpperInvariant();
                var rest = f.Groups[2].Value;
                if (Regex.Match(rest, @"Fc\s+([\d.]+)\s*Hz", RegexOptions.IgnoreCase) is not { Success: true } fc) continue;
                var g = Regex.Match(rest, @"Gain\s+(-?[\d.]+)\s*dB", RegexOptions.IgnoreCase) is { Success: true } gm ? Num(gm.Groups[1].Value) : 0;
                var q = Regex.Match(rest, @"\bQ\s+([\d.]+)", RegexOptions.IgnoreCase) is { Success: true } qm ? Num(qm.Groups[1].Value)
                    : Regex.Match(rest, @"BW Oct\s+([\d.]+)", RegexOptions.IgnoreCase) is { Success: true } bw ? 1 / (2 * Math.Sinh(Math.Log(2) / 2 * Num(bw.Groups[1].Value)))
                    : 0.7071;
                if (Biquad(type, Num(fc.Groups[1].Value), g, q, rate) is not { } c) continue;
                for (var i = 0; i < gain.Length; i++)
                {
                    var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * freqs[i] / rate);
                    var h = (c.B0 + c.B1 * z + c.B2 * z * z) / (c.A0 + c.A1 * z + c.A2 * z * z);
                    gain[i] += 20 * Math.Log10(h.Magnitude);
                }
            }
        }
        var peak = gain.Max();
        return peak > 0.005 ? Math.Ceiling(peak * 10 - 0.05) / 10 : 0;

        static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    private readonly record struct Coefficients(double B0, double B1, double B2, double A0, double A1, double A2);

    /// <summary>The RBJ "Audio EQ Cookbook" biquads Equalizer APO uses, for the filter types that can boost.</summary>
    private static Coefficients? Biquad(string type, double fc, double gainDb, double q, double rate)
    {
        if (!(fc > 0 && fc < rate / 2 && q > 0 && double.IsFinite(gainDb))) return null;
        var a = Math.Pow(10, gainDb / 40);
        var w = 2 * Math.PI * fc / rate;
        double cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), sa = 2 * Math.Sqrt(a) * alpha;
        return type switch
        {
            "PK" or "EQ" => new(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a),
            "LS" or "LSC" or "LSQ" => new(a * ((a + 1) - (a - 1) * cos + sa), 2 * a * ((a - 1) - (a + 1) * cos), a * ((a + 1) - (a - 1) * cos - sa),
                (a + 1) + (a - 1) * cos + sa, -2 * ((a - 1) + (a + 1) * cos), (a + 1) + (a - 1) * cos - sa),
            "HS" or "HSC" or "HSQ" => new(a * ((a + 1) + (a - 1) * cos + sa), -2 * a * ((a - 1) + (a + 1) * cos), a * ((a + 1) + (a - 1) * cos - sa),
                (a + 1) - (a - 1) * cos + sa, 2 * ((a - 1) - (a + 1) * cos), (a + 1) - (a - 1) * cos - sa),
            // Low/high-pass only boost through resonance (Q above 0.707), right at the corner.
            "LP" or "LPQ" => new((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha),
            "HP" or "HPQ" => new((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha),
            _ => null,
        };
    }

    /// <summary>
    /// Writes the presets built into the app (src\SteamSync\presets) into the preset folder when they're missing, so
    /// they come back after Equalizer APO is reinstalled. One deleted by hand comes back too; an edited one is left alone.
    /// </summary>
    public void RestoreBuiltInPresets()
    {
        const string prefix = "SteamSync.presets.";
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var target = Path.Combine(PresetDir, resource[prefix.Length..]);
            if (File.Exists(target)) continue;
            try
            {
                Directory.CreateDirectory(PresetDir);
                using var source = assembly.GetManifestResourceStream(resource)!;
                using var file = File.Create(target);
                source.CopyTo(file);
                Log.Info($"Equalizer preset restored: {Path.GetFileNameWithoutExtension(target)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Could not restore the equalizer preset {target}", ex);
            }
        }
    }

    public bool CanImportPeace => File.Exists(PeacePreset);

    /// <summary>
    /// Copies Peace's live settings (peace.txt, already in Equalizer APO's own format) into a preset named after the
    /// configuration selected in Peace, replacing a preset of that name. Returns the preset's name.
    /// </summary>
    public string ImportPeace()
    {
        var name = File.Exists(PeaceSettings)
            ? File.ReadLines(PeaceSettings).Select(l => l.Split('=', 2)).FirstOrDefault(p => p is ["Selected Configuration", _])?[1].Trim()
            : null;
        name = string.Concat((string.IsNullOrWhiteSpace(name) ? "From Peace" : name).Split(Path.GetInvalidFileNameChars().Append(';').ToArray()));
        Directory.CreateDirectory(PresetDir);
        File.Copy(PeacePreset, Path.Combine(PresetDir, name + ".txt"), overwrite: true);
        Log.Info($"Equalizer preset imported from Peace: {name}");
        return name;
    }

    /// <summary>
    /// The connected playback devices Equalizer APO is installed on (where the equalizer is heard), like "Fosi (Fosi Audio K5 Pro)".
    /// Empty when there are none, which happens when Windows sees a device as new (another USB port, say) and Equalizer
    /// APO has to be installed on it again with its Device Selector.
    /// </summary>
    public static IReadOnlyList<string> Devices()
    {
        var found = new List<string>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var render = hklm.OpenSubKey(RenderKey);
            if (render is null) return found;
            foreach (var id in render.GetSubKeyNames())
            {
                using var endpoint = render.OpenSubKey(id);
                // Low bits 1 = active; 0x10000000 is set on devices disabled in Sound settings (Device Selector's "Disabled").
                if (endpoint?.GetValue("DeviceState") is not int state || (state & 0xF) != 1 || (state & 0x10000000) != 0) continue;
                using var fx = endpoint.OpenSubKey("FxProperties");
                if (fx is null || !fx.GetValueNames().Any(v => fx.GetValue(v) is string s && s.Contains(ApoClsid, StringComparison.OrdinalIgnoreCase))) continue;
                using var props = endpoint.OpenSubKey("Properties");
                var name = props?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string ?? "Unnamed device";
                found.Add(props?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") is string device ? $"{name} ({device})" : name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("Could not list Equalizer APO's devices", ex);
        }
        return found;
    }

    /// <summary>Opens Equalizer APO's Device Selector (it asks for administrator rights itself).</summary>
    public void OpenDeviceSelector()
    {
        var exe = _installDir is null ? null : Path.Combine(_installDir, "DeviceSelector.exe");
        if (exe is null || !File.Exists(exe)) exe = _installDir is null ? null : Path.Combine(_installDir, "Configurator.exe"); // before 1.4
        if (exe is null || !File.Exists(exe)) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // the UAC prompt was declined
    }

    /// <summary>Peace rewrites config.txt when it's used, which would undo the switch here.</summary>
    public static bool PeaceRunning
    {
        get
        {
            var found = Process.GetProcessesByName("Peace");
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }
    }
}

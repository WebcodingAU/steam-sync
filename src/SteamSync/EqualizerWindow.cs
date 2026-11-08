using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SteamSync;

/// <summary>
/// A small window that shows what the equalizer is doing: the curve the preset applies from 20 Hz to 20 kHz with a dot on
/// each filter, the filters' values, and whether it's on and how clipping is handled. Pointing at the curve reads off the
/// frequency and gain there. It follows config.txt and the preset files, so an edit or a menu change shows at once.
/// </summary>
internal sealed class EqualizerWindow : Form
{
    private readonly Equalizer _eq;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Windows.Forms.Timer _reload = new() { Interval = 300 };
    private readonly Label _status = new() { Dock = DockStyle.Top, AutoSize = false, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _bands = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 72, Padding = new Padding(0, 4, 0, 0) };
    private readonly CheckBox _onTop = new() { Text = "Keep on top", AutoSize = true, Dock = DockStyle.Right };
    private readonly Label _limiter = new() { Dock = DockStyle.Top, AutoSize = false, Height = 36, TextAlign = ContentAlignment.MiddleLeft, Visible = false };
    private bool _showCounts;
    private readonly System.Windows.Forms.Timer _counts = new() { Interval = 1000 }; // the limiter's counts change without any file changing
    private readonly Graph _graph = new() { Dock = DockStyle.Fill };

    public EqualizerWindow(Equalizer eq)
    {
        _eq = eq;
        Text = $"Equalizer — {AppInfo.Name}";
        Icon = AppInfo.LoadIcon();
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(580, 360);
        MinimumSize = new Size(380, 240);
        Padding = new Padding(10, 6, 10, 8);
        Font = new Font("Segoe UI", 9f);

        var top = new Panel { Dock = DockStyle.Top, Height = 26 };
        top.Controls.Add(_status);
        top.Controls.Add(_onTop);
        _status.Dock = DockStyle.Fill;
        _onTop.CheckedChanged += (_, _) => TopMost = _onTop.Checked;
        Controls.Add(_graph);
        Controls.Add(_bands);
        Controls.Add(_limiter);
        Controls.Add(top);

        var dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        _graph.Dark = dark;
        BackColor = dark ? Color.FromArgb(32, 32, 32) : SystemColors.Window;
        ForeColor = dark ? Color.FromArgb(220, 220, 220) : SystemColors.WindowText;
        _onTop.ForeColor = ForeColor;
        if (dark) HandleCreated += (_, _) => { var on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); }; // dark title bar

        _watcher = new FileSystemWatcher(eq.ConfigDir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Renamed += Changed;
        _watcher.Deleted += Changed;
        _watcher.EnableRaisingEvents = true;
        _reload.Tick += (_, _) => { _reload.Stop(); Reload(); };
        _counts.Tick += (_, _) => { if (_showCounts) _limiter.Text = Equalizer.DescribeLimiter(Equalizer.ReadLimiterStats()); };
        _counts.Start();
        Reload();
    }

    // From the watcher's thread; editors save in bursts, so wait for it to settle.
    private void Changed(object sender, FileSystemEventArgs e)
    {
        if (!e.Name!.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || IsDisposed) return;
        try { BeginInvoke(() => { _reload.Stop(); _reload.Start(); }); }
        catch (InvalidOperationException) { } // closing
    }

    private void Reload()
    {
        var state = _eq.Read();
        var text = _eq.CurveText(state);
        var name = state.OtherApp ? "Peace" : state.Preset ?? "No preset";
        var clipping = state.Clipping switch
        {
            Equalizer.Clipping.Limiter => "only when needed",
            Equalizer.Clipping.Preamp => state.Preamp < 0 ? $"volume lowered {state.Preamp.ToString("0.0", CultureInfo.InvariantCulture)} dB" : "not needed",
            _ => "off",
        };
        var devices = Equalizer.Devices();
        _status.Text = $"{name}: {(state.On ? "on" : "off")}   ·   Prevent clipping: {clipping}"
            // "Fosi (Fosi Audio K5 Pro)" → "Fosi": the line has to fit next to Keep on top.
            + (devices.Count > 0 ? $"   ·   {string.Join(", ", devices.Select(d => d.Split(" (")[0]))}" : "   ·   not installed on a connected device");
        // Its own flag: a control reads as hidden until the window is on screen.
        _limiter.Visible = _showCounts = state.Clipping == Equalizer.Clipping.Limiter && !state.OtherApp;
        if (_showCounts) _limiter.Text = Equalizer.DescribeLimiter(Equalizer.ReadLimiterStats());

        if (text is null)
        {
            _graph.SetCurve(null, null, [], false);
            _bands.Text = "Pick a preset in the tray menu's Equalizer.";
            return;
        }
        var (freq, gain, bands, preamp) = Equalizer.Response(text);
        // The preset's own shape; a Preamp line in the preset only moves it, and is listed below.
        var shape = gain.Select(g => g - preamp).ToArray();
        _graph.SetCurve(freq, shape, bands, state.On);
        _bands.Text = (bands.Count == 0 ? "No filters" : string.Join("   ·   ", bands.OrderBy(b => b.Fc).Select(Describe)))
            + (preamp != 0 ? $"   ·   preset preamp {preamp.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)} dB" : "");
    }

    internal static string Describe(Equalizer.Band b) =>
        $"{Kind(b.Type)} {Hz(b.Fc)} {b.Gain.ToString("+0.0;−0.0;0", CultureInfo.InvariantCulture)} dB"
        + (b.Type is "PK" or "EQ" ? $" Q {b.Q.ToString("0.##", CultureInfo.InvariantCulture)}" : "");

    private static string Kind(string type) => type switch
    {
        "PK" or "EQ" => "Peak",
        "LS" or "LSC" or "LSQ" => "Low shelf",
        "HS" or "HSC" or "HSQ" => "High shelf",
        "LP" or "LPQ" => "Low-pass",
        "HP" or "HPQ" => "High-pass",
        _ => type,
    };

    internal static string Hz(double f) => f >= 1000 ? $"{(f / 1000).ToString(f >= 10000 ? "0.#" : "0.##", CultureInfo.InvariantCulture)} kHz" : $"{f:0} Hz";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher.Dispose();
            _reload.Dispose();
            _counts.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>The curve: frequency across (20 Hz to 20 kHz, by pitch), gain up, at least ±12 dB.</summary>
    private sealed class Graph : Control
    {
        private double[]? _freq, _gain;
        private IReadOnlyList<Equalizer.Band> _bands = [];
        private bool _on;
        private Point? _mouse;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Dark { get; set; }

        public Graph()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetCurve(double[]? freq, double[]? gain, IReadOnlyList<Equalizer.Band> bands, bool on)
        {
            (_freq, _gain, _bands, _on) = (freq, gain, bands, on);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.Location; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _mouse = null; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color back = Dark ? Color.FromArgb(24, 24, 24) : Color.White, grid = Dark ? Color.FromArgb(56, 56, 56) : Color.FromArgb(228, 228, 228),
                text = Dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110), zero = Dark ? Color.FromArgb(110, 110, 110) : Color.FromArgb(170, 170, 170),
                curve = _on ? (Dark ? Color.FromArgb(79, 195, 247) : Color.FromArgb(10, 116, 218)) : text;
            g.Clear(back);
            using var font = new Font("Segoe UI", 7.5f);
            using var textBrush = new SolidBrush(text);
            var plot = new Rectangle(30, 6, Width - 38, Height - 22);
            if (plot.Width < 50 || plot.Height < 40) return;

            var range = _gain is { } gg ? Math.Max(12, Math.Ceiling(gg.Max(Math.Abs) / 3) * 3) : 12;
            float X(double f) => plot.Left + (float)(Math.Log(f / 20) / Math.Log(1000) * plot.Width);
            float Y(double db) => plot.Top + (float)((range - db) / (2 * range) * plot.Height);

            using var gridPen = new Pen(grid);
            foreach (var f in new[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
            {
                g.DrawLine(gridPen, X(f), plot.Top, X(f), plot.Bottom);
                var label = f >= 1000 ? $"{f / 1000}k" : f.ToString(CultureInfo.InvariantCulture);
                var size = g.MeasureString(label, font);
                g.DrawString(label, font, textBrush, Math.Clamp(X(f) - size.Width / 2, plot.Left - 4, plot.Right - size.Width + 4), plot.Bottom + 2);
            }
            var step = range > 12 ? 6 : 3;
            for (var db = -range; db <= range; db += step)
            {
                using var pen = new Pen(db == 0 ? zero : grid);
                g.DrawLine(pen, plot.Left, Y(db), plot.Right, Y(db));
                var label = db > 0 ? $"+{db}" : db.ToString(CultureInfo.InvariantCulture);
                var size = g.MeasureString(label, font);
                g.DrawString(label, font, textBrush, plot.Left - size.Width - 2, Y(db) - size.Height / 2);
            }
            if (_freq is not { } freq || _gain is not { } gain) return;

            var points = Enumerable.Range(0, freq.Length).Where(i => freq[i] >= 20).Select(i => new PointF(X(freq[i]), Y(gain[i]))).ToArray();
            using (var fill = new GraphicsPath())
            {
                fill.AddLine(points[0].X, Y(0), points[0].X, points[0].Y);
                fill.AddLines(points);
                fill.AddLine(points[^1].X, points[^1].Y, points[^1].X, Y(0));
                using var brush = new SolidBrush(Color.FromArgb(Dark ? 40 : 28, curve));
                g.FillPath(brush, fill);
            }
            using (var pen = new Pen(curve, 2f)) g.DrawLines(pen, points);

            double At(double f) => gain[Math.Clamp((int)Math.Round(Math.Log(f / 10) / Math.Log(2000) * (freq.Length - 1)), 0, freq.Length - 1)];
            Equalizer.Band? near = null;
            using var dot = new SolidBrush(curve);
            foreach (var b in _bands.Where(b => b.Fc >= 20 && b.Fc <= 20000))
            {
                var p = new PointF(X(b.Fc), Y(At(b.Fc)));
                var hit = _mouse is { } m && Math.Abs(m.X - p.X) < 6;
                if (hit) near = b;
                g.FillEllipse(dot, p.X - (hit ? 4.5f : 3f), p.Y - (hit ? 4.5f : 3f), hit ? 9 : 6, hit ? 9 : 6);
            }

            // Reading off the curve where the pointer is, and the filter it's on.
            if (_mouse is { } mouse && mouse.X >= plot.Left && mouse.X <= plot.Right)
            {
                var f = 20 * Math.Pow(1000, (mouse.X - plot.Left) / (double)plot.Width);
                using var cursor = new Pen(zero) { DashStyle = DashStyle.Dot };
                g.DrawLine(cursor, mouse.X, plot.Top, mouse.X, plot.Bottom);
                var label = $"{Hz(f)}  {At(f).ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture)} dB" + (near is { } n ? $"   ({Describe(n)})" : "");
                using var bold = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                var size = g.MeasureString(label, bold);
                var x = Math.Clamp(mouse.X + 8, plot.Left, plot.Right - size.Width);
                using var box = new SolidBrush(Color.FromArgb(220, back));
                g.FillRectangle(box, x - 2, plot.Top + 2, size.Width + 4, size.Height);
                using var strong = new SolidBrush(Dark ? Color.White : Color.Black);
                g.DrawString(label, bold, strong, x, plot.Top + 2);
            }
        }
    }
}

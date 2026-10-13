namespace SteamSync;

/// <summary>Asks for the pairing code shown on the website and claims it.</summary>
internal sealed class PairDialog : Form
{
    private readonly SyncService _sync;
    private readonly TextBox _code = new() { CharacterCasing = CharacterCasing.Upper, MaxLength = 12, Width = 260, Font = new Font("Consolas", 14f) };
    private readonly TextBox _name = new() { Text = Environment.MachineName, MaxLength = 100, Width = 260 };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(260, 0) };
    private readonly Button _ok = new() { Text = "Pair", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

    public PairDialog(SyncService sync, string websiteUrl)
    {
        _sync = sync;
        Text = "Pair this PC — Steam Sync";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        AcceptButton = _ok;
        CancelButton = _cancel;
        Icon = TrayIcons.Create(Color.FromArgb(27, 40, 56));

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(260, 0),
            Text = $"On {new Uri(websiteUrl).Host}, sign in with Steam, open Your PCs and press \"Get a pairing code\".",
        });
        layout.Controls.Add(new Label { AutoSize = true, Text = "Pairing code", Margin = new Padding(3, 12, 3, 0) });
        layout.Controls.Add(_code);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Name for this PC", Margin = new Padding(3, 8, 3, 0) });
        layout.Controls.Add(_name);
        layout.Controls.Add(_error);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = 266, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_ok);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        _ok.Click += async (_, _) => await PairAsync();
    }

    private async Task PairAsync()
    {
        if (string.IsNullOrWhiteSpace(_code.Text))
        {
            _error.Text = "Enter the pairing code from the website.";
            return;
        }

        _ok.Enabled = _cancel.Enabled = _code.Enabled = _name.Enabled = false;
        _error.Text = "";
        try
        {
            await _sync.PairAsync(_code.Text.Trim(), _name.Text.Trim());
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (SupabaseException ex)
        {
            _error.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("Pairing failed", ex);
            _error.Text = $"Could not reach the server: {ex.Message}";
        }
        finally
        {
            _ok.Enabled = _cancel.Enabled = _code.Enabled = _name.Enabled = true;
        }
    }
}

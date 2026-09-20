using BeamerPresenter.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.App;

internal sealed class PresenterForm : Form
{
    private readonly IPresenterSettingsService _settingsService;
    private readonly Label _version = new() { AutoSize = true };
    private readonly Label _build = new() { AutoSize = true };
    private readonly Label _commit = new() { AutoSize = true };
    private readonly Label _runtime = new() { AutoSize = true };
    private readonly Label _presenterStatus = new() { AutoSize = true };
    private readonly TextBox _mediaFolder = new() { Dock = DockStyle.Fill };
    private readonly TextBox _webPort = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox _passwordRepeat = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Label _webUrl = new() { AutoSize = true };
    private readonly Label _passwordStatus = new() { AutoSize = true };
    private readonly NotifyIcon _notifyIcon;
    private bool _allowExit;

    public PresenterForm(WebApplication host)
    {
        _settingsService = host.Services.GetRequiredService<IPresenterSettingsService>();
        var buildInformation = BuildInformation.Current;
        _version.Text = buildInformation.Version;
        _build.Text = buildInformation.BuildTimestampUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "Nicht verfügbar";
        _commit.Text = buildInformation.ShortGitCommitSha;
        _runtime.Text = buildInformation.RuntimeVersion;
        _presenterStatus.Text = host.Services.GetRequiredService<PlaybackController>().State.ToString().ToUpperInvariant();
        Text = "Beamer Presenter for LAN-Parties"; MinimumSize = new Size(650, 560); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(CreateContent()); FormClosing += OnFormClosing; Shown += async (_, _) => await LoadSettingsAsync();
        var menu = new ContextMenuStrip(); menu.Items.Add("Web UI öffnen", null, (_, _) => OpenWebUi()); menu.Items.Add("Einstellungen", null, (_, _) => ShowFromTray()); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Beenden", null, (_, _) => ExitApplication());
        _notifyIcon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Beamer Presenter for LAN-Parties", Visible = true, ContextMenuStrip = menu };
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    protected override void Dispose(bool disposing) { if (disposing) _notifyIcon.Dispose(); base.Dispose(disposing); }
    private Control CreateContent()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 13 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        AddRow(root, 0, "Version:", _version); AddRow(root, 1, "Build:", _build); AddRow(root, 2, "Commit:", _commit); AddRow(root, 3, "Runtime:", _runtime); AddRow(root, 4, "Presenter:", _presenterStatus);
        AddRow(root, 5, "Web UI:", _webUrl); AddRow(root, 6, "Web UI Port:", _webPort); AddRow(root, 7, "Videoordner:", _mediaFolder); AddRow(root, 8, "Web-Passwort:", _password); AddRow(root, 9, "Passwort wiederholen:", _passwordRepeat); AddRow(root, 10, "Schutzstatus:", _passwordStatus);
        var save = new Button { Text = "Einstellungen speichern", AutoSize = true, Anchor = AnchorStyles.Left }; save.Click += async (_, _) => await SaveSettingsAsync(); root.Controls.Add(save, 1, 11);
        root.Controls.Add(new Label { AutoSize = true, Text = "Port-Änderungen gelten nach einem Neustart." }, 1, 12); return root;
    }
    private static void AddRow(TableLayoutPanel panel, int row, string label, Control input) { panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); panel.Controls.Add(input, 1, row); }
    private async Task LoadSettingsAsync() { var settings = await _settingsService.GetAsync(); _mediaFolder.Text = settings.MediaFolder; _webPort.Text = settings.WebPort.ToString(System.Globalization.CultureInfo.InvariantCulture); _webUrl.Text = $"http://localhost:{settings.WebPort}"; _passwordStatus.Text = string.IsNullOrWhiteSpace(settings.PasswordHash) ? "Noch nicht eingerichtet" : "Aktiv"; }
    private async Task SaveSettingsAsync()
    {
        if (string.IsNullOrWhiteSpace(_mediaFolder.Text)) { MessageBox.Show(this, "Bitte einen Videoordner angeben.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (!int.TryParse(_webPort.Text, out var webPort) || webPort is < 1024 or > 65535) { MessageBox.Show(this, "Bitte einen Port zwischen 1024 und 65535 angeben.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (!string.IsNullOrWhiteSpace(_password.Text) && _password.Text != _passwordRepeat.Text) { MessageBox.Show(this, "Die Passwörter stimmen nicht überein.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var settings = await _settingsService.GetAsync(); settings.MediaFolder = _mediaFolder.Text.Trim(); settings.WebPort = webPort; await _settingsService.SaveAsync(settings); if (!string.IsNullOrWhiteSpace(_password.Text)) await _settingsService.SetWebPasswordAsync(_password.Text);
        _password.Clear(); _passwordRepeat.Clear(); await LoadSettingsAsync(); MessageBox.Show(this, "Einstellungen gespeichert.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    private void OpenWebUi() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_webUrl.Text) { UseShellExecute = true });
    private void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void ExitApplication() { _allowExit = true; Close(); }
    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs) { if (!_allowExit && eventArgs.CloseReason == CloseReason.UserClosing) { eventArgs.Cancel = true; Hide(); } }
}

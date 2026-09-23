using BeamerPresenter.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.App;

internal sealed class PresenterForm : Form
{
    private readonly IPresenterSettingsService _settingsService;
    private readonly IMediaFolderService _mediaFolderService;
    private readonly IFfprobeService _ffprobeService;
    private readonly IMonitorService _monitorService;
    private readonly StartupRegistrationService _startupRegistration;
    private readonly PlaybackController _playback;
    private readonly PlaybackOrchestrator _playbackOrchestrator;
    private readonly IBrowserController _browser;
    private readonly Label _version = new() { AutoSize = true };
    private readonly Label _build = new() { AutoSize = true };
    private readonly Label _commit = new() { AutoSize = true };
    private readonly Label _runtime = new() { AutoSize = true };
    private readonly Label _presenterStatus = new() { AutoSize = true };
    private readonly Label _chromeStatus = new() { AutoSize = true };
    private readonly ComboBox _monitor = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _monitorStatus = new() { AutoSize = true };
    private readonly TextBox _chromePath = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _alwaysOnTop = new() { AutoSize = true, Checked = true, Text = "Always On Top" };
    private readonly CheckBox _aggressiveTopmost = new() { AutoSize = true, Text = "Aggressive Topmost" };
    private readonly CheckBox _preventDisplaySleep = new() { AutoSize = true, Checked = true, Text = "Bildschirmabschaltung verhindern" };
    private readonly CheckBox _preventSystemSleep = new() { AutoSize = true, Checked = true, Text = "Windows-Standby verhindern" };
    private readonly CheckBox _startWithWindows = new() { AutoSize = true, Text = "Mit Windows starten" };
    private readonly ListBox _mediaFolders = new() { Dock = DockStyle.Fill, Height = 90 };
    private readonly CheckBox _includeSubdirectories = new() { AutoSize = true, Checked = true, Text = "Unterverzeichnisse durchsuchen" };
    private readonly TextBox _webPort = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _allowLanAccess = new() { AutoSize = true, Text = "Web UI im LAN freigeben" };
    private readonly TextBox _ffprobePath = new() { Dock = DockStyle.Fill };
    private readonly Label _ffprobeStatus = new() { AutoSize = true };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox _passwordRepeat = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Label _webUrl = new() { AutoSize = true };
    private readonly Label _passwordStatus = new() { AutoSize = true };
    private readonly ToolStripMenuItem _trayStatus = new() { Enabled = false };
    private readonly ToolStripMenuItem _activatePresenter = new() { Text = "Presenter aktivieren" };
    private readonly ToolStripMenuItem _pausePresenter = new() { Text = "Presenter pausieren" };
    private readonly ToolStripMenuItem _hidePresenter = new() { Text = "Presenter ausblenden" };
    private readonly ToolStripMenuItem _stopPresenter = new() { Text = "Presenter stoppen" };
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2000 };
    private bool _allowExit;
    private bool _configuredMonitorMissing;
    private bool _statusRefreshInProgress;
    private string _localWebUrl = new UriBuilder(Uri.UriSchemeHttp, "localhost", 8765).Uri.AbsoluteUri;

    public PresenterForm(WebApplication host, bool startMinimized = false)
    {
        _settingsService = host.Services.GetRequiredService<IPresenterSettingsService>();
        _mediaFolderService = host.Services.GetRequiredService<IMediaFolderService>();
        _ffprobeService = host.Services.GetRequiredService<IFfprobeService>();
        _monitorService = host.Services.GetRequiredService<IMonitorService>();
        _startupRegistration = host.Services.GetRequiredService<StartupRegistrationService>();
        _playback = host.Services.GetRequiredService<PlaybackController>();
        _playbackOrchestrator = host.Services.GetRequiredService<PlaybackOrchestrator>();
        _browser = host.Services.GetRequiredService<IBrowserController>();
        var buildInformation = BuildInformation.Current;
        _version.Text = buildInformation.Version;
        _build.Text = buildInformation.BuildTimestampUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "Nicht verfügbar";
        _commit.Text = buildInformation.ShortGitCommitSha;
        _runtime.Text = buildInformation.RuntimeVersion;
        Text = "Beamer Presenter for LAN-Parties"; MinimumSize = new Size(740, 760); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(CreateContent()); FormClosing += OnFormClosing; Shown += async (_, _) =>
        {
            await LoadSettingsAsync();
            _statusTimer.Start();
            if (startMinimized)
            {
                Hide();
            }
        };
        _activatePresenter.Click += async (_, _) => await ChangePresenterStateAsync(_playbackOrchestrator.ActivateAsync, requiresMonitor: true);
        _pausePresenter.Click += async (_, _) => await ChangePresenterStateAsync(_playbackOrchestrator.PauseAsync);
        _hidePresenter.Click += async (_, _) => await ChangePresenterStateAsync(_playbackOrchestrator.HideAsync);
        _stopPresenter.Click += async (_, _) => await ChangePresenterStateAsync(_playbackOrchestrator.StopAsync);
        _monitor.SelectedIndexChanged += (_, _) => UpdateSelectedMonitorStatus();
        _statusTimer.Tick += async (_, _) => await RefreshRuntimeStatusAsync();
        var menu = CreateTrayMenu();
        _notifyIcon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Beamer Presenter for LAN-Parties", Visible = true, ContextMenuStrip = menu };
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
        UpdatePresenterStatus();
    }

    protected override void Dispose(bool disposing) { if (disposing) { _statusTimer.Dispose(); _notifyIcon.Dispose(); } base.Dispose(disposing); }
    private TableLayoutPanel CreateContent()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 22, AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        AddRow(root, 0, "Version:", _version); AddRow(root, 1, "Build:", _build); AddRow(root, 2, "Commit:", _commit); AddRow(root, 3, "Runtime:", _runtime); AddRow(root, 4, "Presenter:", _presenterStatus);
        AddRow(root, 5, "Chrome-Status:", _chromeStatus); AddRow(root, 6, "Monitor:", _monitor); AddRow(root, 7, "Monitorstatus:", _monitorStatus); AddRow(root, 8, "Chrome-Pfad:", CreateChromePathControl()); AddRow(root, 9, "Presenter-Optionen:", CreatePresenterOptionsControl());
        AddRow(root, 10, "Autostart:", _startWithWindows); AddRow(root, 11, "Web UI:", _webUrl); AddRow(root, 12, "Web UI Port:", _webPort); AddRow(root, 13, "Netzwerk:", _allowLanAccess); AddRow(root, 14, "Videoordner:", CreateMediaFolderControl()); AddRow(root, 15, "FFprobe-Pfad:", CreateFfprobePathControl()); AddRow(root, 16, "FFprobe-Status:", CreateFfprobeStatusControl()); AddRow(root, 17, "Web-Passwort:", _password); AddRow(root, 18, "Passwort wiederholen:", _passwordRepeat); AddRow(root, 19, "Schutzstatus:", _passwordStatus);
        var save = new Button { Text = "Einstellungen speichern", AutoSize = true, Anchor = AnchorStyles.Left }; save.Click += async (_, _) => await SaveSettingsAsync(); root.Controls.Add(save, 1, 20);
        root.Controls.Add(new Label { AutoSize = true, Text = "Port- und Netzwerkänderungen gelten nach einem Neustart." }, 1, 21); return root;
    }
    private static void AddRow(TableLayoutPanel panel, int row, string label, Control input) { panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); panel.Controls.Add(input, 1, row); }
    private TableLayoutPanel CreateMediaFolderControl()
    {
        var add = new Button { Text = "Hinzufügen", AutoSize = true };
        add.Click += async (_, _) => await AddMediaFolderAsync();
        var remove = new Button { Text = "Entfernen", AutoSize = true };
        remove.Click += async (_, _) => await RemoveMediaFolderAsync();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actions.Controls.Add(add);
        actions.Controls.Add(remove);
        actions.Controls.Add(_includeSubdirectories);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2, ColumnCount = 1 };
        panel.Controls.Add(_mediaFolders, 0, 0);
        panel.Controls.Add(actions, 0, 1);
        return panel;
    }
    private TableLayoutPanel CreateFfprobePathControl()
    {
        var select = new Button { Text = "Auswählen", AutoSize = true };
        select.Click += (_, _) => SelectFfprobePath();
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(_ffprobePath, 0, 0);
        panel.Controls.Add(select, 1, 0);
        return panel;
    }
    private TableLayoutPanel CreateChromePathControl()
    {
        var select = new Button { Text = "Auswählen", AutoSize = true };
        select.Click += (_, _) => SelectChromePath();
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(_chromePath, 0, 0);
        panel.Controls.Add(select, 1, 0);
        return panel;
    }
    private FlowLayoutPanel CreatePresenterOptionsControl()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(_alwaysOnTop);
        panel.Controls.Add(_aggressiveTopmost);
        panel.Controls.Add(_preventDisplaySleep);
        panel.Controls.Add(_preventSystemSleep);
        return panel;
    }
    private FlowLayoutPanel CreateFfprobeStatusControl()
    {
        var check = new Button { Text = "Erneut prüfen", AutoSize = true };
        check.Click += async (_, _) => await RefreshFfprobeStatusAsync();
        var install = new Button { Text = "FFmpeg installieren", AutoSize = true };
        install.Click += async (_, _) => await InstallFfprobeAsync();
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        panel.Controls.Add(_ffprobeStatus);
        panel.Controls.Add(check);
        panel.Controls.Add(install);
        return panel;
    }
    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_trayStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_activatePresenter);
        menu.Items.Add(_pausePresenter);
        menu.Items.Add(_hidePresenter);
        menu.Items.Add(_stopPresenter);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Web UI öffnen", null, (_, _) => OpenWebUi());
        menu.Items.Add("Einstellungen", null, (_, _) => ShowFromTray());
        menu.Items.Add("Status", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitApplication());
        return menu;
    }
    private async Task ChangePresenterStateAsync(Func<CancellationToken, Task> command, bool requiresMonitor = false)
    {
        if (requiresMonitor && (_configuredMonitorMissing || _monitor.SelectedItem is null))
        {
            MessageBox.Show(this, "Der konfigurierte Monitor ist nicht verfügbar. Bitte einen Fallback-Monitor auswählen und die Einstellungen speichern.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            await command(CancellationToken.None);
            UpdatePresenterStatus();
            await RefreshRuntimeStatusAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private void UpdatePresenterStatus()
    {
        _presenterStatus.Text = _playback.State.ToString().ToUpperInvariant();
        _trayStatus.Text = $"● Presenter {_playback.State.ToString().ToLowerInvariant()}";
        _activatePresenter.Enabled = _playback.State != BeamerPresenter.Domain.PresenterState.Active;
        _pausePresenter.Enabled = _playback.State == BeamerPresenter.Domain.PresenterState.Active;
        _hidePresenter.Enabled = _playback.State is BeamerPresenter.Domain.PresenterState.Active or BeamerPresenter.Domain.PresenterState.Paused;
        _stopPresenter.Enabled = _playback.State != BeamerPresenter.Domain.PresenterState.Stopped;
    }
    private async Task LoadSettingsAsync()
    {
        var settings = await _settingsService.GetAsync();
        _startWithWindows.Checked = _startupRegistration.IsEnabled();
        _webPort.Text = settings.WebPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _allowLanAccess.Checked = settings.AllowLanAccess;
        _ffprobePath.Text = settings.FfprobePath ?? string.Empty;
        _chromePath.Text = settings.ChromePath ?? string.Empty;
        _alwaysOnTop.Checked = settings.AlwaysOnTop;
        _aggressiveTopmost.Checked = settings.AggressiveTopmost;
        _preventDisplaySleep.Checked = settings.PreventDisplaySleep;
        _preventSystemSleep.Checked = settings.PreventSystemSleep;
        _localWebUrl = $"http://localhost:{settings.WebPort}";
        _webUrl.Text = PresenterNetworkAddresses.FormatWebUrls(settings.WebPort, settings.AllowLanAccess, PresenterNetworkAddresses.GetLanIpv4Addresses());
        _passwordStatus.Text = string.IsNullOrWhiteSpace(settings.PasswordHash) ? "Noch nicht eingerichtet" : "Aktiv";
        LoadMonitors(settings.MonitorDeviceName);
        await LoadMediaFoldersAsync();
        await RefreshFfprobeStatusAsync();
        await RefreshRuntimeStatusAsync();
    }
    private async Task LoadMediaFoldersAsync()
    {
        var folders = await _mediaFolderService.GetAllAsync();
        _mediaFolders.Items.Clear();
        _mediaFolders.Items.AddRange(folders.Select(folder => new MediaFolderListItem(folder.Id, folder.Path, folder.IncludeSubdirectories)).ToArray());
    }
    private async Task AddMediaFolderAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "Videoordner auswählen", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await _mediaFolderService.AddAsync(dialog.SelectedPath, _includeSubdirectories.Checked);
        await LoadMediaFoldersAsync();
    }
    private async Task RemoveMediaFolderAsync()
    {
        if (_mediaFolders.SelectedItem is not MediaFolderListItem selectedFolder)
        {
            return;
        }

        await _mediaFolderService.RemoveAsync(selectedFolder.Id);
        await LoadMediaFoldersAsync();
    }
    private void SelectFfprobePath()
    {
        using var dialog = new OpenFileDialog { Filter = "FFprobe|ffprobe.exe|Programme|*.exe", CheckFileExists = true, Multiselect = false, Title = "FFprobe auswählen" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _ffprobePath.Text = dialog.FileName;
        }
    }
    private void SelectChromePath()
    {
        using var dialog = new OpenFileDialog { Filter = "Google Chrome|chrome.exe|Programme|*.exe", CheckFileExists = true, Multiselect = false, Title = "Chrome auswählen" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _chromePath.Text = dialog.FileName;
        }
    }
    private void LoadMonitors(string? configuredDeviceName)
    {
        _monitor.Items.Clear();
        var monitors = _monitorService.GetAll().Select(DisplayMonitorListItem.From).ToArray();
        _monitor.Items.AddRange(monitors);
        var configuredMonitor = monitors.FirstOrDefault(item => string.Equals(item.DeviceName, configuredDeviceName, StringComparison.OrdinalIgnoreCase));
        _configuredMonitorMissing = !string.IsNullOrWhiteSpace(configuredDeviceName) && configuredMonitor is null;
        if (configuredMonitor is not null)
        {
            _monitor.SelectedItem = configuredMonitor;
        }
        else if (!_configuredMonitorMissing)
        {
            _monitor.SelectedItem = monitors.FirstOrDefault(item => item.IsPrimary) ?? monitors.FirstOrDefault();
        }

        if (_configuredMonitorMissing)
        {
            _monitorStatus.Text = $"Nicht verfügbar: {configuredDeviceName}";
        }
        else if (monitors.Length == 0)
        {
            _monitorStatus.Text = "Keine Anzeige erkannt";
        }
        else
        {
            _monitorStatus.Text = "Verfügbar";
        }
    }
    private void UpdateSelectedMonitorStatus()
    {
        if (_configuredMonitorMissing && _monitor.SelectedItem is DisplayMonitorListItem)
        {
            _monitorStatus.Text = "Fallback ausgewählt – bitte speichern";
        }
        else if (_monitor.SelectedItem is DisplayMonitorListItem)
        {
            _monitorStatus.Text = "Verfügbar";
        }
    }
    private async Task RefreshFfprobeStatusAsync()
    {
        _ffprobeStatus.Text = "Wird geprüft …";
        var availability = await _ffprobeService.CheckAvailabilityAsync();
        _ffprobeStatus.Text = availability.IsAvailable
            ? $"✓ {availability.Version}"
            : $"✗ {availability.Error}";
    }
    private async Task InstallFfprobeAsync()
    {
        _ffprobeStatus.Text = "Installation läuft …";
        var availability = await _ffprobeService.InstallWithWinGetAsync();
        _ffprobeStatus.Text = availability.IsAvailable
            ? $"✓ {availability.Version}"
            : $"✗ {availability.Error}";
    }
    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(_webPort.Text, out var webPort) || webPort is < 1024 or > 65535) { MessageBox.Show(this, "Bitte einen Port zwischen 1024 und 65535 angeben.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (!string.IsNullOrWhiteSpace(_password.Text) && _password.Text != _passwordRepeat.Text) { MessageBox.Show(this, "Die Passwörter stimmen nicht überein.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        try { _startupRegistration.SetEnabled(_startWithWindows.Checked); } catch (UnauthorizedAccessException) { MessageBox.Show(this, "Der Windows-Autostart konnte nicht geändert werden.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (_monitor.SelectedItem is not DisplayMonitorListItem selectedMonitor) { MessageBox.Show(this, "Bitte einen verfügbaren Monitor auswählen.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var settings = await _settingsService.GetAsync(); settings.WebPort = webPort; settings.AllowLanAccess = _allowLanAccess.Checked; settings.FfprobePath = string.IsNullOrWhiteSpace(_ffprobePath.Text) ? null : Path.GetFullPath(_ffprobePath.Text.Trim()); settings.ChromePath = string.IsNullOrWhiteSpace(_chromePath.Text) ? null : Path.GetFullPath(_chromePath.Text.Trim()); settings.MonitorDeviceName = selectedMonitor.DeviceName; settings.AlwaysOnTop = _alwaysOnTop.Checked; settings.AggressiveTopmost = _aggressiveTopmost.Checked; settings.PreventDisplaySleep = _preventDisplaySleep.Checked; settings.PreventSystemSleep = _preventSystemSleep.Checked; await _settingsService.SaveAsync(settings); if (!string.IsNullOrWhiteSpace(_password.Text)) await _settingsService.SetWebPasswordAsync(_password.Text);
        _password.Clear(); _passwordRepeat.Clear(); await LoadSettingsAsync(); MessageBox.Show(this, "Einstellungen gespeichert.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    private async Task RefreshRuntimeStatusAsync()
    {
        if (_statusRefreshInProgress || IsDisposed)
        {
            return;
        }

        _statusRefreshInProgress = true;
        try
        {
            _chromeStatus.Text = await _browser.IsRunningAsync() ? "Läuft" : "Gestoppt";
            UpdatePresenterStatus();
        }
        catch (Exception)
        {
            _chromeStatus.Text = "Status nicht verfügbar";
        }
        finally
        {
            _statusRefreshInProgress = false;
        }
    }
    private void OpenWebUi() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_localWebUrl) { UseShellExecute = true });
    private void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    internal void ShowFromExternalLaunch() => ShowFromTray();
    private void ExitApplication() { _allowExit = true; Close(); }
    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs) { if (!_allowExit && eventArgs.CloseReason == CloseReason.UserClosing) { eventArgs.Cancel = true; Hide(); } }

    private sealed record MediaFolderListItem(int Id, string Path, bool IncludeSubdirectories)
    {
        public override string ToString() => IncludeSubdirectories ? $"{Path} (inkl. Unterordner)" : Path;
    }

    private sealed record DisplayMonitorListItem(string DeviceName, string FriendlyName, int X, int Y, int Width, int Height, bool IsPrimary)
    {
        public static DisplayMonitorListItem From(DisplayMonitor monitor) =>
            new(monitor.DeviceName, monitor.FriendlyName, monitor.X, monitor.Y, monitor.Width, monitor.Height, monitor.IsPrimary);

        public override string ToString() => $"{FriendlyName} / {DeviceName} — {Width} × {Height} @ {X}, {Y}{(IsPrimary ? " (Primär)" : string.Empty)}";
    }
}

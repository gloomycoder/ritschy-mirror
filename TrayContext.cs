using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// Tray host: the app starts at login, waits idle in the tray and hosts the HTTP agent.
/// "Start mirror" begins rendering, "Stop mirror" ends it — the app stays in the
/// tray and the agent remains reachable. Replaces the former session-0 scheduled-task workaround
/// (runs in the interactive session → always has display access).
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly string _appSettingsPath;
    private readonly MirrorEngine _engine;
    private readonly AppSettings _app;
    private ControlAgent? _agent;

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _settingsItem, _logItem, _quitItem;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private SettingsForm? _settings;

    private readonly Icon _iconOn;       // tray green = mirror running
    private readonly Icon _iconOff;      // tray red   = mirror stopped
    private bool? _lastRunning;          // only swap the icon on a status change

    // Single instance: a second instance signals "show settings" through this.
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showReg;
    private readonly Control _marshal = new();  // UI-thread marshaling (handle anchor)

    private string? _pendingUpdateUrl;          // target of the "update available" balloon
    private const int MaxTrayTextLength = 127;  // NotifyIcon.Text throws above this

    public TrayContext(string[]? args = null)
    {
        // config\, lang\, log\ next to the exe (migrates the old flat layout, rotates logs).
        var pathNotes = AppPaths.Prepare();
        _appSettingsPath = AppPaths.AppSettings;
        _app = AppSettings.Load(_appSettingsPath);
        L.Init(AppPaths.Base, _app.Language);
        _engine = new MirrorEngine(AppPaths.MirrorConfig, AppPaths.LogFile);
        _engine.Log($"RitschyMirror v{AppInfo.Version} started — log {Path.GetFileName(AppPaths.LogFile)}");
        foreach (var note in pathNotes) _engine.Log(note);

        _iconOn = AppIcon.LoadStatus(running: true);
        _iconOff = AppIcon.LoadStatus(running: false);
        _ = _marshal.Handle;  // forces handle creation for cross-thread BeginInvoke

        _toggleItem = new ToolStripMenuItem(L.T("tray.start"), null, (_, _) => ToggleMirror());
        _settingsItem = new ToolStripMenuItem(L.T("tray.settings"), null, (_, _) => OpenSettings());
        _logItem = new ToolStripMenuItem(L.T("tray.open_log"), null, (_, _) => OpenLog());
        _quitItem = new ToolStripMenuItem(L.T("tray.quit"), null, (_, _) => Quit());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_logItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_quitItem);

        _tray = new NotifyIcon
        {
            Icon = _iconOff,
            Text = "RitschyMirror",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.BalloonTipClicked += (_, _) => { if (_pendingUpdateUrl != null) OpenUrl(_pendingUpdateUrl); };

        // Single instance: listen for the "show settings" signal from a second instance.
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSettingsEvent);
            _showReg = ThreadPool.RegisterWaitForSingleObject(
                _showEvent, (_, _) => { try { _marshal.BeginInvoke((Action)OpenSettings); } catch { } },
                null, -1, executeOnlyOnce: false);
        }
        catch { /* the app runs without signaling anyway (single-instance via mutex still applies) */ }

        // Start the agent (if enabled)
        if (_app.AgentEnabled)
        {
            try
            {
                _agent = new ControlAgent(_engine, _app.AgentBind, _app.AgentPort);
                _agent.Start();
            }
            catch (Exception ex) { _engine.Log("Agent start failed: " + ex.Message); }
        }

        if (_app.AutostartMirror)
        {
            var err = _engine.Preflight();
            if (err == null) _engine.Start();
            else _engine.Log("Autostart mirror skipped: " + err.English);
        }

        // Keep menu/tooltip status up to date
        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();
        RefreshUi();

        // Open the settings directly (e.g. a dedicated "Settings" shortcut / testing).
        if (args != null && Array.Exists(args, a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
            OpenSettings();

        StartUpdateCheck();  // quietly check for a newer version at startup (best-effort)
    }

    /// <summary>Best-effort update notice at startup: query the GitHub release, and show a
    /// clickable tray balloon if a newer version exists. Error/offline = silently nothing.</summary>
    private async void StartUpdateCheck()
    {
        var r = await UpdateCheck.CheckAsync().ConfigureAwait(false);
        if (r is not { UpdateAvailable: true }) return;
        _pendingUpdateUrl = r.Url;
        try
        {
            _marshal.BeginInvoke((Action)(() =>
            {
                _tray.BalloonTipTitle = L.T("tray.update.title");
                _tray.BalloonTipText = L.T("tray.update.text", r.LatestVersion);
                _tray.ShowBalloonTip(6000);
            }));
        }
        catch { }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void ToggleMirror()
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
        }
        else
        {
            var err = _engine.Preflight();
            if (err != null)
            {
                _tray.ShowBalloonTip(4000, "RitschyMirror", err.Text + ".", ToolTipIcon.Warning);
                RefreshUi();
                return;
            }
            _engine.Start();
        }
        RefreshUi();
    }

    private void OpenSettings()
    {
        if (_settings is { IsDisposed: false }) { _settings.Activate(); return; }
        _settings = new SettingsForm(_engine, _appSettingsPath, _app, ApplyLanguage);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
    }

    /// <summary>Language picked in the settings: reload the packs, relabel the tray and rebuild
    /// the settings window (its controls are created once, so reopening is the simplest relabel).</summary>
    private void ApplyLanguage()
    {
        L.Init(AppPaths.Base, _app.Language);
        _settingsItem.Text = L.T("tray.settings");
        _logItem.Text = L.T("tray.open_log");
        _quitItem.Text = L.T("tray.quit");
        RefreshUi();
        var old = _settings;
        _settings = null;
        if (old is { IsDisposed: false })
        {
            var location = old.Location;
            old.Close();
            OpenSettings();
            if (_settings != null) { _settings.StartPosition = FormStartPosition.Manual; _settings.Location = location; }
        }
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(_engine.LogPath)) File.WriteAllText(_engine.LogPath, "");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_engine.LogPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _engine.Log("Failed to open log: " + ex.Message); }
    }

    private void RefreshUi()
    {
        bool running = _engine.IsRunning;
        _toggleItem.Text = L.T(running ? "tray.stop" : "tray.start");
        string bind = _agent?.BoundPrefix ?? L.T("tray.agent_off");
        string text = $"RitschyMirror — {L.T(running ? "tray.state.running" : "tray.state.idle")}\n{bind}";
        _tray.Text = text.Length <= MaxTrayTextLength ? text : text[..MaxTrayTextLength];
        if (_lastRunning != running)  // swap the green/red tray icon only on a status change
        {
            _lastRunning = running;
            _tray.Icon = running ? _iconOn : _iconOff;
        }
    }

    private void Quit()
    {
        _uiTimer.Stop();
        try { _showReg?.Unregister(null); } catch { }
        try { _showEvent?.Dispose(); } catch { }
        try { _engine.Stop(); } catch { }
        try { _engine.Audio.Dispose(); } catch { }
        try { _agent?.Stop(); } catch { }
        _tray.Visible = false;
        _tray.Dispose();
        try { _marshal.Dispose(); } catch { }
        ExitThread();
    }
}

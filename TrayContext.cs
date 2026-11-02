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
    private readonly string _baseDir;
    private readonly string _appSettingsPath;
    private readonly MirrorEngine _engine;
    private readonly AppSettings _app;
    private ControlAgent? _agent;

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _toggleItem;
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

    public TrayContext(string[]? args = null)
    {
        _baseDir = AppContext.BaseDirectory;
        _appSettingsPath = Path.Combine(_baseDir, "app_settings.json");
        _app = AppSettings.Load(_appSettingsPath);
        _engine = new MirrorEngine(_baseDir);

        _iconOn = AppIcon.LoadStatus(running: true);
        _iconOff = AppIcon.LoadStatus(running: false);
        _ = _marshal.Handle;  // forces handle creation for cross-thread BeginInvoke

        _toggleItem = new ToolStripMenuItem("Mirror starten", null, (_, _) => ToggleMirror());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("Einstellungen…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Log öffnen", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Beenden", null, (_, _) => Quit()));

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
            else _engine.Log("Autostart mirror skipped: " + err);
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
                _tray.BalloonTipTitle = "RitschyMirror — Update verfügbar";
                _tray.BalloonTipText = $"Version {r.LatestVersion} ist da. Klick zum Öffnen.";
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
                _tray.ShowBalloonTip(4000, "RitschyMirror", err + ".", ToolTipIcon.Warning);
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
        _settings = new SettingsForm(_engine, _appSettingsPath, _app);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
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
        _toggleItem.Text = running ? "Mirror stoppen" : "Mirror starten";
        string bind = _agent?.BoundPrefix ?? "Agent aus";
        _tray.Text = $"RitschyMirror — {(running ? "läuft" : "idle")}\n{bind}";
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
        try { _agent?.Stop(); } catch { }
        _tray.Visible = false;
        _tray.Dispose();
        try { _marshal.Dispose(); } catch { }
        ExitThread();
    }
}

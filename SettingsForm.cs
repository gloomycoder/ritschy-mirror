using Microsoft.Win32;
using System.Drawing;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// Local settings GUI — the same parameters that are otherwise set remotely via the cockpit.
/// Writes live to mirror_config.json (the engine reloads LIVE keys immediately);
/// structural changes (display/mode/bit depth) require a render restart.
/// App-level settings (agent/autostart) go to app_settings.json.
///
/// Layout is deliberately MANUAL (Location/Size, scrollable panel) — nested
/// AutoSize containers (FlowLayout→GroupBox→TableLayout) collapsed to height 0.
/// </summary>
public sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RitschyMirror";
    private const int LX = 16, CX = 230, VX = 500, ROW = 32;

    private readonly MirrorEngine _engine;
    private readonly string _appSettingsPath;
    private readonly AppSettings _app;
    private MirrorConfig _cfg;
    private bool _loading;

    private readonly Panel _panel;
    private int _y = 12;
    private ComboBox _srcCombo = null!, _dstCombo = null!;
    private ComboBox _modeCombo = null!, _winCombo = null!;
    private List<DisplayInfo> _displays = new();
    private List<WindowInfo> _windows = new();

    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    public SettingsForm(MirrorEngine engine, string appSettingsPath, AppSettings app)
    {
        _engine = engine; _appSettingsPath = appSettingsPath; _app = app;
        _cfg = MirrorConfig.Load(engine.ConfigPath);

        Text = $"RitschyMirror — Einstellungen  v{AppInfo.Version}";
        Icon = AppIcon.Load(small: false);
        ClientSize = new Size(620, 780);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 420);
        // On small/short screens never taller than the working area — otherwise the
        // bottom button bar (Close / Check for updates) slides out of the visible window.
        var wa = Screen.PrimaryScreen?.WorkingArea;
        if (wa is { } a && Height > a.Height - 16) Height = a.Height - 16;

        _panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = SystemColors.ControlLight };
        Controls.Add(_panel);
        Controls.Add(bottom);

        _loading = true;
        BuildSourceTarget();
        BuildImage();
        BuildCrop();
        BuildConnection();
        BuildAbout();
        _loading = false;

        var start = new Button { Text = "Start / Stop", Location = new Point(12, 9), Width = 120, Height = 30 };
        start.Click += (_, _) => { if (_engine.IsRunning) _engine.Stop(); else StartWithCheck(); };
        var restart = new Button { Text = "Neustart (Struktur)", Location = new Point(140, 9), Width = 160, Height = 30 };
        restart.Click += (_, _) => { _engine.Stop(); StartWithCheck(); };
        var close = new Button { Text = "Schließen", Location = new Point(308, 9), Width = 110, Height = 30 };
        close.Click += (_, _) => Close();
        var update = new Button { Text = "↻ Auf Updates prüfen", Location = new Point(426, 9), Width = 172, Height = 30 };
        update.Click += (_, _) => CheckForUpdates(update);
        bottom.Controls.AddRange(new Control[] { start, restart, close, update });
    }

    /// <summary>Start with a preflight check — if the source/target monitor is missing, show a clear
    /// message instead of silently mirroring the wrong screen.</summary>
    private void StartWithCheck()
    {
        var err = _engine.Preflight();
        if (err != null)
        {
            MessageBox.Show(this, err + ".", "RitschyMirror", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _engine.Start();
    }

    // ── Persistence ────────────────────────────────────────────────────────
    private void SaveCfg() { if (_loading) return; try { _cfg.Save(_engine.ConfigPath); } catch { } }
    private void SaveApp() { if (_loading) return; _app.Save(_appSettingsPath); }

    // ── Layout helpers (manual, y cursor) ────────────────────────────────
    private void Header(string text)
    {
        _y += 8;
        _panel.Controls.Add(new Label { Text = text, Location = new Point(LX - 4, _y), AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _y += 24;
    }

    private void Lbl(string text) =>
        _panel.Controls.Add(new Label { Text = text, Location = new Point(LX, _y + 4), AutoSize = true });

    private void Note(string text)
    {
        _panel.Controls.Add(new Label { Text = text, Location = new Point(LX, _y), AutoSize = true, ForeColor = Color.DimGray });
        _y += 22;
    }

    private ComboBox ComboRow(string label, string[] items, string current, Action<string> set)
    {
        Lbl(label);
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 250 };
        c.Items.AddRange(items);
        c.SelectedItem = items.FirstOrDefault(i => string.Equals(i, current, StringComparison.OrdinalIgnoreCase)) ?? (items.Length > 0 ? items[0] : null);
        c.SelectedIndexChanged += (_, _) => { if (!_loading && c.SelectedItem is string s) { set(s); SaveCfg(); } };
        _panel.Controls.Add(c);
        _y += ROW;
        return c;
    }

    private void FloatRow(string label, float min, float max, float val, string fmt, Action<float> set)
    {
        Lbl(label);
        var tb = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Location = new Point(CX, _y - 4), Width = 260 };
        var vl = new Label { Location = new Point(VX, _y + 4), AutoSize = true, Text = val.ToString(fmt) };
        int ToTick(float v) => (int)Math.Round((v - min) / (max - min) * 1000);
        float ToVal(int t) => min + (max - min) * t / 1000f;
        tb.Value = Math.Clamp(ToTick(val), 0, 1000);
        tb.Scroll += (_, _) => { float v = ToVal(tb.Value); vl.Text = v.ToString(fmt); if (!_loading) { set(v); SaveCfg(); } };
        _panel.Controls.Add(tb); _panel.Controls.Add(vl);
        _y += 42;
    }

    private NumericUpDown NumRow(string label, decimal min, decimal max, decimal val, decimal step, int dec, Action<decimal> set, bool app = false)
    {
        Lbl(label);
        var n = new NumericUpDown { Location = new Point(CX, _y), Width = 110, Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max), Increment = step, DecimalPlaces = dec };
        n.ValueChanged += (_, _) => { if (!_loading) { set(n.Value); if (app) SaveApp(); else SaveCfg(); } };
        _panel.Controls.Add(n);
        _y += ROW;
        return n;
    }

    private CheckBox CheckRow(string label, bool val, Action<bool> set, bool app = false)
    {
        var c = new CheckBox { Text = label, Location = new Point(LX, _y), AutoSize = true, Checked = val };
        c.CheckedChanged += (_, _) => { if (!_loading) { set(c.Checked); if (app) SaveApp(); else SaveCfg(); } };
        _panel.Controls.Add(c);
        _y += 28;
        return c;
    }

    // ── Groups ───────────────────────────────────────────────────────────
    private void BuildSourceTarget()
    {
        Header("Quelle / Ziel & Modus");
        _displays = MirrorEngine.EnumerateDisplays();
        var names = DisplayNames();

        // Source mode: whole monitor (Desktop Duplication) OR a single window (Windows.Graphics.Capture).
        _modeCombo = ComboRow("Quellen-Modus", new[] { "monitor", "window" }, _cfg.ResolveCaptureMode(),
                              s => _cfg.CaptureMode = s);
        Note("monitor = ganzer Bildschirm  ·  window = nur EIN Fenster / Vollbild-App");
        Note("(Fenster-Modus zeigt beim Raustaben NICHT deinen Desktop — sicherer.)");

        Lbl("Quell-Monitor  (Modus „monitor\")");
        _srcCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 330 };
        _srcCombo.Items.AddRange(names);
        int siSel = FindByIdentity(_cfg.SourceKey, _cfg.SourceLabel, _cfg.SourceDisplay);
        if (siSel >= 0) _srcCombo.SelectedIndex = siSel;
        _srcCombo.SelectedIndexChanged += (_, _) => { if (!_loading) StoreSelection(_srcCombo, src: true); };
        _panel.Controls.Add(_srcCombo);
        _y += ROW;

        // Window source ("window" mode) — identity-stable via exe + title, like the monitor selection.
        _windows = WindowEnum.List();
        Lbl("Fenster  (Modus „window\")");
        _winCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 330 };
        _winCombo.Items.AddRange(_windows.Select(w => w.DisplayName).ToArray());
        int wiSel = FindWindowIndex();
        if (wiSel >= 0) _winCombo.SelectedIndex = wiSel;
        _winCombo.SelectedIndexChanged += (_, _) => { if (!_loading) StoreWindow(); };
        _panel.Controls.Add(_winCombo);
        var winRefresh = new Button { Text = "↻", Location = new Point(CX + 338, _y - 1), Width = 32, Height = 26 };
        winRefresh.Click += (_, _) => RefreshWindows();
        _panel.Controls.Add(winRefresh);
        _y += ROW;
        if (wiSel < 0 && HasWindowIdentity())
            Note($"⚠ Gespeichertes Fenster \"{WindowLabel()}\" aktuell nicht offen (Auswahl bleibt gespeichert).");

        Lbl("Ziel-Monitor");
        _dstCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 330 };
        _dstCombo.Items.AddRange(names);
        int diSel = FindByIdentity(_cfg.TargetKey, _cfg.TargetLabel, _cfg.TargetDisplay);
        if (diSel >= 0) _dstCombo.SelectedIndex = diSel;
        _dstCombo.SelectedIndexChanged += (_, _) => { if (!_loading) StoreSelection(_dstCombo, src: false); };
        _panel.Controls.Add(_dstCombo);
        var refresh = new Button { Text = "↻", Location = new Point(CX + 338, _y - 1), Width = 32, Height = 26 };
        refresh.Click += (_, _) => RefreshDisplays();
        _panel.Controls.Add(refresh);
        _y += ROW;

        // Note if a saved selection is currently not connected (it stays saved).
        if (siSel < 0 && HasIdentity(_cfg.SourceKey, _cfg.SourceLabel))
            Note($"⚠ Gespeicherte Quelle \"{LabelOf(_cfg.SourceKey, _cfg.SourceLabel)}\" aktuell nicht verbunden.");
        if (diSel < 0 && HasIdentity(_cfg.TargetKey, _cfg.TargetLabel))
            Note($"⚠ Gespeichertes Ziel \"{LabelOf(_cfg.TargetKey, _cfg.TargetLabel)}\" aktuell nicht verbunden.");

        ComboRow("Layout-Modus", new[] { "fit", "stretch", "top_strip", "crop_region" }, _cfg.LayoutMode, s => _cfg.LayoutMode = s);
        ComboRow("Ausgabe-Modus", new[] { "windowed", "borderless", "fullscreen_block", "exclusive" }, _cfg.ResolveOutputMode(), s => _cfg.OutputMode = s);
        ComboRow("Bit-Tiefe", new[] { "8", "10" }, _cfg.OutputBitDepth.ToString(), s => _cfg.OutputBitDepth = int.TryParse(s, out var v) ? v : 10);
        NumRow("Test-Fenster Breite", 320, 7680, _cfg.WindowWidth, 16, 0, v => _cfg.WindowWidth = (int)v);
        NumRow("Test-Fenster Höhe", 240, 4320, _cfg.WindowHeight, 16, 0, v => _cfg.WindowHeight = (int)v);
        Note("Struktur (Monitor/Modus/Bit-Tiefe/Fenster) wirkt erst nach Neustart.");
    }

    private void BuildImage()
    {
        Header("Bild / Tonemap");
        CheckRow("Tonemap aktiv (HDR→SDR)", _cfg.TonemapEnabled, v => _cfg.TonemapEnabled = v);
        ComboRow("Operator", new[] { "bt2390", "reinhard", "hable", "aces" }, _cfg.Operator, s => _cfg.Operator = s);
        FloatRow("Belichtung (Stops)", -3f, 3f, _cfg.Exposure, "0.00", v => _cfg.Exposure = v);
        FloatRow("Sättigung", 0f, 2f, _cfg.Saturation, "0.00", v => _cfg.Saturation = v);
        FloatRow("Kontrast", 0f, 2f, _cfg.Contrast, "0.00", v => _cfg.Contrast = v);
        FloatRow("Gamma", 0.3f, 3f, _cfg.Gamma, "0.00", v => _cfg.Gamma = v);
        FloatRow("Weißpunkt (nits)", 80f, 400f, _cfg.TargetPaperwhite, "0", v => _cfg.TargetPaperwhite = v);
        FloatRow("Quell-Spitze (nits)", 200f, 4000f, _cfg.SourcePeakNits, "0", v => _cfg.SourcePeakNits = v);
        NumRow("Vertikal-Offset (px)", -2160, 2160, _cfg.ContentOffsetY, 2, 0, v => _cfg.ContentOffsetY = (int)v);
        CheckRow("Mauszeiger anzeigen", _cfg.ShowCursor, v => _cfg.ShowCursor = v);
        CheckRow("VSync", _cfg.Vsync, v => _cfg.Vsync = v);
        CheckRow("Schlafmodus / Monitor-Abschaltung verhindern (während Spiegelung)",
                 _cfg.KeepAwake, v => _cfg.KeepAwake = v);
    }

    private void BuildCrop()
    {
        Header("Crop (nur Layout-Modus crop_region)");
        NumRow("Crop X (0..1)", 0m, 1m, (decimal)_cfg.CropX, 0.01m, 2, v => _cfg.CropX = (float)v);
        NumRow("Crop Y (0..1)", 0m, 1m, (decimal)_cfg.CropY, 0.01m, 2, v => _cfg.CropY = (float)v);
        NumRow("Crop Breite (0..1)", 0.01m, 1m, (decimal)_cfg.CropW, 0.01m, 2, v => _cfg.CropW = (float)v);
        NumRow("Crop Höhe (0..1)", 0.01m, 1m, (decimal)_cfg.CropH, 0.01m, 2, v => _cfg.CropH = (float)v);
    }

    private void BuildConnection()
    {
        Header("Verbindung / Autostart");
        Lbl("Agent-Bind");
        var bind = new TextBox { Location = new Point(CX, _y), Width = 250, Text = _app.AgentBind };
        bind.TextChanged += (_, _) => { if (!_loading) { _app.AgentBind = bind.Text.Trim(); SaveApp(); } };
        _panel.Controls.Add(bind);
        _y += ROW;
        Note("\"+\" = vom Netz erreichbar  ·  \"localhost\" = nur lokal");
        NumRow("Agent-Port", 1, 65535, _app.AgentPort, 1, 0, v => _app.AgentPort = (int)v, app: true);
        CheckRow("HTTP-Agent aktiv (Cockpit-Fernsteuerung)", _app.AgentEnabled, v => _app.AgentEnabled = v, app: true);
        CheckRow("Spiegelung beim App-Start automatisch beginnen", _app.AutostartMirror, v => _app.AutostartMirror = v, app: true);
        var al = new CheckBox { Text = "App beim Windows-Login automatisch starten", Location = new Point(LX, _y), AutoSize = true, Checked = IsLoginAutostart() };
        al.CheckedChanged += (_, _) => { if (!_loading) SetLoginAutostart(al.Checked); };
        _panel.Controls.Add(al);
        _y += 28;
        Note("Agent-/Bind-/Port-Änderungen wirken nach App-Neustart.");
    }

    // ── About / Update ─────────────────────────────────────────────────────
    private void BuildAbout()
    {
        Header("Über");
        Note($"{AppInfo.Name}  v{AppInfo.Version}");
        Note(AppInfo.Copyright);

        var repo = new LinkLabel { Text = AppInfo.RepoUrl.Replace("https://", ""), Location = new Point(LX, _y), AutoSize = true };
        repo.LinkClicked += (_, _) => OpenUrl(AppInfo.RepoUrl);
        _panel.Controls.Add(repo);
        _y += 26;
        Note("Updates prüfst du unten über „↻ Auf Updates prüfen\".");
    }

    /// <summary>Large button at the bottom: check GitHub for a newer version and offer to
    /// open the download page.</summary>
    private async void CheckForUpdates(Button btn)
    {
        var old = btn.Text;
        btn.Enabled = false; btn.Text = "prüfe…";
        var r = await UpdateCheck.CheckAsync();
        btn.Enabled = true; btn.Text = old;
        if (r is null)
        {
            MessageBox.Show(this, "Update-Prüfung fehlgeschlagen (offline?).", "RitschyMirror",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else if (r.UpdateAvailable)
        {
            if (MessageBox.Show(this,
                    $"Update {r.LatestVersion} ist verfügbar (du hast v{AppInfo.Version}).\n\nJetzt zur Download-Seite?",
                    "RitschyMirror — Update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenUrl(r.Url);
        }
        else
        {
            MessageBox.Show(this, $"Du hast die neueste Version (v{AppInfo.Version}).", "RitschyMirror",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    // ── Displays ──────────────────────────────────────────────────────────
    private string[] DisplayNames() =>
        _displays.Select(d => $"{d.Index}: {d.DisplayName}  {d.Resolution}{(d.Hdr ? " HDR" : "")}  [{d.Adapter}]").ToArray();

    private static bool HasIdentity(string key, string label) =>
        !string.IsNullOrWhiteSpace(key) || !string.IsNullOrWhiteSpace(label);

    private static string LabelOf(string key, string label) =>
        !string.IsNullOrWhiteSpace(label) ? label : key;

    /// <summary>Combo index for the saved selection: by key → unique label →
    /// (only without identity) index. -1 = identity set, but no matching display present.</summary>
    private int FindByIdentity(string key, string label, int index)
    {
        if (HasIdentity(key, label))
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                int i = _displays.FindIndex(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            if (!string.IsNullOrWhiteSpace(label))
            {
                var m = _displays.FindAll(d => string.Equals(d.Friendly, label, StringComparison.OrdinalIgnoreCase));
                if (m.Count == 1) return _displays.IndexOf(m[0]);
            }
            return -1;
        }
        return (index >= 0 && index < _displays.Count) ? index : -1;
    }

    /// <summary>Persist the selection: index (backward compat) AND stable identity (key+label).</summary>
    private void StoreSelection(ComboBox combo, bool src)
    {
        int i = combo.SelectedIndex;
        if (i < 0 || i >= _displays.Count) return;
        var d = _displays[i];
        if (src) { _cfg.SourceDisplay = i; _cfg.SourceKey = d.Key; _cfg.SourceLabel = d.Friendly; }
        else { _cfg.TargetDisplay = i; _cfg.TargetKey = d.Key; _cfg.TargetLabel = d.Friendly; }
        SaveCfg();
    }

    private void RefreshDisplays()
    {
        _displays = MirrorEngine.EnumerateDisplays();
        var names = DisplayNames();
        _loading = true;
        _srcCombo.Items.Clear(); _srcCombo.Items.AddRange(names);
        _dstCombo.Items.Clear(); _dstCombo.Items.AddRange(names);
        int s = FindByIdentity(_cfg.SourceKey, _cfg.SourceLabel, _cfg.SourceDisplay);
        int d = FindByIdentity(_cfg.TargetKey, _cfg.TargetLabel, _cfg.TargetDisplay);
        _srcCombo.SelectedIndex = s >= 0 ? s : -1;
        _dstCombo.SelectedIndex = d >= 0 ? d : -1;
        _loading = false;
    }

    // ── Window selection (window capture mode) ───────────────────────────
    private bool HasWindowIdentity() =>
        !string.IsNullOrWhiteSpace(_cfg.WindowExe) || !string.IsNullOrWhiteSpace(_cfg.WindowTitle);

    private string WindowLabel() =>
        string.IsNullOrWhiteSpace(_cfg.WindowExe) ? _cfg.WindowTitle : $"{_cfg.WindowTitle} ({_cfg.WindowExe})";

    /// <summary>Combo index for the saved window identity: same exe + exact title →
    /// first same exe → exact title. -1 = not found (window not open).</summary>
    private int FindWindowIndex()
    {
        if (!HasWindowIdentity()) return -1;
        int exact = _windows.FindIndex(w =>
            string.Equals(w.Exe, _cfg.WindowExe, OIC) && string.Equals(w.Title, _cfg.WindowTitle, OIC));
        if (exact >= 0) return exact;
        int byExe = _windows.FindIndex(w => string.Equals(w.Exe, _cfg.WindowExe, OIC));
        if (byExe >= 0) return byExe;
        return _windows.FindIndex(w => string.Equals(w.Title, _cfg.WindowTitle, OIC));
    }

    /// <summary>Persist the window selection (exe + title) and switch the source mode to "window"
    /// along with it — choosing a window means: mirror the window.</summary>
    private void StoreWindow()
    {
        int i = _winCombo.SelectedIndex;
        if (i < 0 || i >= _windows.Count) return;
        var w = _windows[i];
        _cfg.WindowExe = w.Exe;
        _cfg.WindowTitle = w.Title;
        _cfg.CaptureMode = "window";
        _loading = true;
        _modeCombo.SelectedItem = "window";
        _loading = false;
        SaveCfg();
    }

    private void RefreshWindows()
    {
        _windows = WindowEnum.List();
        _loading = true;
        _winCombo.Items.Clear();
        _winCombo.Items.AddRange(_windows.Select(w => w.DisplayName).ToArray());
        int i = FindWindowIndex();
        _winCombo.SelectedIndex = i >= 0 ? i : -1;
        _loading = false;
    }

    // ── Login autostart (HKCU\...\Run) ────────────────────────────────────
    private static bool IsLoginAutostart()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunValue) != null; }
        catch { return false; }
    }

    private void SetLoginAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k!.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
            else k!.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex) { _engine.Log("Failed to set login autostart: " + ex.Message); }
    }
}

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
    private const int NoteWidth = 570;            // notes wrap at this width instead of widening the panel
    private const int StatusRefreshMs = 1000;
    private const int StatusLines = 3;            // audio status: room for long device names

    private readonly MirrorEngine _engine;
    private readonly string _appSettingsPath;
    private readonly AppSettings _app;
    private readonly Action _onLanguageChanged;
    private MirrorConfig _cfg;
    private bool _loading;

    private readonly Panel _panel;
    private readonly float _scale;   // device pixels per layout pixel (1.5 at 150 % display scaling)
    private int _y = 12;
    private ComboBox _srcCombo = null!, _dstCombo = null!;
    private ComboBox _modeCombo = null!, _winCombo = null!;
    private List<DisplayInfo> _displays = new();
    private List<WindowInfo> _windows = new();
    private Label _audioStatus = null!;
    private readonly System.Windows.Forms.Timer _statusTimer;

    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    /// <param name="onLanguageChanged">Called after the user picked another UI language
    /// (the tray reloads the packs and rebuilds this window).</param>
    public SettingsForm(MirrorEngine engine, string appSettingsPath, AppSettings app, Action onLanguageChanged)
    {
        _engine = engine; _appSettingsPath = appSettingsPath; _app = app;
        _onLanguageChanged = onLanguageChanged;
        _cfg = MirrorConfig.Load(engine.ConfigPath);

        // All coordinates below are 96-dpi (100 %) layout pixels. WinForms scales locations and sizes
        // to the monitor's DPI on ResumeLayout (and again on WM_DPICHANGED); without this only the
        // fonts grew at 125/150 %, so labels overlapped the inputs and rows overlapped each other.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _scale = DeviceDpi / 96f;

        Text = L.T("settings.title", AppInfo.Version);
        Icon = AppIcon.Load(small: false);
        ClientSize = new Size(620, 780);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 420);

        _panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = SystemColors.ControlLight };
        Controls.Add(_panel);
        Controls.Add(bottom);

        _loading = true;
        BuildGeneral();
        BuildSourceTarget();
        BuildImage();
        BuildFrameRate();
        BuildAudio();
        BuildCrop();
        BuildConnection();
        BuildAbout();
        _loading = false;

        var start = new Button { Text = L.T("settings.btn.start_stop"), Location = new Point(12, 9), Width = 120, Height = 30 };
        start.Click += (_, _) => { if (_engine.IsRunning) _engine.Stop(); else StartWithCheck(); };
        var restart = new Button { Text = L.T("settings.btn.restart"), Location = new Point(140, 9), Width = 160, Height = 30 };
        restart.Click += (_, _) => { _engine.Stop(); StartWithCheck(); };
        var close = new Button { Text = L.T("settings.btn.close"), Location = new Point(308, 9), Width = 110, Height = 30 };
        close.Click += (_, _) => Close();
        var update = new Button { Text = L.T("settings.btn.check_updates"), Location = new Point(426, 9), Width = 172, Height = 30 };
        update.Click += (_, _) => CheckForUpdates(update);
        bottom.Controls.AddRange(new Control[] { start, restart, close, update });

        ResumeLayout(false);
        PerformLayout();
        // On small/short screens never taller than the working area — otherwise the bottom
        // button bar (Close / Check for updates) slides out of the visible window. After scaling,
        // so the check uses the real (device-pixel) height.
        var wa = Screen.PrimaryScreen?.WorkingArea;
        if (wa is { } a && Height > a.Height - 16) Height = a.Height - 16;

        // Audio-mirror status follows device switches live → poll the engine while the window is open.
        _statusTimer = new System.Windows.Forms.Timer { Interval = StatusRefreshMs };
        _statusTimer.Tick += (_, _) => RefreshAudioStatus();
        _statusTimer.Start();
        FormClosed += (_, _) => _statusTimer.Dispose();
    }

    /// <summary>Start with a preflight check — if the source/target monitor is missing, show a clear
    /// message instead of silently mirroring the wrong screen.</summary>
    private void StartWithCheck()
    {
        var err = _engine.Preflight();
        if (err != null)
        {
            MessageBox.Show(this, err.Text + ".", "RitschyMirror", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _engine.Start();
    }

    // ── Persistence ────────────────────────────────────────────────────────
    private void SaveCfg() { if (_loading) return; try { _cfg.Save(_engine.ConfigPath); } catch { } }
    private void SaveApp() { if (_loading) return; _app.Save(_appSettingsPath); }

    // ── Layout helpers (manual, y cursor) ────────────────────────────────
    private int ToLayout(int devicePx) => (int)Math.Ceiling(devicePx / _scale);

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
        // Wrap long (translated) notes instead of letting them widen the scroll area.
        var lbl = new Label { Text = text, Location = new Point(LX, _y), AutoSize = true, ForeColor = Color.DimGray,
                              MaximumSize = new Size(NoteWidth, 0) };
        _panel.Controls.Add(lbl);
        // Measured in device pixels (the font is already DPI-sized) → back to layout pixels.
        _y += ToLayout(lbl.GetPreferredSize(new Size((int)(NoteWidth * _scale), 0)).Height) + 7;
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

    /// <summary>Drop-down whose entries show translated text but store a fixed value.</summary>
    private ComboBox ChoiceRow(string label, (string Value, string Text)[] items, string current,
                               Action<string> set, bool app = false)
    {
        Lbl(label);
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 250 };
        c.Items.AddRange(items.Select(i => (object)i.Text).ToArray());
        int sel = Array.FindIndex(items, i => string.Equals(i.Value, current, OIC));
        if (items.Length > 0) c.SelectedIndex = sel >= 0 ? sel : 0;
        c.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || c.SelectedIndex < 0) return;
            set(items[c.SelectedIndex].Value);
            if (app) SaveApp(); else SaveCfg();
        };
        _panel.Controls.Add(c);
        _y += ROW;
        return c;
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
    private void BuildGeneral()
    {
        Header(L.T("section.general"));
        var langs = new List<(string Value, string Text)> { (L.Auto, L.T("settings.language.auto")) };
        langs.AddRange(L.Available().Select(code => (code, $"{L.DisplayName(code)} ({code})")));
        ChoiceRow(L.T("settings.language"), langs.ToArray(), _app.Language, code =>
        {
            _app.Language = code;
            // Rebuilding this window from inside its own combo event is unsafe → defer.
            BeginInvoke(_onLanguageChanged);
        }, app: true);
        Note(L.T("settings.language.note"));
    }

    private void BuildSourceTarget()
    {
        Header(L.T("section.source_target"));
        _displays = MirrorEngine.EnumerateDisplays();
        var names = DisplayNames();

        // Source mode: whole monitor (Desktop Duplication) OR a single window (Windows.Graphics.Capture).
        _modeCombo = ComboRow(L.T("settings.capture_mode"), new[] { "monitor", "window" }, _cfg.ResolveCaptureMode(),
                              s => _cfg.CaptureMode = s);
        Note(L.T("settings.capture_mode.note1"));
        Note(L.T("settings.capture_mode.note2"));

        Lbl(L.T("settings.source_monitor"));
        _srcCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 330 };
        _srcCombo.Items.AddRange(names);
        int siSel = FindByIdentity(_cfg.SourceKey, _cfg.SourceLabel, _cfg.SourceDisplay);
        if (siSel >= 0) _srcCombo.SelectedIndex = siSel;
        _srcCombo.SelectedIndexChanged += (_, _) => { if (!_loading) StoreSelection(_srcCombo, src: true); };
        _panel.Controls.Add(_srcCombo);
        _y += ROW;

        // Window source ("window" mode) — identity-stable via exe + title, like the monitor selection.
        _windows = WindowEnum.List();
        Lbl(L.T("settings.window"));
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
            Note(L.T("settings.window_missing", WindowLabel()));

        Lbl(L.T("settings.target_monitor"));
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
            Note(L.T("settings.source_missing", LabelOf(_cfg.SourceKey, _cfg.SourceLabel)));
        if (diSel < 0 && HasIdentity(_cfg.TargetKey, _cfg.TargetLabel))
            Note(L.T("settings.target_missing", LabelOf(_cfg.TargetKey, _cfg.TargetLabel)));

        ComboRow(L.T("settings.layout_mode"), new[] { "fit", "stretch", "top_strip", "crop_region" }, _cfg.LayoutMode, s => _cfg.LayoutMode = s);
        ComboRow(L.T("settings.output_mode"), new[] { "windowed", "borderless", "fullscreen_block", "exclusive" }, _cfg.ResolveOutputMode(), s => _cfg.OutputMode = s);
        ComboRow(L.T("settings.bit_depth"), new[] { "8", "10" }, _cfg.OutputBitDepth.ToString(), s => _cfg.OutputBitDepth = int.TryParse(s, out var v) ? v : 10);
        NumRow(L.T("settings.window_width"), 320, 7680, _cfg.WindowWidth, 16, 0, v => _cfg.WindowWidth = (int)v);
        NumRow(L.T("settings.window_height"), 240, 4320, _cfg.WindowHeight, 16, 0, v => _cfg.WindowHeight = (int)v);
        Note(L.T("settings.struct_note"));
    }

    private void BuildImage()
    {
        Header(L.T("section.image"));
        CheckRow(L.T("settings.tonemap"), _cfg.TonemapEnabled, v => _cfg.TonemapEnabled = v);
        ComboRow(L.T("settings.operator"), new[] { "bt2390", "reinhard", "hable", "aces" }, _cfg.Operator, s => _cfg.Operator = s);
        FloatRow(L.T("settings.exposure"), -3f, 3f, _cfg.Exposure, "0.00", v => _cfg.Exposure = v);
        FloatRow(L.T("settings.saturation"), 0f, 2f, _cfg.Saturation, "0.00", v => _cfg.Saturation = v);
        FloatRow(L.T("settings.contrast"), 0f, 2f, _cfg.Contrast, "0.00", v => _cfg.Contrast = v);
        FloatRow(L.T("settings.gamma"), 0.3f, 3f, _cfg.Gamma, "0.00", v => _cfg.Gamma = v);
        FloatRow(L.T("settings.paperwhite"), 80f, 400f, _cfg.TargetPaperwhite, "0", v => _cfg.TargetPaperwhite = v);
        FloatRow(L.T("settings.source_peak"), 200f, 4000f, _cfg.SourcePeakNits, "0", v => _cfg.SourcePeakNits = v);
        NumRow(L.T("settings.offset_y"), -2160, 2160, _cfg.ContentOffsetY, 2, 0, v => _cfg.ContentOffsetY = (int)v);
        CheckRow(L.T("settings.show_cursor"), _cfg.ShowCursor, v => _cfg.ShowCursor = v);
        CheckRow(L.T("settings.keep_awake"), _cfg.KeepAwake, v => _cfg.KeepAwake = v);
    }

    private void BuildFrameRate()
    {
        Header(L.T("section.frame"));
        NumericUpDown? fps = null;
        var vsync = CheckRow(L.T("settings.vsync"), _cfg.Vsync, v =>
        {
            _cfg.Vsync = v;
            if (fps != null) fps.Enabled = !v; // target_fps only applies with VSync off
        });
        fps = NumRow(L.T("settings.target_fps"), 0, MirrorConfig.MaxTargetFps, _cfg.TargetFps, 1, 0,
                     v => _cfg.TargetFps = (int)v);
        fps.Enabled = !vsync.Checked;
        Note(L.T("settings.target_fps.note"));
        CheckRow(L.T("settings.fps_overlay"), _cfg.FpsOverlay, v => _cfg.FpsOverlay = v);
        var positions = MirrorConfig.OverlayPositions.Select(pos => (pos, L.T("pos." + pos))).ToArray();
        ChoiceRow(L.T("settings.fps_overlay_position"), positions, _cfg.FpsOverlayPosition,
                  v => _cfg.FpsOverlayPosition = v);
        Note(L.T("settings.fps_overlay.note"));
    }

    private void BuildAudio()
    {
        Header(L.T("section.audio"));
        CheckRow(L.T("settings.audio_mirror"), _cfg.AudioMirror, v => { _cfg.AudioMirror = v; RefreshAudioStatus(); });
        Note(L.T("settings.audio_mirror.note"));
        // Device names make this line long and it changes live → reserve room for a few wrapped
        // lines, measured in the actual font (a fixed pixel height clips at high display scaling).
        int statusHeight = ToLayout(TextRenderer.MeasureText("Ag", Font).Height) * StatusLines;
        _audioStatus = new Label { Location = new Point(LX, _y), AutoSize = false, AutoEllipsis = true,
                                   Size = new Size(NoteWidth, statusHeight) };
        _panel.Controls.Add(_audioStatus);
        _y += statusHeight + 6;
        RefreshAudioStatus();
    }

    private void RefreshAudioStatus()
    {
        if (_audioStatus is null || _audioStatus.IsDisposed) return;
        _audioStatus.Text = L.T("settings.audio_status", _engine.AudioStatusText(_cfg.AudioMirror));
        _audioStatus.ForeColor = _cfg.AudioMirror && _engine.IsRunning && _engine.Audio.State == AudioMirrorState.Unavailable
            ? Color.Firebrick : SystemColors.ControlText;
    }

    private void BuildCrop()
    {
        Header(L.T("section.crop"));
        NumRow(L.T("settings.crop_x"), 0m, 1m, (decimal)_cfg.CropX, 0.01m, 2, v => _cfg.CropX = (float)v);
        NumRow(L.T("settings.crop_y"), 0m, 1m, (decimal)_cfg.CropY, 0.01m, 2, v => _cfg.CropY = (float)v);
        NumRow(L.T("settings.crop_w"), 0.01m, 1m, (decimal)_cfg.CropW, 0.01m, 2, v => _cfg.CropW = (float)v);
        NumRow(L.T("settings.crop_h"), 0.01m, 1m, (decimal)_cfg.CropH, 0.01m, 2, v => _cfg.CropH = (float)v);
    }

    private void BuildConnection()
    {
        Header(L.T("section.connection"));
        Lbl(L.T("settings.agent_bind"));
        var bind = new TextBox { Location = new Point(CX, _y), Width = 250, Text = _app.AgentBind };
        bind.TextChanged += (_, _) => { if (!_loading) { _app.AgentBind = bind.Text.Trim(); SaveApp(); } };
        _panel.Controls.Add(bind);
        _y += ROW;
        Note(L.T("settings.agent_bind.note"));
        NumRow(L.T("settings.agent_port"), 1, 65535, _app.AgentPort, 1, 0, v => _app.AgentPort = (int)v, app: true);
        CheckRow(L.T("settings.agent_enabled"), _app.AgentEnabled, v => _app.AgentEnabled = v, app: true);
        CheckRow(L.T("settings.autostart_mirror"), _app.AutostartMirror, v => _app.AutostartMirror = v, app: true);
        var al = new CheckBox { Text = L.T("settings.login_autostart"), Location = new Point(LX, _y), AutoSize = true, Checked = IsLoginAutostart() };
        al.CheckedChanged += (_, _) => { if (!_loading) SetLoginAutostart(al.Checked); };
        _panel.Controls.Add(al);
        _y += 28;
        Note(L.T("settings.agent_note"));
    }

    // ── About / Update ─────────────────────────────────────────────────────
    private void BuildAbout()
    {
        Header(L.T("section.about"));
        Note($"{AppInfo.Name}  v{AppInfo.Version}");
        Note(L.T("about.copyright"));

        var repo = new LinkLabel { Text = AppInfo.RepoUrl.Replace("https://", ""), Location = new Point(LX, _y), AutoSize = true };
        repo.LinkClicked += (_, _) => OpenUrl(AppInfo.RepoUrl);
        _panel.Controls.Add(repo);
        _y += 26;
        Note(L.T("about.update_hint"));
    }

    /// <summary>Large button at the bottom: check GitHub for a newer version and offer to
    /// open the download page.</summary>
    private async void CheckForUpdates(Button btn)
    {
        var old = btn.Text;
        btn.Enabled = false; btn.Text = L.T("settings.btn.checking");
        var r = await UpdateCheck.CheckAsync();
        if (IsDisposed) return;   // window closed meanwhile (e.g. rebuilt after a language switch)
        btn.Enabled = true; btn.Text = old;
        if (r is null)
        {
            MessageBox.Show(this, L.T("update.failed"), "RitschyMirror",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else if (r.UpdateAvailable)
        {
            if (MessageBox.Show(this,
                    L.T("update.available", r.LatestVersion, AppInfo.Version),
                    L.T("update.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenUrl(r.Url);
        }
        else
        {
            MessageBox.Show(this, L.T("update.latest", AppInfo.Version), "RitschyMirror",
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

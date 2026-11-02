using Microsoft.Win32;
using System.Drawing;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// Local settings GUI — the same parameters that are otherwise set remotely via the cockpit.
/// Writes live to mirror_config.json (the engine reloads LIVE keys immediately);
/// structural changes (display/mode/bit depth) require a render restart.
/// App-level settings (agent/autostart/language/UI state) go to app_settings.json.
///
/// Structure: one <see cref="CollapsibleSection"/> per main feature (header = on/off + live
/// summary, body = its sub-settings), stacked in a scrolling FlowLayoutPanel; a resizable,
/// hideable <see cref="LogView"/> at the bottom. Inside a section body the layout is deliberately
/// MANUAL (Location/Size, y cursor) — nested AutoSize containers collapsed to height 0.
/// </summary>
public sealed partial class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RitschyMirror";
    private const int LX = 16, CX = 230, VX = 500, ROW = 32;
    private const int SectionWidth = 640;
    private const int NoteWidth = 600;            // notes wrap at this width instead of widening the panel
    private const int StatusRefreshMs = 1000;
    private const int StatusLines = 3;            // audio status: room for long device names

    private readonly MirrorEngine _engine;
    private readonly string _appSettingsPath;
    private readonly AppSettings _app;
    private readonly Action _rebuild;   // tray reloads the language packs and reopens this window
    private MirrorConfig _cfg;
    private bool _loading;

    private readonly FlowLayoutPanel _flow;
    private readonly LogView _logView;
    private readonly Splitter _logSplitter;
    private readonly Button _logButton;
    private readonly float _scale;   // device pixels per layout pixel (1.5 at 150 % display scaling)
    private Panel _body = null!;     // section body currently being built
    private int _y;                  // y cursor inside _body
    private readonly List<CollapsibleSection> _sections = new();
    private readonly Dictionary<string, CollapsibleSection> _byId = new();

    private ComboBox _srcCombo = null!, _dstCombo = null!;
    private ComboBox _modeCombo = null!, _winCombo = null!;
    private List<DisplayInfo> _displays = new();
    private List<int> _dstIndices = new();   // _displays index per target-combo entry (source filtered out)
    private List<WindowInfo> _windows = new();
    private Label _audioStatus = null!;
    private readonly System.Windows.Forms.Timer _statusTimer;

    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    /// <param name="rebuild">Reopens this window from scratch — after a language switch or to
    /// re-list audio devices (the tray reloads the packs and recreates the form).</param>
    public SettingsForm(MirrorEngine engine, string appSettingsPath, AppSettings app, Action rebuild)
    {
        _engine = engine; _appSettingsPath = appSettingsPath; _app = app;
        _rebuild = rebuild;
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
        ClientSize = new Size(SectionWidth + 40, 820);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(SectionWidth + 56, 420);

        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(8),
        };
        _logView = new LogView(engine) { Visible = _app.UiShowLog };
        _logSplitter = new Splitter { Dock = DockStyle.Bottom, Height = 5, Visible = _app.UiShowLog };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = SystemColors.ControlLight };
        // Docking runs from the last added control: button bar at the very bottom, log above it,
        // splitter above the log, sections fill the rest.
        Controls.Add(_flow);
        Controls.Add(_logSplitter);
        Controls.Add(_logView);
        Controls.Add(bottom);

        _loading = true;
        BuildMirroring();
        BuildTonemap();
        BuildAdjustments();
        BuildFrameRate();
        BuildFpsOverlay();
        BuildAudio();
        BuildCrop();
        BuildConnection();
        BuildGeneral();
        BuildAbout();
        _loading = false;

        var start = new Button { Text = L.T("settings.btn.start_stop"), Location = new Point(12, 9), Width = 110, Height = 30 };
        start.Click += (_, _) => { if (_engine.IsRunning) _engine.Stop(); else StartWithCheck(); RefreshSummaries(); };
        var restart = new Button { Text = L.T("settings.btn.restart"), Location = new Point(130, 9), Width = 150, Height = 30 };
        restart.Click += (_, _) => { _engine.Stop(); StartWithCheck(); RefreshSummaries(); };
        var close = new Button { Text = L.T("settings.btn.close"), Location = new Point(288, 9), Width = 90, Height = 30 };
        close.Click += (_, _) => Close();
        var update = new Button { Text = L.T("settings.btn.check_updates"), Location = new Point(386, 9), Width = 170, Height = 30 };
        update.Click += (_, _) => CheckForUpdates(update);
        _logButton = new Button { Location = new Point(564, 9), Width = 100, Height = 30 };
        _logButton.Click += (_, _) => SetLogVisible(!_logView.Visible);
        bottom.Controls.AddRange(new Control[] { start, restart, close, update, _logButton });
        UpdateLogButton();

        ResumeLayout(false);
        PerformLayout();
        // On small/short screens never taller than the working area — otherwise the bottom
        // button bar (Close / Check for updates) slides out of the visible window. After scaling,
        // so the check uses the real (device-pixel) height.
        var wa = Screen.PrimaryScreen?.WorkingArea;
        if (wa is { } a && Height > a.Height - 16) Height = a.Height - 16;

        // Header summaries (running state, audio status, live FPS) follow the engine → poll while open.
        RefreshSummaries();
        _statusTimer = new System.Windows.Forms.Timer { Interval = StatusRefreshMs };
        _statusTimer.Tick += (_, _) => RefreshSummaries();
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
    private void SaveCfg()
    {
        if (_loading) return;
        try { _cfg.Save(_engine.ConfigPath); } catch { }
        RefreshSummaries();
    }

    private void SaveApp()
    {
        if (_loading) return;
        _app.Save(_appSettingsPath);
        RefreshSummaries();
    }

    // ── Log pane ───────────────────────────────────────────────────────────
    private void SetLogVisible(bool visible)
    {
        _logView.Visible = visible;
        _logSplitter.Visible = visible;
        _app.UiShowLog = visible;
        SaveApp();
        UpdateLogButton();
    }

    private void UpdateLogButton() =>
        _logButton.Text = L.T(_logView.Visible ? "settings.btn.log_hide" : "settings.btn.log_show");

    // ── Sections ───────────────────────────────────────────────────────────
    /// <summary>Start a feature section; the row helpers below add to its body until
    /// <see cref="EndSection"/>. A header toggle switches the feature itself.</summary>
    private CollapsibleSection BeginSection(string id, string title, bool? toggled = null, Action<bool>? onToggle = null)
    {
        bool expanded = _app.UiExpanded.Contains(id, StringComparer.OrdinalIgnoreCase);
        var s = new CollapsibleSection(id, title, SectionWidth, expanded, toggled.HasValue, toggled ?? false);
        if (s.Toggle is { } t && onToggle != null)
            t.CheckedChanged += (_, _) => { if (!_loading) { onToggle(t.Checked); SaveCfg(); } };
        s.ExpandedChanged += (_, _) => RememberExpanded(s);
        _flow.Controls.Add(s);
        _sections.Add(s);
        _byId[id] = s;
        _body = s.Body;
        _y = 8;
        return s;
    }

    private void EndSection(CollapsibleSection s) => s.SetBodyHeight(_y + 6);

    private void RememberExpanded(CollapsibleSection s)
    {
        _app.UiExpanded.RemoveAll(id => string.Equals(id, s.Id, OIC));
        if (s.Expanded) _app.UiExpanded.Add(s.Id);
        SaveApp();
    }

    /// <summary>Live one-line status per section header.</summary>
    private void RefreshSummaries()
    {
        if (IsDisposed || _byId.Count == 0) return;
        bool running = _engine.IsRunning;

        string src = _cfg.ResolveCaptureMode() == "window"
            ? (string.IsNullOrWhiteSpace(_cfg.WindowTitle) ? "window" : _cfg.WindowTitle)
            : LabelOf(_cfg.SourceKey, _cfg.SourceLabel);
        string dst = LabelOf(_cfg.TargetKey, _cfg.TargetLabel);
        string pair = src.Length > 0 || dst.Length > 0 ? $"  ·  {src} → {dst}" : "";
        SetSummary("mirroring", L.T(running ? "summary.running" : "summary.stopped") + pair);

        SetSummary("tonemap", _cfg.TonemapEnabled ? _cfg.Operator : "");

        string pacing = _cfg.Vsync ? L.T("summary.vsync")
            : _cfg.EffectiveTargetFps > 0 ? L.T("summary.fps_limit", _cfg.EffectiveTargetFps)
            : L.T("summary.unlimited");
        string rates = running ? $"  ·  {L.T("summary.rates", _engine.CaptureFps.ToString("0.0"), _engine.OutputFps.ToString("0.0"))}" : "";
        SetSummary("frame", pacing + rates);

        SetSummary("fps_overlay", _cfg.FpsOverlay ? L.T("pos." + _cfg.FpsOverlayPosition) : "");

        string audio = _engine.AudioStatusText(_cfg.AudioMirror);
        SetSummary("audio", _cfg.AudioMirror ? audio : "");
        if (_audioStatus is { IsDisposed: false })
        {
            _audioStatus.Text = L.T("settings.audio_status", audio);
            _audioStatus.ForeColor = _cfg.AudioMirror && running && _engine.Audio.State == AudioMirrorState.Unavailable
                ? Color.Firebrick : SystemColors.ControlText;
        }

        SetSummary("crop", string.Equals(_cfg.LayoutMode, "crop_region", OIC)
            ? $"{_cfg.CropX:0.00}, {_cfg.CropY:0.00}  {_cfg.CropW:0.00} × {_cfg.CropH:0.00}"
            : L.T("summary.crop_inactive"));

        SetSummary("connection", _app.AgentEnabled ? $"{_app.AgentBind}:{_app.AgentPort}" : L.T("tray.agent_off"));
        SetSummary("general", L.DisplayName(L.Current));
        SetSummary("about", $"v{AppInfo.Version}");
    }

    private void SetSummary(string id, string text)
    {
        if (_byId.TryGetValue(id, out var s)) s.SetSummary(text);
    }

    // ── Row helpers (manual, y cursor inside the current section body) ────
    private int ToLayout(int devicePx) => (int)Math.Ceiling(devicePx / _scale);

    private void Lbl(string text) =>
        _body.Controls.Add(new Label { Text = text, Location = new Point(LX, _y + 4), AutoSize = true });

    private void Note(string text)
    {
        // Wrap long (translated) notes instead of letting them widen the scroll area.
        var lbl = new Label { Text = text, Location = new Point(LX, _y), AutoSize = true, ForeColor = Color.DimGray,
                              MaximumSize = new Size(NoteWidth, 0) };
        _body.Controls.Add(lbl);
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
        _body.Controls.Add(c);
        _y += ROW;
        return c;
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
        _body.Controls.Add(c);
        _y += ROW;
        return c;
    }

    private void FloatRow(string label, float min, float max, float val, string fmt, Action<float> set)
    {
        Lbl(label);
        // Fixed height: an auto-sized TrackBar mis-renders (thumb only) after DPI scaling.
        var tb = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, AutoSize = false,
                                Location = new Point(CX, _y - 2), Width = 260, Height = 30 };
        var vl = new Label { Location = new Point(VX, _y + 4), AutoSize = true, Text = val.ToString(fmt) };
        int ToTick(float v) => (int)Math.Round((v - min) / (max - min) * 1000);
        float ToVal(int t) => min + (max - min) * t / 1000f;
        tb.Value = Math.Clamp(ToTick(val), 0, 1000);
        tb.Scroll += (_, _) => { float v = ToVal(tb.Value); vl.Text = v.ToString(fmt); if (!_loading) { set(v); SaveCfg(); } };
        _body.Controls.Add(tb); _body.Controls.Add(vl);
        _y += 38;
    }

    private NumericUpDown NumRow(string label, decimal min, decimal max, decimal val, decimal step, int dec, Action<decimal> set, bool app = false)
    {
        Lbl(label);
        var n = new NumericUpDown { Location = new Point(CX, _y), Width = 110, Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max), Increment = step, DecimalPlaces = dec };
        n.ValueChanged += (_, _) => { if (!_loading) { set(n.Value); if (app) SaveApp(); else SaveCfg(); } };
        _body.Controls.Add(n);
        _y += ROW;
        return n;
    }

    private CheckBox CheckRow(string label, bool val, Action<bool> set, bool app = false)
    {
        var c = new CheckBox { Text = label, Location = new Point(LX, _y), AutoSize = true, Checked = val };
        c.CheckedChanged += (_, _) => { if (!_loading) { set(c.Checked); if (app) SaveApp(); else SaveCfg(); } };
        _body.Controls.Add(c);
        _y += 28;
        return c;
    }

    // ── Feature sections ──────────────────────────────────────────────────
    private void BuildMirroring()
    {
        var s = BeginSection("mirroring", L.T("section.mirroring"));
        _displays = MirrorEngine.EnumerateDisplays();
        var names = DisplayNames();

        // Source mode: whole monitor (Desktop Duplication) OR a single window (Windows.Graphics.Capture).
        _modeCombo = ComboRow(L.T("settings.capture_mode"), new[] { "monitor", "window" }, _cfg.ResolveCaptureMode(),
                              v => { _cfg.CaptureMode = v; RebuildTargetList(); });
        Note(L.T("settings.capture_mode.note1"));
        Note(L.T("settings.capture_mode.note2"));

        Lbl(L.T("settings.source_monitor"));
        _srcCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 330 };
        _srcCombo.Items.AddRange(names);
        int siSel = FindByIdentity(_cfg.SourceKey, _cfg.SourceLabel, _cfg.SourceDisplay);
        if (siSel >= 0) _srcCombo.SelectedIndex = siSel;
        _srcCombo.SelectedIndexChanged += (_, _) => { if (!_loading) StoreSelection(_srcCombo, src: true); };
        _body.Controls.Add(_srcCombo);
        _y += ROW;

        // Window source ("window" mode) — identity-stable via exe + title, like the monitor selection.
        _windows = WindowEnum.List();
        Lbl(L.T("settings.window"));
        _winCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 330 };
        _winCombo.Items.AddRange(_windows.Select(w => w.DisplayName).ToArray());
        int wiSel = FindWindowIndex();
        if (wiSel >= 0) _winCombo.SelectedIndex = wiSel;
        _winCombo.SelectedIndexChanged += (_, _) => { if (!_loading) StoreWindow(); };
        _body.Controls.Add(_winCombo);
        var winRefresh = new Button { Text = "↻", Location = new Point(CX + 338, _y - 1), Width = 32, Height = 26 };
        winRefresh.Click += (_, _) => RefreshWindows();
        _body.Controls.Add(winRefresh);
        _y += ROW;
        if (wiSel < 0 && HasWindowIdentity())
            Note(L.T("settings.window_missing", WindowLabel()));

        Lbl(L.T("settings.target_monitor"));
        // Owner-drawn so the primary monitor can be shown dimmed (listed, but not selectable).
        _dstCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 330,
                                   DrawMode = DrawMode.OwnerDrawFixed };
        _dstCombo.DrawItem += DrawTargetItem;
        int diSel = FindByIdentity(_cfg.TargetKey, _cfg.TargetLabel, _cfg.TargetDisplay);
        RebuildTargetList();
        _dstCombo.SelectedIndexChanged += (_, _) => { if (!_loading) StoreTarget(); };
        _dstCombo.DropDownClosed += (_, _) => RevertPrimaryPick();
        _body.Controls.Add(_dstCombo);
        var refresh = new Button { Text = "↻", Location = new Point(CX + 338, _y - 1), Width = 32, Height = 26 };
        refresh.Click += (_, _) => RefreshDisplays();
        _body.Controls.Add(refresh);
        _y += ROW;

        // Note if a saved selection is currently not connected (it stays saved).
        if (siSel < 0 && HasIdentity(_cfg.SourceKey, _cfg.SourceLabel))
            Note(L.T("settings.source_missing", LabelOf(_cfg.SourceKey, _cfg.SourceLabel)));
        if (diSel < 0 && HasIdentity(_cfg.TargetKey, _cfg.TargetLabel))
            Note(L.T("settings.target_missing", LabelOf(_cfg.TargetKey, _cfg.TargetLabel)));

        ComboRow(L.T("settings.layout_mode"), new[] { "fit", "stretch", "top_strip", "crop_region" }, _cfg.LayoutMode, v => _cfg.LayoutMode = v);
        NumRow(L.T("settings.offset_y"), -2160, 2160, _cfg.ContentOffsetY, 2, 0, v => _cfg.ContentOffsetY = (int)v);
        ComboRow(L.T("settings.output_mode"), new[] { "windowed", "borderless", "fullscreen_block", "exclusive" }, _cfg.ResolveOutputMode(), v => _cfg.OutputMode = v);
        ComboRow(L.T("settings.bit_depth"), new[] { "8", "10" }, _cfg.OutputBitDepth.ToString(), v => _cfg.OutputBitDepth = int.TryParse(v, out var b) ? b : 10);
        NumRow(L.T("settings.window_width"), 320, 7680, _cfg.WindowWidth, 16, 0, v => _cfg.WindowWidth = (int)v);
        NumRow(L.T("settings.window_height"), 240, 4320, _cfg.WindowHeight, 16, 0, v => _cfg.WindowHeight = (int)v);
        CheckRow(L.T("settings.show_cursor"), _cfg.ShowCursor, v => _cfg.ShowCursor = v);
        Note(L.T("settings.struct_note"));
        EndSection(s);
    }

    private void BuildTonemap()
    {
        var s = BeginSection("tonemap", L.T("section.tonemap"), _cfg.TonemapEnabled, v => _cfg.TonemapEnabled = v);
        ComboRow(L.T("settings.operator"), new[] { "bt2390", "reinhard", "hable", "aces" }, _cfg.Operator, v => _cfg.Operator = v);
        FloatRow(L.T("settings.paperwhite"), 80f, 400f, _cfg.TargetPaperwhite, "0", v => _cfg.TargetPaperwhite = v);
        FloatRow(L.T("settings.source_peak"), 200f, 4000f, _cfg.SourcePeakNits, "0", v => _cfg.SourcePeakNits = v);
        Note(L.T("settings.tonemap.note"));
        EndSection(s);
    }

    private void BuildAdjustments()
    {
        var s = BeginSection("adjust", L.T("section.adjust"));
        FloatRow(L.T("settings.exposure"), -3f, 3f, _cfg.Exposure, "0.00", v => _cfg.Exposure = v);
        FloatRow(L.T("settings.saturation"), 0f, 2f, _cfg.Saturation, "0.00", v => _cfg.Saturation = v);
        FloatRow(L.T("settings.contrast"), 0f, 2f, _cfg.Contrast, "0.00", v => _cfg.Contrast = v);
        FloatRow(L.T("settings.gamma"), 0.3f, 3f, _cfg.Gamma, "0.00", v => _cfg.Gamma = v);
        EndSection(s);
    }

    private void BuildFrameRate()
    {
        var s = BeginSection("frame", L.T("section.frame"));
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
        EndSection(s);
    }

    private void BuildFpsOverlay()
    {
        var s = BeginSection("fps_overlay", L.T("settings.fps_overlay"), _cfg.FpsOverlay, v => _cfg.FpsOverlay = v);
        var positions = MirrorConfig.OverlayPositions.Select(pos => (pos, L.T("pos." + pos))).ToArray();
        ChoiceRow(L.T("settings.fps_overlay_position"), positions, _cfg.FpsOverlayPosition,
                  v => _cfg.FpsOverlayPosition = v);
        Note(L.T("settings.fps_overlay.note"));
        EndSection(s);
    }

    private void BuildCrop()
    {
        var s = BeginSection("crop", L.T("section.crop"));
        NumRow(L.T("settings.crop_x"), 0m, 1m, (decimal)_cfg.CropX, 0.01m, 2, v => _cfg.CropX = (float)v);
        NumRow(L.T("settings.crop_y"), 0m, 1m, (decimal)_cfg.CropY, 0.01m, 2, v => _cfg.CropY = (float)v);
        NumRow(L.T("settings.crop_w"), 0.01m, 1m, (decimal)_cfg.CropW, 0.01m, 2, v => _cfg.CropW = (float)v);
        NumRow(L.T("settings.crop_h"), 0.01m, 1m, (decimal)_cfg.CropH, 0.01m, 2, v => _cfg.CropH = (float)v);
        Note(L.T("settings.crop.note"));
        EndSection(s);
    }

    private void BuildConnection()
    {
        var s = BeginSection("connection", L.T("section.connection"));
        Lbl(L.T("settings.agent_bind"));
        var bind = new TextBox { Location = new Point(CX, _y), Width = 250, Text = _app.AgentBind };
        bind.TextChanged += (_, _) => { if (!_loading) { _app.AgentBind = bind.Text.Trim(); SaveApp(); } };
        _body.Controls.Add(bind);
        _y += ROW;
        Note(L.T("settings.agent_bind.note"));
        NumRow(L.T("settings.agent_port"), 1, 65535, _app.AgentPort, 1, 0, v => _app.AgentPort = (int)v, app: true);
        CheckRow(L.T("settings.agent_enabled"), _app.AgentEnabled, v => _app.AgentEnabled = v, app: true);
        CheckRow(L.T("settings.autostart_mirror"), _app.AutostartMirror, v => _app.AutostartMirror = v, app: true);
        var al = new CheckBox { Text = L.T("settings.login_autostart"), Location = new Point(LX, _y), AutoSize = true, Checked = IsLoginAutostart() };
        al.CheckedChanged += (_, _) => { if (!_loading) SetLoginAutostart(al.Checked); };
        _body.Controls.Add(al);
        _y += 28;
        Note(L.T("settings.agent_note"));
        EndSection(s);
    }

    private void BuildGeneral()
    {
        var s = BeginSection("general", L.T("section.general"));
        var langs = new List<(string Value, string Text)> { (L.Auto, L.T("settings.language.auto")) };
        langs.AddRange(L.Available().Select(code => (code, $"{L.DisplayName(code)} ({code})")));
        ChoiceRow(L.T("settings.language"), langs.ToArray(), _app.Language, code =>
        {
            _app.Language = code;
            // Rebuilding this window from inside its own combo event is unsafe → defer.
            BeginInvoke(_rebuild);
        }, app: true);
        Note(L.T("settings.language.note"));
        CheckRow(L.T("settings.keep_awake"), _cfg.KeepAwake, v => _cfg.KeepAwake = v);
        EndSection(s);
    }

    // ── About / Update ─────────────────────────────────────────────────────
    private void BuildAbout()
    {
        var s = BeginSection("about", L.T("section.about"));
        Note($"{AppInfo.Name}  v{AppInfo.Version}");
        Note(L.T("about.copyright"));

        var repo = new LinkLabel { Text = AppInfo.RepoUrl.Replace("https://", ""), Location = new Point(LX, _y), AutoSize = true };
        repo.LinkClicked += (_, _) => OpenUrl(AppInfo.RepoUrl);
        _body.Controls.Add(repo);
        _y += 26;
        Note(L.T("about.update_hint"));
        EndSection(s);
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
        if (src) RebuildTargetList();   // the new source may not stay a target candidate
        SaveCfg();
    }

    private void StoreTarget()
    {
        int entry = _dstCombo.SelectedIndex;
        if (entry < 0 || entry >= _dstIndices.Count) return;
        var d = _displays[_dstIndices[entry]];
        if (d.IsPrimary) { RevertPrimaryPick(); return; }   // listed for orientation only
        _cfg.TargetDisplay = d.Index; _cfg.TargetKey = d.Key; _cfg.TargetLabel = d.Friendly;
        SaveCfg();
    }

    /// <summary>A click on the dimmed primary entry snaps back to the saved target (or nothing).</summary>
    private void RevertPrimaryPick()
    {
        int entry = _dstCombo.SelectedIndex;
        if (entry < 0 || entry >= _dstIndices.Count || !_displays[_dstIndices[entry]].IsPrimary) return;
        int saved = FindByIdentity(_cfg.TargetKey, _cfg.TargetLabel, _cfg.TargetDisplay);
        bool wasLoading = _loading;
        _loading = true;
        _dstCombo.SelectedIndex = saved >= 0 && !_displays[saved].IsPrimary ? _dstIndices.IndexOf(saved) : -1;
        _loading = wasLoading;
    }

    private void DrawTargetItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _dstIndices.Count) { e.DrawBackground(); return; }
        bool primary = _displays[_dstIndices[e.Index]].IsPrimary;
        bool selected = (e.State & DrawItemState.Selected) != 0 && !primary;
        using var back = new SolidBrush(selected ? SystemColors.Highlight : _dstCombo.BackColor);
        e.Graphics.FillRectangle(back, e.Bounds);
        var color = primary ? SystemColors.GrayText : selected ? SystemColors.HighlightText : _dstCombo.ForeColor;
        TextRenderer.DrawText(e.Graphics, _dstCombo.Items[e.Index]?.ToString(), e.Font ?? Font, e.Bounds, color,
                              TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// Source and target must differ: in monitor mode the target list leaves out the current source
    /// monitor (mirroring a monitor onto itself only feeds the picture back into the capture). The
    /// primary monitor is listed but dimmed and not selectable — the mirror would cover your own
    /// desktop and taskbar. If the saved target is the source or the primary monitor, it is cleared
    /// (nothing selected) instead of silently picking another monitor.
    /// </summary>
    private void RebuildTargetList()
    {
        int srcIdx = _cfg.ResolveCaptureMode() == "monitor"
            ? FindByIdentity(_cfg.SourceKey, _cfg.SourceLabel, _cfg.SourceDisplay) : -1;
        int dstIdx = FindByIdentity(_cfg.TargetKey, _cfg.TargetLabel, _cfg.TargetDisplay);
        // A saved target equal to the source, or on the primary monitor, is cleared (nothing selected).
        bool clash = dstIdx >= 0 && (dstIdx == srcIdx || _displays[dstIdx].IsPrimary);
        if (clash) ClearTarget();

        var names = DisplayNames();
        _dstIndices = Enumerable.Range(0, _displays.Count).Where(i => i != srcIdx).ToList();
        bool wasLoading = _loading;
        _loading = true;
        _dstCombo.Items.Clear();
        _dstCombo.Items.AddRange(_dstIndices.Select(i => (object)(_displays[i].IsPrimary
            ? $"{names[i]}  —  {L.T("display.primary")}" : names[i])).ToArray());
        _dstCombo.SelectedIndex = clash ? -1 : _dstIndices.IndexOf(dstIdx);
        _loading = wasLoading;
        if (clash) SaveCfg();
    }

    /// <summary>No target: empty identity + index -1 (the engine then reports "no target selected").</summary>
    private void ClearTarget()
    {
        _cfg.TargetKey = "";
        _cfg.TargetLabel = "";
        _cfg.TargetDisplay = -1;
    }

    private void RefreshDisplays()
    {
        _displays = MirrorEngine.EnumerateDisplays();
        var names = DisplayNames();
        _loading = true;
        _srcCombo.Items.Clear(); _srcCombo.Items.AddRange(names);
        int s = FindByIdentity(_cfg.SourceKey, _cfg.SourceLabel, _cfg.SourceDisplay);
        _srcCombo.SelectedIndex = s >= 0 ? s : -1;
        _loading = false;
        RebuildTargetList();
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
        RebuildTargetList();   // window mode: every monitor is a valid target again
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

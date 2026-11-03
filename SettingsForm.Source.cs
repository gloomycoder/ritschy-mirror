using System.Drawing;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// Capture rows of the mirroring section: ONE capture-mode drop-down (Monitor DXGI / Monitor WGC /
/// Window WGC) and ONE capture-target drop-down whose list follows the mode (monitors or windows).
/// The config keeps its two keys (<c>capture_mode</c> + <c>monitor_api</c>) for compatibility with
/// the HTTP agent and older files; this UI just presents them as one choice. Changing the mode
/// clears the capture target — the list then holds different things.
/// </summary>
public sealed partial class SettingsForm
{
    private static readonly string[] CaptureModes = { "monitor_dxgi", "monitor_wgc", "window" };

    private ComboBox _sourceCombo = null!;
    private Label _sourceNote = null!;
    private CheckBox _similarTitle = null!;

    private bool IsWindowMode => _cfg.ResolveCaptureMode() == "window";

    private string CurrentCaptureMode() =>
        IsWindowMode ? "window" : _cfg.ResolveMonitorApi() == "wgc" ? "monitor_wgc" : "monitor_dxgi";

    private void BuildCaptureRows()
    {
        ChoiceRow(L.T("settings.capture_mode"), CaptureModes.Select(m => (m, L.T("mode." + m))).ToArray(),
                  CurrentCaptureMode(), SetCaptureMode);
        Note(L.T("settings.capture_mode.note"));

        Lbl(L.T("settings.capture_target"));
        _sourceCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 330 };
        _sourceCombo.SelectedIndexChanged += (_, _) => { if (!_loading) StoreSource(); };
        _body.Controls.Add(_sourceCombo);
        var refresh = new Button { Text = "↻", Location = new Point(CX + 338, _y - 1), Width = 32, Height = 26 };
        refresh.Click += (_, _) => RefreshSources();
        _body.Controls.Add(refresh);
        _y += ROW;

        // "saved target not connected / not open" — one reserved line, filled per mode.
        int lineHeight = ToLayout(TextRenderer.MeasureText("Ag", Font).Height);
        _sourceNote = new Label { Location = new Point(LX, _y), AutoSize = false, AutoEllipsis = true,
                                  Size = new Size(NoteWidth, lineHeight), ForeColor = Color.DimGray };
        _body.Controls.Add(_sourceNote);
        _y += lineHeight + 6;

        // Window capture only: also resume on a slightly renamed window (games' build numbers).
        _similarTitle = CheckRow(L.T("settings.window_title_similar"), _cfg.WindowTitleSimilar, v =>
        {
            _cfg.WindowTitleSimilar = v;
            PopulateSources();   // the shown selection follows the same rule
        });
        Note(L.T("settings.window_title_similar.note"));
        PopulateSources();
    }

    /// <summary>Mode picked: map to capture_mode/monitor_api and start with no capture target.</summary>
    private void SetCaptureMode(string mode)
    {
        _cfg.CaptureMode = mode == "window" ? "window" : "monitor";
        if (mode != "window") _cfg.MonitorApi = mode == "monitor_wgc" ? "wgc" : "dxgi";
        _cfg.SourceKey = ""; _cfg.SourceLabel = ""; _cfg.SourceDisplay = -1;
        _cfg.WindowExe = ""; _cfg.WindowTitle = "";
        PopulateSources();
        RebuildTargetList();   // in window mode every monitor is a valid output again
    }

    /// <summary>Fill the capture-target list for the current mode and select the saved target.</summary>
    private void PopulateSources()
    {
        bool wasLoading = _loading;
        _loading = true;
        _sourceCombo.Items.Clear();
        int sel;
        string missing = "";
        if (IsWindowMode)
        {
            _windows = WindowEnum.List();
            _sourceCombo.Items.AddRange(_windows.Select(w => (object)w.DisplayName).ToArray());
            sel = FindWindowIndex();
            if (sel < 0 && HasWindowIdentity()) missing = L.T("settings.window_missing", WindowLabel());
        }
        else
        {
            _sourceCombo.Items.AddRange(DisplayNames().Cast<object>().ToArray());
            sel = FindByIdentity(_cfg.SourceKey, _cfg.SourceLabel, _cfg.SourceDisplay);
            if (sel < 0 && HasIdentity(_cfg.SourceKey, _cfg.SourceLabel))
                missing = L.T("settings.source_missing", LabelOf(_cfg.SourceKey, _cfg.SourceLabel));
        }
        _sourceCombo.SelectedIndex = sel;
        _sourceNote.Text = missing;
        if (_similarTitle != null) _similarTitle.Enabled = IsWindowMode;
        _loading = wasLoading;
    }

    /// <summary>Persist the picked capture target: window identity (exe + title) or monitor
    /// identity (index for backward compat + stable key/label).</summary>
    private void StoreSource()
    {
        int i = _sourceCombo.SelectedIndex;
        if (IsWindowMode)
        {
            if (i < 0 || i >= _windows.Count) return;
            _cfg.WindowExe = _windows[i].Exe;
            _cfg.WindowTitle = _windows[i].Title;
        }
        else
        {
            if (i < 0 || i >= _displays.Count) return;
            var d = _displays[i];
            _cfg.SourceDisplay = i; _cfg.SourceKey = d.Key; _cfg.SourceLabel = d.Friendly;
            RebuildTargetList();   // the capture monitor may not stay an output candidate
        }
        _sourceNote.Text = "";
        SaveCfg();
    }

    private void RefreshSources()
    {
        if (!IsWindowMode) _displays = MirrorEngine.EnumerateDisplays();
        PopulateSources();
        RebuildTargetList();
    }
}

using System.Drawing;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// Scale-filter row of the mirroring section: an owner-drawn drop-down that lists every filter with
/// its character, typical use and cost on the right, and is disabled (with a note) whenever the
/// current source/target/layout combination draws the source 1:1 — no filter would run then.
/// </summary>
public sealed partial class SettingsForm
{
    private const int FilterDropDownWidth = 640;
    private const int FilterDescX = 230;   // description column inside the drop-down

    private ComboBox? _filterCombo;
    private Label? _filterNote;

    private void BuildScaleFilterRow()
    {
        Lbl(L.T("settings.scale_filter"));
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 250,
            DrawMode = DrawMode.OwnerDrawFixed, DropDownWidth = FilterDropDownWidth,
        };
        c.Items.AddRange(MirrorConfig.ScaleFilters.Cast<object>().ToArray());
        int sel = Array.FindIndex(MirrorConfig.ScaleFilters, f => string.Equals(f, _cfg.ScaleFilter, OIC));
        c.SelectedIndex = sel >= 0 ? sel : 0;
        c.DrawItem += DrawFilterItem;
        c.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || c.SelectedIndex < 0) return;
            _cfg.ScaleFilter = MirrorConfig.ScaleFilters[c.SelectedIndex];
            SaveCfg();
        };
        _body.Controls.Add(c);
        _filterCombo = c;
        _y += ROW;

        _filterNote = new Label { Location = new Point(LX, _y), AutoSize = true, ForeColor = Color.DimGray,
                                  MaximumSize = new Size(NoteWidth, 0) };
        _body.Controls.Add(_filterNote);
        _y += ToLayout(TextRenderer.MeasureText("Ag", Font).Height) + 8;
    }

    /// <summary>Closed box: filter name. Open list: name + "character · use · cost" in grey.</summary>
    private void DrawFilterItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox c) return;
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= MirrorConfig.ScaleFilters.Length) return;
        string id = MirrorConfig.ScaleFilters[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool editPart = (e.State & DrawItemState.ComboBoxEdit) != 0;
        var font = e.Font ?? Font;
        var nameColor = !c.Enabled ? SystemColors.GrayText : selected ? SystemColors.HighlightText : c.ForeColor;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

        if (editPart)
        {
            TextRenderer.DrawText(e.Graphics, L.T($"filter.{id}.name"), font, e.Bounds, nameColor, flags);
            return;
        }
        int descX = (int)(FilterDescX * _scale);
        var nameRect = new Rectangle(e.Bounds.X + 2, e.Bounds.Y, descX - 6, e.Bounds.Height);
        var descRect = new Rectangle(e.Bounds.X + descX, e.Bounds.Y, e.Bounds.Width - descX - 2, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, L.T($"filter.{id}.name"), font, nameRect, nameColor, flags);
        TextRenderer.DrawText(e.Graphics, L.T($"filter.{id}.desc"), font, descRect,
                              selected ? SystemColors.HighlightText : SystemColors.GrayText, flags);
    }

    /// <summary>
    /// Disable the filter choice when the source crop maps 1:1 onto the output. Uses the renderer's
    /// own layout maths; unknown sizes (window capture, unplugged monitor) keep it enabled.
    /// </summary>
    private void UpdateFilterAvailability()
    {
        if (_filterCombo is null || _filterNote is null || _filterCombo.IsDisposed) return;
        bool unscaled = false;
        var src = _cfg.ResolveCaptureMode() == "monitor"
            ? DisplayAt(FindByIdentity(_cfg.SourceKey, _cfg.SourceLabel, _cfg.SourceDisplay)) : null;
        (int W, int H)? output = _cfg.ResolveOutputMode() == "windowed"
            ? (_cfg.WindowWidth, _cfg.WindowHeight)
            : DisplayAt(FindByIdentity(_cfg.TargetKey, _cfg.TargetLabel, _cfg.TargetDisplay)) is { } d
                ? (d.Right - d.Left, d.Bottom - d.Top) : null;
        if (src != null && output is { } o)
            unscaled = Renderer.IsUnscaled(src.Right - src.Left, src.Bottom - src.Top, o.W, o.H, _cfg);

        if (_filterCombo.Enabled == unscaled) { _filterCombo.Enabled = !unscaled; _filterCombo.Invalidate(); }
        string id = MirrorConfig.ScaleFilters[Math.Max(0, _filterCombo.SelectedIndex)];
        _filterNote.Text = unscaled ? L.T("settings.scale_filter.unscaled") : L.T($"filter.{id}.desc");
    }

    private DisplayInfo? DisplayAt(int index) => index >= 0 && index < _displays.Count ? _displays[index] : null;
}

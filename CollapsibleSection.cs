using System.Drawing;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// One main feature in the settings window: a clickable header (expand arrow, title or an on/off
/// checkbox for the feature, a short live summary) and a body with the feature's sub-settings that
/// is only shown while the section is expanded.
///
/// Sizes are explicit — no AutoSize containers (nested AutoSize panels collapsed to height 0
/// before). The caller lays out the body with absolute coordinates and then calls
/// <see cref="SetBodyHeight"/>; expanding/collapsing just changes this panel's height, and the
/// parent FlowLayoutPanel re-flows the sections below it.
/// </summary>
public sealed class CollapsibleSection : Panel
{
    private const int HeaderHeight = 34;
    private const int ArrowX = 8, TitleX = 34, SummaryX = 300, TextY = 8;
    private const string ArrowCollapsed = "▶", ArrowExpanded = "▼";

    private readonly Panel _header;
    private readonly Label _arrow;
    private readonly SummaryLabel _summary;

    public string Id { get; }
    public Panel Body { get; }
    /// <summary>On/off checkbox in the header for features that can be switched (null otherwise).</summary>
    public CheckBox? Toggle { get; }
    public bool Expanded { get; private set; }

    public event EventHandler? ExpandedChanged;

    public CollapsibleSection(string id, string title, int width, bool expanded,
                              bool hasToggle = false, bool toggled = false)
    {
        Id = id;
        Width = width;
        Margin = new Padding(0, 0, 0, 6);
        BackColor = SystemColors.Window;

        _header = new Panel { Location = new Point(0, 0), Size = new Size(width, HeaderHeight),
                              BackColor = SystemColors.ControlLight, Cursor = Cursors.Hand };
        _arrow = new Label { Location = new Point(ArrowX, TextY), AutoSize = true, ForeColor = Color.DimGray };
        _header.Controls.Add(_arrow);

        var bold = new Font(Font, FontStyle.Bold);
        if (hasToggle)
        {
            // The checkbox switches the feature itself; clicking it must not also expand/collapse.
            Toggle = new CheckBox { Text = title, Location = new Point(TitleX - 2, TextY - 3), AutoSize = true,
                                    Checked = toggled, Font = bold, Cursor = Cursors.Default };
            _header.Controls.Add(Toggle);
        }
        else
        {
            var titleLabel = new Label { Text = title, Location = new Point(TitleX, TextY), AutoSize = true, Font = bold };
            titleLabel.Click += (_, _) => SetExpanded(!Expanded);
            _header.Controls.Add(titleLabel);
        }

        _summary = new SummaryLabel { Location = new Point(SummaryX, 0), Size = new Size(width - SummaryX - 10, HeaderHeight),
                                      ForeColor = Color.DimGray, BackColor = _header.BackColor };
        _header.Controls.Add(_summary);

        _header.Click += (_, _) => SetExpanded(!Expanded);
        _arrow.Click += (_, _) => SetExpanded(!Expanded);
        _summary.Click += (_, _) => SetExpanded(!Expanded);

        Body = new Panel { Location = new Point(0, HeaderHeight), Size = new Size(width, 0) };
        Controls.Add(Body);
        Controls.Add(_header);

        Expanded = expanded;
        ApplyExpanded();
    }

    /// <summary>Short live status shown at the right of the header (e.g. "60 fps limit").</summary>
    public void SetSummary(string text)
    {
        if (_summary.Text != text) _summary.Text = text;
    }

    /// <summary>Height of the laid-out body (in the same units as the coordinates used to build it).</summary>
    public void SetBodyHeight(int height)
    {
        Body.Height = height;
        ApplyExpanded();
    }

    public void SetExpanded(bool expanded)
    {
        if (Expanded == expanded) return;
        Expanded = expanded;
        ApplyExpanded();
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Header summary: ALWAYS one line, right-aligned, cut with "…". A Label wraps long text (e.g. a
    /// long window title) onto a second line that the header then clips.
    /// </summary>
    private sealed class SummaryLabel : Control
    {
        private const TextFormatFlags Flags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        public SummaryLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, Flags);
        }
    }

    private void ApplyExpanded()
    {
        _arrow.Text = Expanded ? ArrowExpanded : ArrowCollapsed;
        Body.Visible = Expanded;
        // Read the live (DPI-scaled) heights, so toggling after auto-scaling stays correct.
        Height = _header.Height + (Expanded ? Body.Height : 0);
    }
}

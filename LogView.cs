using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// Live view of mirror.log at the bottom of the settings window: shows the tail of the file on open,
/// then appends every new line the engine logs (<see cref="MirrorEngine.LogWritten"/>, raised on
/// whatever thread logged → marshalled to the UI thread).
/// </summary>
public sealed class LogView : Panel
{
    private const int InitialTailBytes = 32 * 1024;
    private const int MaxLines = 500;      // keep the box responsive on long sessions
    private const int TrimSlack = 100;     // trim in batches, not on every new line
    private const float FontSizePt = 8.5f;

    private readonly MirrorEngine _engine;
    private readonly TextBox _text;
    private readonly Queue<string> _lines = new();   // what the box shows, oldest first

    public LogView(MirrorEngine engine)
    {
        _engine = engine;
        Dock = DockStyle.Bottom;
        Height = 170;
        _text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BackColor = SystemColors.Window,
            Font = new Font(FontFamily.GenericMonospace, FontSizePt),
        };
        Controls.Add(_text);

        LoadTail();
        // Scrolling only works once the box has a handle and is shown → jump to the newest line then.
        _text.HandleCreated += (_, _) => ScrollToEnd();
        VisibleChanged += (_, _) => { if (Visible) ScrollToEnd(); };
        _engine.LogWritten += OnLogWritten;
        Disposed += (_, _) => _engine.LogWritten -= OnLogWritten;
    }

    private void OnLogWritten(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => Append(line)); }
        // Handle destroyed between the check and the call (ObjectDisposedException derives from it).
        catch (InvalidOperationException) { }
    }

    private void Append(string line)
    {
        if (_text.IsDisposed) return;
        _lines.Enqueue(line);
        if (_lines.Count <= MaxLines + TrimSlack)
        {
            _text.AppendText(line + Environment.NewLine);
            return;
        }
        while (_lines.Count > MaxLines) _lines.Dequeue();
        ShowLines();
        ScrollToEnd();
    }

    private void ShowLines() =>
        _text.Text = _lines.Count == 0 ? "" : string.Join(Environment.NewLine, _lines) + Environment.NewLine;

    private void ScrollToEnd()
    {
        if (_text.IsDisposed || !_text.IsHandleCreated) return;
        _text.SelectionStart = _text.TextLength;
        _text.ScrollToCaret();
    }

    /// <summary>Last few KB (at most <see cref="MaxLines"/> lines) of mirror.log (shared read — the
    /// engine keeps appending to it).</summary>
    private void LoadTail()
    {
        try
        {
            if (!File.Exists(_engine.LogPath)) return;
            using var fs = new FileStream(_engine.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, fs.Length - InitialTailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string tail = reader.ReadToEnd();
            if (start > 0)
            {
                int nl = tail.IndexOf('\n');
                tail = nl >= 0 ? tail[(nl + 1)..] : tail;   // skip the partial first line
            }
            foreach (var l in tail.Replace("\r\n", "\n").Split('\n'))
                if (l.Length > 0) _lines.Enqueue(l);
            while (_lines.Count > MaxLines) _lines.Dequeue();
            ShowLines();
        }
        catch (IOException ex) { _text.Text = "mirror.log: " + ex.Message + Environment.NewLine; }
        catch (UnauthorizedAccessException ex) { _text.Text = "mirror.log: " + ex.Message + Environment.NewLine; }
    }
}

using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace RitschyMirror;

/// <summary>
/// Audio-mirror section of the settings window: the master switch in the header, then one checkbox
/// row per capturable device, grouped into output devices (loopback — what they play) and input
/// devices, each with its own gain slider plus a common gain per group (dB, additive).
///
/// The device-dependent rows are rebuilt in place whenever Windows reports a device change (default
/// switched, plugged, unplugged) — so "Default output device (…)" and the lists match what the audio
/// mirror actually uses, without reopening the window. Only the side that changed is rebuilt.
/// </summary>
public sealed partial class SettingsForm
{
    private const int SourceIndent = 12;
    private const int SourceTextWidth = 290;
    private const int GainX = 330, GainWidth = 190, GainValueX = 528, GainNumberWidth = 64;
    private const int GainTicksPerDb = 2;   // 0.5 dB steps
    private const int DeviceChangeDebounceMs = 500;   // a default switch fires several notifications

    // The device-dependent rows sit in three stacked panels: output side (mix output, follow switch,
    // output devices), input devices, and the tail (refresh button, status line). A device change
    // rebuilds only the side whose device list (ids, names, default) actually differs.
    private CollapsibleSection _audioSection = null!;
    private Panel _audioRender = null!, _audioCapture = null!, _audioTail = null!;
    private string _renderSignature = "", _captureSignature = "";
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDeviceNotificationClient? _deviceNotify;
    private System.Windows.Forms.Timer? _deviceDebounce;

    private void BuildAudio()
    {
        var s = BeginSection("audio", L.T("settings.audio_mirror"), _cfg.AudioMirror, v => _cfg.AudioMirror = v);
        Note(L.T("settings.audio_mirror.note"));
        _audioSection = s;
        int top = _y;
        _audioRender = AddAudioPart(s.Body, ref top, BuildRenderPart);
        _audioCapture = AddAudioPart(s.Body, ref top, BuildCapturePart);
        _audioTail = AddAudioPart(s.Body, ref top, BuildAudioTail);
        _body = s.Body;
        _y = top;
        EndSection(s);
        WatchAudioDevices();
    }

    /// <summary>A panel at <paramref name="top"/> filled by <paramref name="build"/> (96-dpi layout pixels).</summary>
    private Panel AddAudioPart(Panel host, ref int top, Action build)
    {
        var panel = NewAudioPart(build);
        panel.Location = new Point(0, top);
        host.Controls.Add(panel);
        top += panel.Height;
        return panel;
    }

    private Panel NewAudioPart(Action build)
    {
        var panel = new Panel { Width = SectionWidth };
        var body = _body; int y = _y;
        _body = panel;
        _y = 0;
        try { build(); panel.Height = _y; }
        finally { _body = body; _y = y; }
        return panel;
    }

    /// <summary>Mix output, follow-volume switch and the output-device group.</summary>
    private void BuildRenderPart()
    {
        _renderSignature = DeviceSignature(render: true);
        // Where the mix goes: automatic (target monitor's HDMI/DP audio) or a fixed device.
        var autoIds = AudioDevices.TargetEndpointIds(_cfg.TargetLabel);
        BuildOutputRow(autoIds);
        CheckRow(L.T("settings.audio_follow_volume"), _cfg.AudioFollowVolume, v => _cfg.AudioFollowVolume = v);
        Note(L.T("settings.audio_follow_volume.note"));

        // The output endpoint itself is never offered as a source: capturing it would loop the mix.
        var targetIds = string.IsNullOrWhiteSpace(_cfg.AudioOutputId)
            ? autoIds : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _cfg.AudioOutputId };
        BuildAudioGroup(render: true, targetIds);
    }

    private void BuildCapturePart()
    {
        _captureSignature = DeviceSignature(render: false);
        BuildAudioGroup(render: false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private void BuildAudioTail()
    {
        var refresh = new Button { Text = L.T("audio.src.refresh"), Location = new Point(LX, _y), AutoSize = true };
        refresh.Click += (_, _) => RefreshAudioDevices(force: true);
        _body.Controls.Add(refresh);
        _y += 36;

        // Device names make this line long and it changes live → reserve room for a few wrapped
        // lines, measured in the actual font (a fixed pixel height clips at high display scaling).
        int statusHeight = ToLayout(TextRenderer.MeasureText("Ag", Font).Height) * StatusLines;
        _audioStatus = new Label { Location = new Point(LX, _y), AutoSize = false, AutoEllipsis = true,
                                   Size = new Size(NoteWidth, statusHeight) };
        _body.Controls.Add(_audioStatus);
        _y += statusHeight;
    }

    /// <summary>What a device group shows: every active device (id, name) and which one is the default.</summary>
    private static string DeviceSignature(bool render) =>
        string.Join("|", AudioDevices.List(render).Select(d => $"{d.Id}={d.Name}{(d.IsDefault ? "*" : "")}"));

    /// <summary>Rebuild the side(s) whose devices changed (both when <paramref name="force"/>d), in place.</summary>
    private void RefreshAudioDevices(bool force = false)
    {
        if (IsDisposed) return;
        bool render = force || DeviceSignature(render: true) != _renderSignature;
        bool capture = force || DeviceSignature(render: false) != _captureSignature;
        if (!render && !capture) return;

        // Swapping panels makes the scrolling window jump to the top (relayout, or focus moving off a
        // disposed control) → keep the user's scroll position.
        var scroll = _flow.AutoScrollPosition;   // reads as negative offsets
        bool loading = _loading;
        _loading = true;
        var host = _audioSection.Body;
        // No painting until the new rows are complete and placed: one repaint instead of the rows
        // appearing one after another.
        SetRedraw(_flow, false);
        try
        {
            host.SuspendLayout();
            try
            {
                if (render) _audioRender = ReplaceAudioPart(host, _audioRender, BuildRenderPart);
                if (capture) _audioCapture = ReplaceAudioPart(host, _audioCapture, BuildCapturePart);
                // Re-stack below a part whose height changed (device pixels from here on).
                _audioCapture.Top = _audioRender.Bottom;
                _audioTail.Top = _audioCapture.Bottom;
            }
            finally
            {
                host.ResumeLayout();
                _loading = loading;
            }
            _audioSection.SetBodyHeight(_audioTail.Bottom + (int)Math.Ceiling(6 * DeviceDpi / 96f));
            _flow.PerformLayout();
            RestoreScroll(scroll);
        }
        finally
        {
            SetRedraw(_flow, true);   // never leave the window frozen
        }
        BeginInvoke(() => RestoreScroll(scroll));   // again after any deferred flow layout
    }

    private const int WM_SETREDRAW = 0x000B;
    private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_FRAME = 0x400;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

    /// <summary>Freeze / unfreeze painting of a control and its children (on: one full repaint).</summary>
    private static void SetRedraw(Control c, bool on)
    {
        if (!c.IsHandleCreated) return;
        SendMessage(c.Handle, WM_SETREDRAW, (nint)(on ? 1 : 0), IntPtr.Zero);
        if (on) RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME);
    }

    private void RestoreScroll(Point scroll)
    {
        if (!_flow.IsDisposed) _flow.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
    }

    private Panel ReplaceAudioPart(Panel host, Panel old, Action build)
    {
        var fresh = NewAudioPart(build);
        // Built in layout pixels like the rest of the window, but the form already applied its DPI
        // scaling at load → scale this part by the current DPI before it goes in.
        float scale = DeviceDpi / 96f;
        fresh.Scale(new SizeF(scale, scale));
        fresh.Location = old.Location;
        if (old.ContainsFocus) ActiveControl = null;   // don't let focus (and the scroll) wander
        host.Controls.Add(fresh);
        host.Controls.Remove(old);
        old.Dispose();
        return fresh;
    }

    /// <summary>Windows device notifications (any thread) → one debounced refresh on the UI thread.</summary>
    private void WatchAudioDevices()
    {
        _deviceDebounce = new System.Windows.Forms.Timer { Interval = DeviceChangeDebounceMs };
        _deviceDebounce.Tick += (_, _) => { _deviceDebounce.Stop(); RefreshAudioDevices(); };
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _deviceNotify = _deviceEnumerator.CreateNotificationClient(useSynchronizationContext: false);
            _deviceNotify.DefaultDeviceChanged += (_, _) => OnAudioDevicesChanged();
            _deviceNotify.DeviceAdded += (_, _) => OnAudioDevicesChanged();
            _deviceNotify.DeviceRemoved += (_, _) => OnAudioDevicesChanged();
            _deviceNotify.DeviceStateChanged += (_, _) => OnAudioDevicesChanged();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _engine.Log("Settings: audio device notifications unavailable — use Refresh devices: " + ex.Message);
        }
        FormClosed += (_, _) =>
        {
            try { _deviceNotify?.Dispose(); } catch (Exception) { /* closing anyway */ }
            try { _deviceEnumerator?.Dispose(); } catch (Exception) { /* closing anyway */ }
            _deviceDebounce.Dispose();
        };
    }

    private void OnAudioDevicesChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed) { _deviceDebounce?.Stop(); _deviceDebounce?.Start(); } }); }
        // Handle destroyed between the check and the call (ObjectDisposedException derives from it).
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// One device group: header, common gain, "default device" row, then every other active device.
    /// The current default is not listed twice; selected devices that are unplugged stay listed.
    /// </summary>
    private void BuildAudioGroup(bool render, HashSet<string> targetIds)
    {
        string kind = render ? AudioSourceConfig.Render : AudioSourceConfig.Capture;
        _body.Controls.Add(new Label { Text = L.T(render ? "audio.src.render_header" : "audio.src.capture_header"),
                                       Location = new Point(LX, _y + 4), AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _y += 28;

        GainSlider(_y, render ? _cfg.AudioRenderGainDb : _cfg.AudioCaptureGainDb, v =>
        {
            if (render) _cfg.AudioRenderGainDb = v; else _cfg.AudioCaptureGainDb = v;
        });
        _body.Controls.Add(new Label { Text = L.T("audio.src.group_gain"), Location = new Point(LX + SourceIndent, _y + 4), AutoSize = true });
        _y += ROW;

        var devices = AudioDevices.List(render);
        var def = devices.FirstOrDefault(d => d.IsDefault);
        string defName = def?.Name ?? L.T("audio.src.none");
        SourceRow(kind, AudioSourceConfig.DefaultId,
                  L.T(render ? "audio.src.default_render" : "audio.src.default_capture", defName), def?.Name ?? "");

        // The current default is covered by the row above — unless it was also ticked by name: then it
        // stays listed (marked), otherwise un-ticking "default" would leave a hidden selection capturing.
        foreach (var d in devices.Where(d => !(render && targetIds.Contains(d.Id))))
        {
            if (!d.IsDefault) SourceRow(kind, d.Id, d.Name, d.Name);
            else if (FindSource(kind, d.Id) is { Enabled: true })
                SourceRow(kind, d.Id, L.T("audio.src.is_default", d.Name), d.Name);
        }

        foreach (var saved in _cfg.AudioSources.Where(x => x.IsRender == render && !x.IsDefault
                     && !devices.Any(d => string.Equals(d.Id, x.Id, OIC))))
            SourceRow(kind, saved.Id, L.T("audio.src.disconnected", saved.Name.Length > 0 ? saved.Name : saved.Id), saved.Name);
        _y += 6;
    }

    /// <summary>Output device for the mix: "automatic" (target monitor, by EDID name) or a fixed one.</summary>
    private void BuildOutputRow(HashSet<string> autoIds)
    {
        var devices = AudioDevices.List(render: true);
        string autoName = devices.FirstOrDefault(d => autoIds.Contains(d.Id))?.Name
                          ?? (_cfg.TargetLabel.Length > 0 ? _cfg.TargetLabel : L.T("audio.src.none"));
        var items = new List<(string Value, string Text)> { ("", L.T("audio.out.auto", autoName)) };
        items.AddRange(devices.Select(d => (d.Id, d.Name)));
        if (_cfg.AudioOutputId.Length > 0 && !devices.Any(d => string.Equals(d.Id, _cfg.AudioOutputId, OIC)))
            items.Add((_cfg.AudioOutputId, L.T("audio.src.disconnected", _cfg.AudioOutputName.Length > 0 ? _cfg.AudioOutputName : _cfg.AudioOutputId)));

        var combo = ChoiceRow(L.T("audio.out.label"), items.ToArray(), _cfg.AudioOutputId, id =>
        {
            _cfg.AudioOutputId = id;
            _cfg.AudioOutputName = devices.FirstOrDefault(d => d.Id == id)?.Name ?? "";
        });
        combo.Width = 330;
        combo.DropDownWidth = 420;
        Note(L.T("audio.out.note"));
    }

    /// <summary>Checkbox (include this device) + its own gain.</summary>
    private void SourceRow(string kind, string id, string text, string name)
    {
        var entry = FindSource(kind, id);
        var cb = new CheckBox { Text = text, Location = new Point(LX + SourceIndent, _y + 2), AutoSize = false,
                                Size = new Size(SourceTextWidth, 24), AutoEllipsis = true, Checked = entry?.Enabled ?? false };
        cb.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            GetOrAddSource(kind, id, name).Enabled = cb.Checked;
            SaveCfg();
        };
        _body.Controls.Add(cb);
        GainSlider(_y, entry?.GainDb ?? 0f, v => GetOrAddSource(kind, id, name).GainDb = v);
        _y += ROW;
    }

    /// <summary>
    /// dB gain at the right of a row: slider (−30 … +12, 0.5 dB steps) and a number box to type an
    /// exact value (arrows step 0.5 dB; type 0 to reset), kept in sync.
    /// </summary>
    private void GainSlider(int y, float db, Action<float> set)
    {
        int min = (int)(AudioSourceConfig.MinGainDb * GainTicksPerDb), max = (int)(AudioSourceConfig.MaxGainDb * GainTicksPerDb);
        // Fixed height: an auto-sized TrackBar mis-renders (thumb only) after DPI scaling.
        var tb = new TrackBar { Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false,
                                Location = new Point(GainX, y - 2), Size = new Size(GainWidth, 30),
                                Value = SliderTicks(db, min, max) };
        var num = new NumericUpDown { Location = new Point(GainValueX, y + 1), Width = GainNumberWidth,
                                      Minimum = (decimal)AudioSourceConfig.MinGainDb, Maximum = (decimal)AudioSourceConfig.MaxGainDb,
                                      Increment = 1m / GainTicksPerDb, DecimalPlaces = 1, TextAlign = HorizontalAlignment.Right,
                                      Value = (decimal)Math.Clamp(db, AudioSourceConfig.MinGainDb, AudioSourceConfig.MaxGainDb) };

        bool syncing = false;   // one control updating the other must not echo back
        void Apply(float v)
        {
            if (_loading) return;
            set(v);
            SaveCfg();
        }
        tb.Scroll += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            num.Value = tb.Value / (decimal)GainTicksPerDb;
            syncing = false;
            Apply(tb.Value / (float)GainTicksPerDb);
        };
        // Typed values keep their precision (e.g. 3.3 dB); the slider shows the nearest step.
        num.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            tb.Value = SliderTicks((float)num.Value, min, max);
            syncing = false;
            Apply((float)num.Value);
        };
        _body.Controls.Add(tb);
        _body.Controls.Add(num);
    }

    private static int SliderTicks(float db, int min, int max) => Math.Clamp((int)Math.Round(db * GainTicksPerDb), min, max);

    private AudioSourceConfig? FindSource(string kind, string id) =>
        _cfg.AudioSources.FirstOrDefault(x => string.Equals(x.Kind, kind, OIC) && string.Equals(x.Id, id, OIC));

    /// <summary>Config entry for a device row, created (unticked, 0 dB) on first change.</summary>
    private AudioSourceConfig GetOrAddSource(string kind, string id, string name)
    {
        var entry = FindSource(kind, id);
        if (entry is null)
        {
            entry = new AudioSourceConfig { Kind = kind, Id = id, Enabled = false };
            _cfg.AudioSources.Add(entry);
        }
        if (name.Length > 0 && id != AudioSourceConfig.DefaultId) entry.Name = name;
        return entry;
    }
}

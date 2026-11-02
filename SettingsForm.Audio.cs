using System.Drawing;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// Audio-mirror section of the settings window: the master switch in the header, then one checkbox
/// row per capturable device, grouped into output devices (loopback — what they play) and input
/// devices, each with its own gain slider plus a common gain per group (dB, additive).
/// </summary>
public sealed partial class SettingsForm
{
    private const int SourceIndent = 12;
    private const int SourceTextWidth = 290;
    private const int GainX = 330, GainWidth = 190, GainValueX = 528;
    private const int GainTicksPerDb = 2;   // 0.5 dB steps

    private void BuildAudio()
    {
        var s = BeginSection("audio", L.T("settings.audio_mirror"), _cfg.AudioMirror, v => _cfg.AudioMirror = v);
        Note(L.T("settings.audio_mirror.note"));

        // Where the mix goes: automatic (target monitor's HDMI/DP audio) or a fixed device.
        var autoIds = AudioDevices.TargetEndpointIds(_cfg.TargetLabel);
        BuildOutputRow(autoIds);

        // The output endpoint itself is never offered as a source: capturing it would loop the mix.
        var targetIds = string.IsNullOrWhiteSpace(_cfg.AudioOutputId)
            ? autoIds : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _cfg.AudioOutputId };
        BuildAudioGroup(render: true, targetIds);
        BuildAudioGroup(render: false, targetIds);

        var refresh = new Button { Text = L.T("audio.src.refresh"), Location = new Point(LX, _y), AutoSize = true };
        refresh.Click += (_, _) => BeginInvoke(_rebuild);   // devices are listed once per window
        _body.Controls.Add(refresh);
        _y += 36;

        // Device names make this line long and it changes live → reserve room for a few wrapped
        // lines, measured in the actual font (a fixed pixel height clips at high display scaling).
        int statusHeight = ToLayout(TextRenderer.MeasureText("Ag", Font).Height) * StatusLines;
        _audioStatus = new Label { Location = new Point(LX, _y), AutoSize = false, AutoEllipsis = true,
                                   Size = new Size(NoteWidth, statusHeight) };
        _body.Controls.Add(_audioStatus);
        _y += statusHeight + 6;
        EndSection(s);
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

    /// <summary>dB slider (−30 … +12, 0.5 dB steps) with its value label, at the right of a row.</summary>
    private void GainSlider(int y, float db, Action<float> set)
    {
        int min = (int)(AudioSourceConfig.MinGainDb * GainTicksPerDb), max = (int)(AudioSourceConfig.MaxGainDb * GainTicksPerDb);
        // Fixed height: an auto-sized TrackBar mis-renders (thumb only) after DPI scaling.
        var tb = new TrackBar { Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false,
                                Location = new Point(GainX, y - 2), Size = new Size(GainWidth, 30),
                                Value = Math.Clamp((int)Math.Round(db * GainTicksPerDb), min, max) };
        var value = new Label { Location = new Point(GainValueX, y + 4), AutoSize = true, Text = FormatDb(db) };
        tb.Scroll += (_, _) =>
        {
            float v = tb.Value / (float)GainTicksPerDb;
            value.Text = FormatDb(v);
            if (_loading) return;
            set(v);
            SaveCfg();
        };
        _body.Controls.Add(tb);
        _body.Controls.Add(value);
    }

    private static string FormatDb(float db) => db.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB";

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

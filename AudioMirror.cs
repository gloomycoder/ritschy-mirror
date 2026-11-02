using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RitschyMirror;

public enum AudioMirrorState { Off, Running, Paused, Unavailable }

/// <summary>
/// Mixes the selected audio sources and plays the mix on the HDMI/DP audio endpoint of the mirror's
/// target monitor, so the capture card receives the sound (NAudio WASAPI).
///
/// • Sources (<c>audio_sources</c>, checkbox list in Settings): output devices are captured in
///   loopback (what they play — e.g. speakers plus a headset that Discord plays to), input devices
///   directly (e.g. a microphone). "default" entries follow the current Windows default device.
///   Gain is in dB and additive: effective = group gain (outputs / inputs) + the device's own gain.
/// • Target (output) is <c>audio_output_id</c> if one is chosen, otherwise the audio endpoint whose
///   device description equals the target monitor's EDID name — Windows names HDMI/DP audio
///   endpoints after the EDID. No match (or several identical monitors) → Unavailable.
/// • An output source that IS the target is skipped (it would loop the mirror back into itself); a
///   device selected twice (as "default" and by name) is captured once. If nothing is left because
///   the default output already is the target → <see cref="AudioMirrorState.Paused"/>.
/// • Device notifications (default changed, added/removed, state changed) re-run that decision live;
///   they arrive in bursts → debounced. Gain changes apply immediately without a restart.
///
/// • A failed (re)start — device not ready yet, held in exclusive mode by another app — is retried
///   with back-off (1 s → 30 s), because such a device may come back without any notification.
///
/// All audio-object work runs on one dedicated worker thread: Core Audio notification callbacks must
/// not block or call back into the audio stack, so they only signal the worker.
/// </summary>
public sealed class AudioMirror : IDisposable
{
    private const int DebounceMs = 300;
    private const int RetryMinMs = 1000, RetryMaxMs = 30000;
    private const int OutputLatencyMs = 30;
    private const int MixSampleRate = 48000;

    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _worker;
    private volatile bool _disposed;

    // Desired state (written by the engine, read by the worker) — guarded by _gate.
    private bool _enabled;
    private string _targetLabel = "";
    private List<AudioSourceConfig> _sources = new();
    private float _renderGainDb, _captureGainDb;
    private string _outputId = "", _outputName = "";
    private string _compositionKey = "";

    // Pipeline (built/torn down by the worker; the channel list is also read under _gate for gains).
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notify;
    private List<AudioSourceChannel> _channels = new();
    private WasapiPlayer? _out;
    private MMDevice? _outDevice;
    private string _runningKey = "";
    private volatile bool _faulted;

    // Published status; localized on read so a language switch applies immediately.
    private volatile AudioMirrorState _state = AudioMirrorState.Off;
    private List<LocalizedText> _status = new() { new("audio.status.off") };

    public AudioMirror(Action<string> log) => _log = log;

    public AudioMirrorState State => _state;

    public string StatusText
    {
        get
        {
            List<LocalizedText> parts;
            lock (_gate) parts = _status;
            return string.Join("  ·  ", parts.Select(p => p.Text));
        }
    }

    /// <summary>
    /// Request mirroring on/off for the target monitor (EDID name) with the audio settings of
    /// <paramref name="cfg"/> (sources, group gains, optional fixed output). Cheap and idempotent —
    /// called at render start, on every config reload and at stop. Pure gain changes are applied
    /// in place; anything else wakes the worker to rebuild.
    /// </summary>
    public void SetDesired(bool enabled, string targetLabel, MirrorConfig cfg)
    {
        if (_disposed) return;
        targetLabel = (targetLabel ?? "").Trim();
        var copy = cfg.AudioSources.Select(s => s.Clone()).ToList();
        string outputId = (cfg.AudioOutputId ?? "").Trim();
        string composition = $"{enabled}|{targetLabel}|{outputId}|" +
            string.Join(";", copy.Where(s => s.Enabled).Select(s => s.Key.ToLowerInvariant()));
        bool rebuild;
        lock (_gate)
        {
            rebuild = composition != _compositionKey;
            _enabled = enabled;
            _targetLabel = targetLabel;
            _sources = copy;
            _renderGainDb = cfg.AudioRenderGainDb;
            _captureGainDb = cfg.AudioCaptureGainDb;
            _outputId = outputId;
            _outputName = cfg.AudioOutputName ?? "";
            _compositionKey = composition;
            foreach (var ch in _channels) ch.SetGain(GainFor(ch.ConfigKey, ch.IsRender));
        }
        if (!rebuild) return;
        if (enabled && _worker is null)
        {
            _worker = new Thread(WorkerMain) { IsBackground = true, Name = "AudioMirror" };
            _worker.Start();
        }
        Signal();
    }

    /// <summary>Linear gain of a source: group dB + device dB (call under _gate).</summary>
    private float GainFor(string configKey, bool isRender)
    {
        var src = _sources.FirstOrDefault(s => string.Equals(s.Key, configKey, StringComparison.OrdinalIgnoreCase));
        float db = (src?.GainDb ?? 0f) + (isRender ? _renderGainDb : _captureGainDb);
        return AudioSourceConfig.DbToLinear(db);
    }

    // ── Worker ────────────────────────────────────────────────────────────
    private void WorkerMain()
    {
        try
        {
            int retryMs = 0;   // > 0: the last attempt failed → try again after this long
            while (!_disposed)
            {
                bool signalled = retryMs > 0 ? _wake.WaitOne(retryMs) : _wake.WaitOne();
                // Debounce: a default-device switch fires several notifications back to back.
                if (signalled) while (!_disposed && _wake.WaitOne(DebounceMs)) { }
                if (_disposed) break;
                try
                {
                    Reevaluate();
                    if (retryMs > 0) _log("Audio mirror: recovered.");
                    retryMs = 0;
                }
                catch (Exception ex)
                {
                    // E.g. the device isn't ready yet after an error, or another app holds it in
                    // exclusive mode. It may come back without any notification → retry on a timer.
                    retryMs = retryMs == 0 ? RetryMinMs : Math.Min(retryMs * 2, RetryMaxMs);
                    _log($"Audio mirror: start failed ({ex.Message}) — retrying in {retryMs / 1000} s.");
                    StopPipeline();
                    SetStatus(AudioMirrorState.Unavailable, new LocalizedText("audio.status.retrying", ex.Message, retryMs / 1000));
                }
            }
        }
        finally
        {
            StopPipeline();
            try { _notify?.Dispose(); } catch (Exception ex) { _log("Audio mirror: notify dispose: " + ex.Message); }
            try { _enumerator?.Dispose(); } catch (Exception ex) { _log("Audio mirror: enumerator dispose: " + ex.Message); }
            _notify = null;
            _enumerator = null;
        }
    }

    private sealed record Resolved(MMDevice Device, AudioSourceConfig Source);

    private void Reevaluate()
    {
        bool enabled; string label, outputId, outputName; List<AudioSourceConfig> sources;
        lock (_gate) { enabled = _enabled; label = _targetLabel; sources = _sources; outputId = _outputId; outputName = _outputName; }

        if (!enabled)
        {
            StopPipeline();
            SetStatus(AudioMirrorState.Off, new LocalizedText("audio.status.off"));
            return;
        }

        EnsureNotifications();
        var en = _enumerator!;
        MMDevice? dst = ResolveOutput(en, label, outputId, outputName);
        if (dst is null) return;   // status already set

        var resolved = new List<Resolved>();
        var skipped = new List<LocalizedText>();
        try
        {
            bool skippedTarget = ResolveSources(en, sources, dst.ID, resolved, skipped);
            if (resolved.Count == 0)
            {
                if (_out != null) _log("Audio mirror: no usable source left — stopped.");
                StopPipeline();
                if (skippedTarget) SetStatus(AudioMirrorState.Paused, new LocalizedText("audio.status.paused_same", dst.FriendlyName));
                else SetStatus(AudioMirrorState.Unavailable, WithSkipped(new LocalizedText("audio.status.no_sources"), skipped));
                return;
            }

            string runKey = dst.ID + "|" + string.Join(";", resolved.Select(r => r.Source.Key + "=" + r.Device.ID));
            var running = new LocalizedText("audio.status.running",
                string.Join(", ", resolved.Select(r => r.Device.FriendlyName)), dst.FriendlyName);
            if (_out != null && !_faulted && runKey == _runningKey)
            {
                SetStatus(AudioMirrorState.Running, WithSkipped(running, skipped));
                return; // already mirroring exactly this set
            }

            StopPipeline();
            var handOver = resolved.ToList();
            resolved.Clear();       // the pipeline owns these devices now (also on failure)
            var target = dst;
            dst = null;
            StartPipeline(handOver, target);
            _runningKey = runKey;
            SetStatus(AudioMirrorState.Running, WithSkipped(running, skipped));
        }
        finally
        {
            foreach (var r in resolved) r.Device.Dispose();
            dst?.Dispose();
        }
    }

    /// <summary>
    /// Configured sources → active devices. Skips disconnected devices and an output source that is
    /// the target itself; a device selected twice ("default" and by name) is used once — the
    /// "default" entry wins, so its gain applies. Returns true if a source was the target.
    /// </summary>
    private static bool ResolveSources(MMDeviceEnumerator en, List<AudioSourceConfig> sources, string targetId,
                                       List<Resolved> resolved, List<LocalizedText> skipped)
    {
        bool skippedTarget = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in sources.Where(s => s.Enabled).OrderByDescending(s => s.IsDefault))
        {
            var flow = src.IsRender ? DataFlow.Render : DataFlow.Capture;
            MMDevice? dev;
            if (src.IsDefault)
            {
                if (!en.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out dev))
                {
                    skipped.Add(new LocalizedText(src.IsRender ? "audio.reason.no_default_render" : "audio.reason.no_default_capture"));
                    continue;
                }
            }
            else
            {
                try { dev = en.GetDevice(src.Id); }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException) { dev = null; }
                if (dev is null || dev.State != DeviceState.Active)
                {
                    dev?.Dispose();
                    skipped.Add(new LocalizedText("audio.reason.not_connected", src.Name.Length > 0 ? src.Name : src.Id));
                    continue;
                }
            }

            if (src.IsRender && string.Equals(dev.ID, targetId, StringComparison.OrdinalIgnoreCase))
            {
                skippedTarget = true;
                skipped.Add(new LocalizedText("audio.reason.same_as_target", dev.FriendlyName));
                dev.Dispose();
                continue;
            }
            if (!seen.Add($"{flow}:{dev.ID}")) { dev.Dispose(); continue; }
            resolved.Add(new Resolved(dev, src));
        }
        return skippedTarget;
    }

    private static List<LocalizedText> WithSkipped(LocalizedText main, List<LocalizedText> skipped)
    {
        var parts = new List<LocalizedText> { main };
        if (skipped.Count > 0) parts.Add(new LocalizedText("audio.status.skipped", new JoinedText(skipped)));
        return parts;
    }

    private void Unavailable(LocalizedText reason)
    {
        if (_out != null) _log($"Audio mirror stopped: {reason.English}");
        StopPipeline();
        SetStatus(AudioMirrorState.Unavailable, reason);
    }

    private void EnsureNotifications()
    {
        if (_enumerator != null) return;
        _enumerator = new MMDeviceEnumerator();
        // Raised on the Core Audio worker thread — only signal, never touch audio objects here.
        _notify = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notify.DefaultDeviceChanged += (_, _) => Signal();
        _notify.DeviceAdded += (_, _) => Signal();
        _notify.DeviceRemoved += (_, _) => Signal();
        _notify.DeviceStateChanged += (_, _) => Signal();
    }

    /// <summary>
    /// The endpoint the mix is played on: the fixed <c>audio_output_id</c> if one is chosen, otherwise
    /// the target monitor's HDMI/DP endpoint found by EDID name. null = unavailable (status set).
    /// </summary>
    private MMDevice? ResolveOutput(MMDeviceEnumerator en, string label, string outputId, string outputName)
    {
        if (outputId.Length > 0)
        {
            MMDevice? fixedOut;
            try { fixedOut = en.GetDevice(outputId); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException) { fixedOut = null; }
            if (fixedOut is { State: DeviceState.Active }) return fixedOut;
            fixedOut?.Dispose();
            Unavailable(new LocalizedText("audio.status.output_not_found", outputName.Length > 0 ? outputName : outputId));
            return null;
        }

        if (label.Length == 0) { Unavailable(new LocalizedText("audio.status.no_target_name")); return null; }
        var targets = FindTargetEndpoints(en, label);
        if (targets.Count == 1) return targets[0];
        foreach (var t in targets) t.Dispose();
        Unavailable(new LocalizedText(targets.Count == 0 ? "audio.status.target_not_found" : "audio.status.target_ambiguous", label));
        return null;
    }

    /// <summary>Active render endpoints whose device description is the monitor's EDID name.</summary>
    private static List<MMDevice> FindTargetEndpoints(MMDeviceEnumerator en, string label)
    {
        var result = new List<MMDevice>();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (AudioEndpointMatcher.Matches(EndpointDescription(d), d.FriendlyName, label)) result.Add(d);
            else d.Dispose();
        }
        return result;
    }

    internal static string EndpointDescription(MMDevice d)
    {
        try
        {
            var props = d.Properties;
            return props.Contains(PropertyKeys.PKEY_Device_DeviceDesc)
                ? props[PropertyKeys.PKEY_Device_DeviceDesc].Value as string ?? ""
                : "";
        }
        catch (Exception) { return ""; } // property store unavailable → match on FriendlyName only
    }

    // ── Pipeline ──────────────────────────────────────────────────────────
    /// <summary>Sources → (per-source capture, stereo, 48 kHz, gain) → mixer → target endpoint.
    /// Takes over every device passed in, also when it throws.</summary>
    private void StartPipeline(List<Resolved> sources, MMDevice dst)
    {
        _faulted = false;
        var mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, 2);
        var mixer = new MixingSampleProvider(mixFormat) { ReadFully = true };   // silence when idle
        var channels = new List<AudioSourceChannel>();
        WasapiPlayer? player = null;
        int handed = 0;
        try
        {
            foreach (var r in sources)
            {
                float gain;
                lock (_gate) gain = GainFor(r.Source.Key, r.Source.IsRender);
                handed++;   // the channel owns the device from here on, also if its constructor throws
                var ch = new AudioSourceChannel(r.Device, r.Source.IsRender, r.Source.Key, gain, mixFormat);
                ch.Stopped += OnPipelineStopped;
                channels.Add(ch);
                mixer.AddMixerInput(ch.Output);
            }
            // Shared mode + AUTOCONVERTPCM: Windows adapts the 48 kHz stereo mix to the endpoint.
            player = new WasapiPlayerBuilder().WithDevice(dst).WithSharedMode()
                .WithLatency(OutputLatencyMs).WithMmcssThreadPriority().Build();
            player.Init(mixer.ToWaveProvider());
            player.PlaybackStopped += OnPipelineStopped;
            player.Play();
            foreach (var ch in channels) ch.Start();
        }
        catch
        {
            foreach (var ch in channels) ch.Dispose();
            if (handed > channels.Count) sources[handed - 1].Device.Dispose();   // constructor threw
            foreach (var r in sources.Skip(handed)) r.Device.Dispose();
            player?.Dispose();
            dst.Dispose();
            throw;
        }

        lock (_gate) _channels = channels;
        _out = player;
        _outDevice = dst;
        foreach (var ch in channels)
            _log($"Audio mirror source: \"{ch.Name}\" ({(ch.IsRender ? "loopback" : "input")}, {ch.CaptureFormat}).");
        _log($"Audio mirror running: {channels.Count} source(s) → \"{dst.FriendlyName}\".");
    }

    private void StopPipeline()
    {
        List<AudioSourceChannel> channels;
        lock (_gate) { channels = _channels; _channels = new(); }
        var player = _out;
        _out = null;
        _runningKey = "";
        foreach (var ch in channels)
        {
            ch.Stopped -= OnPipelineStopped;
            try { ch.Dispose(); } catch (Exception ex) { _log("Audio mirror: stop source: " + ex.Message); }
        }
        if (player != null)
        {
            player.PlaybackStopped -= OnPipelineStopped;
            try { player.Stop(); } catch (Exception ex) { _log("Audio mirror: stop output: " + ex.Message); }
            player.Dispose();
        }
        _outDevice?.Dispose();
        _outDevice = null;
    }

    /// <summary>Unexpected stop (device unplugged, exclusive-mode app took the device …) → re-evaluate.</summary>
    private void OnPipelineStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) _log("Audio mirror stream stopped: " + e.Exception.Message);
        _faulted = true;
        Signal();
    }

    private void SetStatus(AudioMirrorState state, LocalizedText part) => SetStatus(state, new List<LocalizedText> { part });

    private void SetStatus(AudioMirrorState state, List<LocalizedText> parts)
    {
        lock (_gate) _status = parts;
        _state = state;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Signal();
        // Only release the event once the worker is gone; a late device notification or a
        // stuck worker must never hit a disposed handle (it would take the process down).
        if (_worker is null || _worker.Join(TimeSpan.FromSeconds(3))) _wake.Dispose();
    }

    /// <summary>Wake the worker; safe from any thread, also during/after shutdown.</summary>
    private void Signal()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    /// <summary>Comma list of localized texts, rendered in the current language when shown.</summary>
    private sealed class JoinedText
    {
        private readonly List<LocalizedText> _items;
        public JoinedText(List<LocalizedText> items) => _items = items;
        public override string ToString() => string.Join(", ", _items.Select(i => i.Text));
    }
}

/// <summary>Name matching between a monitor's EDID name and an audio endpoint.</summary>
public static class AudioEndpointMatcher
{
    /// <summary>
    /// True if the endpoint belongs to the monitor named <paramref name="edidName"/>: its device
    /// description equals the name, or (description unavailable) its friendly name is
    /// "&lt;name&gt; (&lt;adapter&gt;)".
    /// </summary>
    public static bool Matches(string description, string friendlyName, string edidName)
    {
        if (string.IsNullOrWhiteSpace(edidName)) return false;
        string name = edidName.Trim();
        if (!string.IsNullOrWhiteSpace(description))
            return string.Equals(description.Trim(), name, StringComparison.OrdinalIgnoreCase);
        return (friendlyName ?? "").Trim().StartsWith(name + " (", StringComparison.OrdinalIgnoreCase);
    }
}

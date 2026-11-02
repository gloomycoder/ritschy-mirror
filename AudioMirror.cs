using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RitschyMirror;

public enum AudioMirrorState { Off, Running, Paused, Unavailable }

/// <summary>
/// Copies the Windows default playback device to the HDMI/DP audio endpoint of the mirror's
/// target monitor (WASAPI loopback → WASAPI shared-mode render, via NAudio's WasapiRecorder/WasapiPlayer).
///
/// • Source is always the current default playback device (role Multimedia).
/// • Target is the audio endpoint whose device description equals the target monitor's EDID name —
///   Windows names HDMI/DP audio endpoints after the EDID, e.g. "LG ULTRAGEAR (NVIDIA High Definition
///   Audio)". No match (or several identical monitors) → <see cref="AudioMirrorState.Unavailable"/>.
/// • Default device == target → <see cref="AudioMirrorState.Paused"/> (the sound already plays there;
///   mirroring would loop it back onto itself).
/// • Device notifications (default changed, added/removed, state changed) re-run that decision live,
///   so switching the default from the HDMI monitor to a headset starts the mirror and switching back
///   pauses it. Notifications arrive in bursts → debounced.
///
/// All audio-object work runs on one dedicated worker thread: Core Audio notification callbacks must
/// not block or call back into the audio stack, so they only signal the worker.
///
/// Clock drift: the two devices run on independent clocks, so the buffer between them slowly fills or
/// drains. Overflow is trimmed back to <see cref="TargetLatencyMs"/> (one short, rare skip); underrun is
/// padded with silence by <see cref="BufferedWaveProvider.ReadFully"/>.
/// </summary>
public sealed class AudioMirror : IDisposable
{
    private const int DebounceMs = 300;
    private const int OutputLatencyMs = 30;     // WASAPI render buffer
    private const int TargetLatencyMs = 40;     // queue depth to trim back to
    private const int MaxLatencyMs = 120;       // queue depth that triggers a trim
    private const int QueueCapacityMs = 500;

    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _worker;
    private volatile bool _disposed;

    // Desired state (written by the engine, read by the worker).
    private bool _enabled;
    private string _targetLabel = "";

    // Worker-thread-only objects.
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notify;
    private WasapiRecorder? _capture;
    private WasapiPlayer? _out;
    private BufferedWaveProvider? _queue;
    private byte[] _trimScratch = Array.Empty<byte>();
    private string _srcId = "", _dstId = "";
    private volatile bool _faulted;

    // Published status; text is localized on read so a language switch applies immediately.
    private volatile AudioMirrorState _state = AudioMirrorState.Off;
    private string _statusKey = "audio.status.off";
    private object?[] _statusArgs = Array.Empty<object?>();

    public AudioMirror(Action<string> log) => _log = log;

    public AudioMirrorState State => _state;

    public string StatusText
    {
        get
        {
            string key; object?[] args;
            lock (_gate) { key = _statusKey; args = _statusArgs; }
            return L.T(key, args);
        }
    }

    /// <summary>
    /// Request mirroring on/off for the given target monitor (EDID name). Cheap and idempotent —
    /// called at render start, on every config reload and at render stop.
    /// </summary>
    public void SetDesired(bool enabled, string targetLabel)
    {
        if (_disposed) return;
        targetLabel = (targetLabel ?? "").Trim();
        lock (_gate)
        {
            if (_enabled == enabled && _targetLabel == targetLabel) return;
            _enabled = enabled;
            _targetLabel = targetLabel;
        }
        if (enabled && _worker is null)
        {
            _worker = new Thread(WorkerMain) { IsBackground = true, Name = "AudioMirror" };
            _worker.Start();
        }
        Signal();
    }

    // ── Worker ────────────────────────────────────────────────────────────
    private void WorkerMain()
    {
        try
        {
            while (!_disposed)
            {
                _wake.WaitOne();
                // Debounce: a default-device switch fires several notifications back to back.
                while (!_disposed && _wake.WaitOne(DebounceMs)) { }
                if (_disposed) break;
                try { Reevaluate(); }
                catch (Exception ex)
                {
                    _log("Audio mirror: evaluation failed: " + ex.Message);
                    StopPipeline();
                    SetStatus(AudioMirrorState.Unavailable, "audio.status.error", ex.Message);
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

    private void Reevaluate()
    {
        bool enabled; string label;
        lock (_gate) { enabled = _enabled; label = _targetLabel; }

        if (!enabled)
        {
            StopPipeline();
            SetStatus(AudioMirrorState.Off, "audio.status.off");
            return;
        }

        EnsureNotifications();
        var en = _enumerator!;

        if (label.Length == 0)
        {
            Unavailable("audio.status.no_target_name");
            return;
        }
        if (!en.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var src))
        {
            Unavailable("audio.status.no_default");
            return;
        }

        using (src)
        {
            var matches = FindTargetEndpoints(en, label);
            try
            {
                if (matches.Count == 0) { Unavailable("audio.status.target_not_found", label); return; }
                if (matches.Count > 1) { Unavailable("audio.status.target_ambiguous", label); return; }
                var dst = matches[0];

                if (string.Equals(src.ID, dst.ID, StringComparison.OrdinalIgnoreCase))
                {
                    if (_capture != null) _log($"Audio mirror paused: default output is the target ({dst.FriendlyName}).");
                    StopPipeline();
                    SetStatus(AudioMirrorState.Paused, "audio.status.paused_same", dst.FriendlyName);
                    return;
                }

                bool same = _capture != null && !_faulted && _srcId == src.ID && _dstId == dst.ID;
                if (same) return; // already mirroring exactly this pair

                StopPipeline();
                StartPipeline(src, dst);
                SetStatus(AudioMirrorState.Running, "audio.status.running", src.FriendlyName, dst.FriendlyName);
            }
            finally
            {
                foreach (var m in matches) m.Dispose();
            }
        }
    }

    private void Unavailable(string key, params object?[] args)
    {
        if (_capture != null) _log($"Audio mirror stopped: {key} {string.Join(", ", args)}");
        StopPipeline();
        SetStatus(AudioMirrorState.Unavailable, key, args);
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

    private static string EndpointDescription(MMDevice d)
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
    private void StartPipeline(MMDevice src, MMDevice dst)
    {
        _faulted = false;
        var capture = new WasapiRecorderBuilder()
            .WithDevice(src)
            .WithLoopbackCapture()
            .WithSharedMode()
            .WithMmcssThreadPriority()
            .Build();
        var queue = new BufferedWaveProvider(capture.WaveFormat, TimeSpan.FromMilliseconds(QueueCapacityMs))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true, // underrun (silence / drift) → pad with silence instead of stopping
        };
        WasapiPlayer output;
        try
        {
            output = new WasapiPlayerBuilder()
                .WithDevice(dst)
                .WithSharedMode()
                .WithLatency(OutputLatencyMs)
                .WithMmcssThreadPriority()
                .Build();
        }
        catch
        {
            capture.Dispose();
            throw;
        }
        try
        {
            // Shared mode + AUTOCONVERTPCM: Windows adapts sample rate and channel layout, so a
            // 48 kHz stereo source can feed a 44.1 kHz or multichannel HDMI endpoint.
            output.Init(queue);
            _queue = queue;   // before StartRecording, or the first packets are dropped
            capture.DataAvailable += OnCaptured;
            capture.RecordingStopped += OnPipelineStopped;
            output.PlaybackStopped += OnPipelineStopped;
            output.Play();
            capture.StartRecording();
        }
        catch
        {
            _queue = null;
            capture.Dispose();
            output.Dispose();
            throw;
        }

        _capture = capture;
        _out = output;
        _srcId = src.ID;
        _dstId = dst.ID;
        _log($"Audio mirror running: \"{src.FriendlyName}\" → \"{dst.FriendlyName}\" ({capture.WaveFormat}).");
    }

    private void StopPipeline()
    {
        var capture = _capture; var output = _out;
        _capture = null; _out = null; _queue = null;
        _srcId = ""; _dstId = "";
        if (capture != null)
        {
            capture.DataAvailable -= OnCaptured;
            capture.RecordingStopped -= OnPipelineStopped;
            try { capture.StopRecording(); } catch (Exception ex) { _log("Audio mirror: stop capture: " + ex.Message); }
            capture.Dispose();
        }
        if (output != null)
        {
            output.PlaybackStopped -= OnPipelineStopped;
            try { output.Stop(); } catch (Exception ex) { _log("Audio mirror: stop output: " + ex.Message); }
            output.Dispose();
        }
    }

    /// <summary>Capture thread: queue the block and trim accumulated drift latency.</summary>
    private void OnCaptured(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var queue = _queue;
        if (queue is null || buffer.IsEmpty) return;
        queue.AddSamples(buffer);

        var fmt = queue.WaveFormat;
        int maxBytes = BytesFor(fmt, MaxLatencyMs);
        int buffered = queue.BufferedBytes;
        if (buffered <= maxBytes) return;
        int drop = buffered - BytesFor(fmt, TargetLatencyMs);
        drop -= drop % fmt.BlockAlign;
        if (drop <= 0) return;
        if (_trimScratch.Length < drop) _trimScratch = new byte[drop];
        queue.Read(_trimScratch.AsSpan(0, drop)); // discard the oldest audio → latency back to target
    }

    private static int BytesFor(WaveFormat fmt, int ms)
    {
        int bytes = (int)((long)fmt.AverageBytesPerSecond * ms / 1000);
        return bytes - bytes % fmt.BlockAlign;
    }

    /// <summary>Unexpected stop (device unplugged, exclusive-mode app took the device …) → re-evaluate.</summary>
    private void OnPipelineStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) _log("Audio mirror stream stopped: " + e.Exception.Message);
        _faulted = true;
        Signal();
    }

    private void SetStatus(AudioMirrorState state, string key, params object?[] args)
    {
        lock (_gate) { _statusKey = key; _statusArgs = args; }
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

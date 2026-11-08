using System.Runtime.ExceptionServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RitschyMirror;

/// <summary>
/// One captured device feeding the audio-mirror mixer:
/// WASAPI capture (loopback for an output device, normal capture for an input device)
/// → queue → float → stereo (by channel mask) → drift compensation → mixer sample rate → gain.
///
/// Each device runs on its own clock, so the queue slowly fills or drains relative to the output;
/// <see cref="DriftCompensator"/> holds it at ~40 ms by resampling within ±0.5 %. Only if the
/// queue still runs away (playback stalled) is it cut back on the capture thread.
///
/// With "follow volume" on, an output-device channel applies whatever part of that device's Windows
/// volume and mute its loopback signal does not already contain (see <see cref="LoopbackVolume"/>:
/// a hardware volume and the mute — a software volume is already in the signal), updated live from
/// the endpoint's volume notifications. Input devices are left as Windows delivers them.
/// </summary>
internal sealed class AudioSourceChannel : IDisposable
{
    private const int EmergencyLatencyMs = 300;   // drift compensation normally keeps ~40 ms
    private const int QueueCapacityMs = 500;

    private readonly MMDevice _device;
    private readonly WasapiRecorder _recorder;
    private readonly BufferedWaveProvider _queue;
    private readonly VolumeSampleProvider _gain;
    private readonly DriftCompensator _drift;
    private byte[] _trimScratch = Array.Empty<byte>();
    private int _trims;   // emergency cuts since the last TakeStats()
    private readonly AudioEndpointVolume? _endpointVolume;   // output devices only
    private float _userGain;                 // group + device gain (linear)
    private volatile bool _follow;
    private volatile float _followFactor = 1f;   // device volume/mute as a linear factor
    private bool _hardwareVolume;                // else the volume is already in the loopback signal

    /// <summary>Device-level identity ("render:{endpoint id}") — used to drop duplicates.</summary>
    public string DeviceKey { get; }
    /// <summary>The config entry this channel came from (for live gain updates).</summary>
    public string ConfigKey { get; }
    public bool IsRender { get; }
    public string Name { get; }
    public ISampleProvider Output => _gain;
    public WaveFormat CaptureFormat => _recorder.WaveFormat;

    public event EventHandler<StoppedEventArgs>? Stopped;

    /// <param name="device">Taken over by the channel (disposed with it).</param>
    public AudioSourceChannel(MMDevice device, bool isRender, string configKey, float gain, bool followVolume, WaveFormat mixFormat)
    {
        _device = device;
        IsRender = isRender;
        ConfigKey = configKey;
        Name = device.FriendlyName;
        DeviceKey = $"{(isRender ? AudioSourceConfig.Render : AudioSourceConfig.Capture)}:{device.ID}";

        var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithMmcssThreadPriority();
        if (isRender) builder = builder.WithLoopbackCapture();   // what the output device plays
        _recorder = builder.Build();
        try
        {
            _queue = new BufferedWaveProvider(_recorder.WaveFormat, TimeSpan.FromMilliseconds(QueueCapacityMs))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            ISampleProvider s = _queue.ToSampleProvider();
            int mask = _recorder.WaveFormat is WaveFormatExtensible ext ? ext.ChannelMask : 0;
            s = s.WaveFormat.Channels switch
            {
                1 => s.ToStereo(),
                2 => s,
                _ => new DownmixToStereoSampleProvider(s, mask),
            };
            s = _drift = new DriftCompensator(s, _queue);
            if (s.WaveFormat.SampleRate != mixFormat.SampleRate)
                s = new WdlResamplingSampleProvider(s, mixFormat.SampleRate);
            _gain = new VolumeSampleProvider(s) { Volume = gain };
            _userGain = gain;
            if (isRender)
            {
                _endpointVolume = device.AudioEndpointVolume;   // owned (and disposed) by the device
                _hardwareVolume = LoopbackVolume.HasHardwareVolume((uint)_endpointVolume.HardwareSupport);
                _endpointVolume.OnVolumeNotification += OnDeviceVolume;
                _followFactor = ReadFollowFactor();
            }
            SetFollowVolume(followVolume);
        }
        catch
        {
            if (_endpointVolume != null) _endpointVolume.OnVolumeNotification -= OnDeviceVolume;
            _recorder.Dispose();
            throw;
        }
        _recorder.DataAvailable += OnCaptured;
        _recorder.RecordingStopped += OnStopped;
    }

    /// <summary>Linear gain; read by the playback thread, a plain float write is enough.
    /// Returns whether it changed.</summary>
    public bool SetGain(float linear)
    {
        bool changed = linear != _userGain;
        _userGain = linear;
        ApplyGain();
        return changed;
    }

    /// <summary>"gain +6.0 dB, follows volume -10.6 dB → -4.6 dB" for logs: what is really applied.</summary>
    public string GainText
    {
        get
        {
            float user = _userGain, total = user * (_follow ? _followFactor : 1f);
            string Db(float v) => v <= 0 ? "-inf dB" : $"{20 * Math.Log10(v):+0.0;-0.0;0.0} dB";
            return $"gain {Db(user)}" + (_follow ? $", {FollowText} → {Db(total)}" : "");
        }
    }

    /// <summary>Apply (or ignore) the captured output device's Windows volume and mute.</summary>
    public bool SetFollowVolume(bool follow)
    {
        bool before = _follow;
        _follow = follow && _endpointVolume != null;
        ApplyGain();
        return before != _follow;
    }

    /// <summary>"follows volume -12.3 dB" / "… muted" / software-volume note for logs; "" when not following.</summary>
    public string FollowText
    {
        get
        {
            if (!_follow) return "";
            float f = _followFactor;
            if (f <= 0) return "follows volume: muted";
            return _hardwareVolume ? $"follows volume {20 * Math.Log10(f):0.0} dB"
                                   : "follows mute (software volume is already in the signal)";
        }
    }

    private void ApplyGain() => _gain.Volume = _userGain * (_follow ? _followFactor : 1f);

    /// <summary>Device volume changed (any thread) → follow it immediately.</summary>
    private void OnDeviceVolume(AudioVolumeNotificationData data)
    {
        try
        {
            _followFactor = ReadFollowFactor();
            ApplyGain();
        }
        catch (Exception) { /* device going away — the pipeline is about to be rebuilt */ }
    }

    /// <summary>The device's current volume/mute as the factor the loopback signal still needs
    /// (dB rather than the slider scalar, so the mirror changes by what you actually hear).</summary>
    private float ReadFollowFactor()
    {
        var v = _endpointVolume!;
        return LoopbackVolume.Factor((uint)v.HardwareSupport, v.Mute, v.MasterVolumeLevelScalar,
                                     v.MasterVolumeLevel, v.VolumeRange.MaxDecibels);
    }

    public void Start() => _recorder.StartRecording();

    /// <summary>Capture thread: queue the block; cut back only a runaway queue (playback stalled).</summary>
    private void OnCaptured(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.IsEmpty) return;
        _queue.AddSamples(buffer);

        var fmt = _queue.WaveFormat;
        int buffered = _queue.BufferedBytes;
        if (buffered <= BytesFor(fmt, EmergencyLatencyMs)) return;
        int drop = buffered - BytesFor(fmt, (int)DriftCompensator.TargetLatencyMs);
        drop -= drop % fmt.BlockAlign;
        if (drop <= 0) return;
        if (_trimScratch.Length < drop) _trimScratch = new byte[drop];
        _queue.Read(_trimScratch.AsSpan(0, drop)); // discard the oldest audio → latency back to target
        Interlocked.Increment(ref _trims);
    }

    /// <summary>Soak figures since the previous call: drift compensation + emergency cuts.</summary>
    public (DriftStats Drift, int Trims) TakeStats() => (_drift.TakeStats(), Interlocked.Exchange(ref _trims, 0));

    private static int BytesFor(WaveFormat fmt, int ms)
    {
        int bytes = (int)((long)fmt.AverageBytesPerSecond * ms / 1000);
        return bytes - bytes % fmt.BlockAlign;
    }

    private void OnStopped(object? sender, StoppedEventArgs e) => Stopped?.Invoke(this, e);

    public void Dispose()
    {
        _recorder.DataAvailable -= OnCaptured;
        _recorder.RecordingStopped -= OnStopped;
        if (_endpointVolume != null) _endpointVolume.OnVolumeNotification -= OnDeviceVolume;
        // Release every part even if one throws (a vanished device fails in odd places);
        // the first error goes to the caller, which logs it.
        Exception? first = null;
        try { _recorder.StopRecording(); }
        catch (InvalidOperationException) { /* never started / already stopped */ }
        catch (Exception ex) { first ??= ex; }
        try { _recorder.Dispose(); } catch (Exception ex) { first ??= ex; }
        try { _device.Dispose(); } catch (Exception ex) { first ??= ex; }
        if (first != null) ExceptionDispatchInfo.Capture(first).Throw();
    }
}

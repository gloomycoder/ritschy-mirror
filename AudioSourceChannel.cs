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
/// Loopback captures an output device's mix BEFORE its Windows volume and mute, so turning the
/// speakers down would not change the mirror. With "follow volume" on, an output-device channel
/// applies that device's attenuation (dB relative to its maximum) and mute on top of the user gain,
/// updated live from the endpoint's volume notifications. Input devices need nothing: Windows
/// applies a microphone's input level and mute before any app receives the signal.
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

    /// <summary>Linear gain; read by the playback thread, a plain float write is enough.</summary>
    public void SetGain(float linear)
    {
        _userGain = linear;
        ApplyGain();
    }

    /// <summary>Apply (or ignore) the captured output device's Windows volume and mute.</summary>
    public void SetFollowVolume(bool follow)
    {
        _follow = follow && _endpointVolume != null;
        ApplyGain();
    }

    /// <summary>"follows volume -12.3 dB" / "follows volume: muted" for logs; "" when not following.</summary>
    public string FollowText
    {
        get
        {
            if (!_follow) return "";
            float f = _followFactor;
            return f <= 0 ? "follows volume: muted" : $"follows volume {20 * Math.Log10(f):0.0} dB";
        }
    }

    private void ApplyGain() => _gain.Volume = _userGain * (_follow ? _followFactor : 1f);

    /// <summary>Device volume changed (any thread) → follow it immediately.</summary>
    private void OnDeviceVolume(AudioVolumeNotificationData data)
    {
        try
        {
            _followFactor = data.Muted || data.MasterVolume <= 0 ? 0f : ReadFollowFactor();
            ApplyGain();
        }
        catch (Exception) { /* device going away — the pipeline is about to be rebuilt */ }
    }

    /// <summary>
    /// Attenuation the device applies, as a linear factor: its level in dB relative to its maximum
    /// (100 % = 1.0, so a device with a +dB range never boosts the mirror), 0 when muted or at 0 %.
    /// dB rather than the slider scalar, so the mirror changes by what you actually hear.
    /// </summary>
    private float ReadFollowFactor()
    {
        var v = _endpointVolume!;
        if (v.Mute || v.MasterVolumeLevelScalar <= 0) return 0f;
        double db = v.MasterVolumeLevel - v.VolumeRange.MaxDecibels;
        return (float)Math.Pow(10, Math.Min(0, db) / 20);
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

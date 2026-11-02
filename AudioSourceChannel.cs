using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RitschyMirror;

/// <summary>
/// One captured device feeding the audio-mirror mixer:
/// WASAPI capture (loopback for an output device, normal capture for an input device)
/// → queue → float → stereo → mixer sample rate → gain (group gain × device gain).
///
/// Each device runs on its own clock, so the queue slowly fills or drains relative to the output.
/// Overflow is trimmed back to <see cref="TargetLatencyMs"/> on the capture thread (one short skip,
/// rarely); an underrun reads as silence (<see cref="BufferedWaveProvider.ReadFully"/>).
/// </summary>
internal sealed class AudioSourceChannel : IDisposable
{
    private const int TargetLatencyMs = 40;
    private const int MaxLatencyMs = 120;
    private const int QueueCapacityMs = 500;

    private readonly MMDevice _device;
    private readonly WasapiRecorder _recorder;
    private readonly BufferedWaveProvider _queue;
    private readonly VolumeSampleProvider _gain;
    private byte[] _trimScratch = Array.Empty<byte>();

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
    public AudioSourceChannel(MMDevice device, bool isRender, string configKey, float gain, WaveFormat mixFormat)
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
            s = s.WaveFormat.Channels switch
            {
                1 => s.ToStereo(),
                2 => s,
                _ => new DownmixToStereoSampleProvider(s),
            };
            if (s.WaveFormat.SampleRate != mixFormat.SampleRate)
                s = new WdlResamplingSampleProvider(s, mixFormat.SampleRate);
            _gain = new VolumeSampleProvider(s) { Volume = gain };
        }
        catch
        {
            _recorder.Dispose();
            throw;
        }
        _recorder.DataAvailable += OnCaptured;
        _recorder.RecordingStopped += OnStopped;
    }

    /// <summary>Linear gain; read by the playback thread, a plain float write is enough.</summary>
    public void SetGain(float linear) => _gain.Volume = linear;

    public void Start() => _recorder.StartRecording();

    /// <summary>Capture thread: queue the block and trim accumulated drift latency.</summary>
    private void OnCaptured(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.IsEmpty) return;
        _queue.AddSamples(buffer);

        var fmt = _queue.WaveFormat;
        int buffered = _queue.BufferedBytes;
        if (buffered <= BytesFor(fmt, MaxLatencyMs)) return;
        int drop = buffered - BytesFor(fmt, TargetLatencyMs);
        drop -= drop % fmt.BlockAlign;
        if (drop <= 0) return;
        if (_trimScratch.Length < drop) _trimScratch = new byte[drop];
        _queue.Read(_trimScratch.AsSpan(0, drop)); // discard the oldest audio → latency back to target
    }

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
        try { _recorder.StopRecording(); }
        catch (InvalidOperationException) { /* never started / already stopped */ }
        _recorder.Dispose();
        _device.Dispose();
    }
}

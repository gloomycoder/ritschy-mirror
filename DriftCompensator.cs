using NAudio.Wave;

namespace RitschyMirror;

/// <summary>
/// Keeps one audio source's queue at a steady latency by resampling it very slightly faster or
/// slower — instead of throwing away chunks of audio when the two devices' clocks drift apart.
///
/// The capture device and the HDMI output run on independent clocks (typically tens of ppm apart),
/// so the queue between them slowly fills or drains. Every read measures the queue (smoothed) and
/// sets the playback ratio to 1 ± (excess latency × gain), capped at ±0.5 % — far below anything
/// audible, but enough to pull 40 ms of excess back in ~8 s. Interpolation is 4-point Hermite.
///
/// Loopback capture sends nothing while the device is silent, so after a pause the queue is empty:
/// the compensator then outputs silence ("priming") until the queue holds the target latency again,
/// rather than starting with a half-empty queue that would crackle.
/// </summary>
internal sealed class DriftCompensator : ISampleProvider
{
    public const double TargetLatencyMs = 40;
    private const double RatioPerMs = 0.0001;     // 10 ms over target → +0.1 %
    private const double MaxRatioDeviation = 0.005;
    private const double Smoothing = 0.05;        // EMA weight per read (~every 10 ms)
    private const int MinReadFrames = 256;         // upstream read granularity

    private readonly ISampleProvider _source;
    private readonly BufferedWaveProvider _queue;
    private readonly int _channels;

    private float[] _in = Array.Empty<float>();   // interleaved input window
    private int _inFrames;                        // valid frames in _in
    private double _pos;                          // read position in _in (frames)
    private bool _priming = true;
    private double _avgMs = TargetLatencyMs;

    /// <summary>Current playback ratio (input frames per output frame), for diagnostics.</summary>
    public double Ratio { get; private set; } = 1.0;

    /// <param name="source">Float stream read from <paramref name="queue"/> (same sample rate).</param>
    public DriftCompensator(ISampleProvider source, BufferedWaveProvider queue)
    {
        _source = source;
        _queue = queue;
        _channels = source.WaveFormat.Channels;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        int outFrames = buffer.Length / _channels;
        double queuedMs = _queue.BufferedDuration.TotalMilliseconds;
        double neededMs = outFrames * 1000.0 / WaveFormat.SampleRate;

        if (_priming)
        {
            if (queuedMs < TargetLatencyMs) { buffer.Clear(); return buffer.Length; }
            _priming = false;
            _inFrames = 0; _pos = 1;   // fresh start: one frame of history is primed below
            _avgMs = queuedMs;
        }
        else if (queuedMs < neededMs)
        {
            // Underrun (source went silent / stalled): pause cleanly and re-prime to the target.
            _priming = true;
            buffer.Clear();
            return buffer.Length;
        }

        _avgMs += Smoothing * (queuedMs - _avgMs);
        double ratio = 1.0 + Math.Clamp((_avgMs - TargetLatencyMs) * RatioPerMs, -MaxRatioDeviation, MaxRatioDeviation);
        Ratio = ratio;

        for (int f = 0; f < outFrames; f++)
        {
            // EnsureFrames may slide the window (and _pos) — take the index afterwards.
            if (!EnsureFrames((int)_pos + 3)) { buffer.Slice(f * _channels).Clear(); break; }
            int i0 = (int)_pos;
            float t = (float)(_pos - i0);
            for (int c = 0; c < _channels; c++)
            {
                float ym1 = _in[(i0 - 1) * _channels + c], y0 = _in[i0 * _channels + c];
                float y1 = _in[(i0 + 1) * _channels + c], y2 = _in[(i0 + 2) * _channels + c];
                buffer[f * _channels + c] = Hermite(ym1, y0, y1, y2, t);
            }
            _pos += ratio;
        }
        return buffer.Length;
    }

    /// <summary>Make frames [0, count) available, sliding the window (keeps one frame of history).</summary>
    private bool EnsureFrames(int count)
    {
        if (count <= _inFrames) return true;
        int keepFrom = Math.Min(Math.Max(0, (int)_pos - 1), _inFrames);
        int keep = _inFrames - keepFrom;
        if (keepFrom > 0 && keep > 0) Array.Copy(_in, keepFrom * _channels, _in, 0, keep * _channels);
        _inFrames = keep;
        _pos -= keepFrom;
        count -= keepFrom;
        if (_inFrames == 0)
        {
            // Fresh start: one silent history frame in front of the first real sample.
            EnsureCapacity(1);
            Array.Clear(_in, 0, _channels);
            _inFrames = 1;
        }

        int want = Math.Max(count - _inFrames, MinReadFrames);
        EnsureCapacity(_inFrames + want);
        int got = _source.Read(_in.AsSpan(_inFrames * _channels, want * _channels)) / _channels;
        _inFrames += got;
        return count <= _inFrames;
    }

    private void EnsureCapacity(int frames)
    {
        if (_in.Length < frames * _channels) Array.Resize(ref _in, frames * _channels);
    }

    private static float Hermite(float ym1, float y0, float y1, float y2, float t)
    {
        float c1 = 0.5f * (y1 - ym1);
        float c2 = ym1 - 2.5f * y0 + 2f * y1 - 0.5f * y2;
        float c3 = 0.5f * (y2 - ym1) + 1.5f * (y0 - y1);
        return ((c3 * t + c2) * t + c1) * t + y0;
    }
}

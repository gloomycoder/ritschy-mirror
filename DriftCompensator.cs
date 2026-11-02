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
/// Loopback capture sends nothing while the device is silent, so a stream regularly stops and
/// restarts. On a restart the compensator outputs silence ("priming") until the queue holds the
/// target latency — but for at most <see cref="MaxPrimeWaitMs"/> once data has arrived, so a short
/// sound (a click) that never fills the target still plays. When the stream stops, whatever is left
/// in the queue is played out (padded with silence) before priming again; nothing is held back to
/// surface late in front of the next sound.
/// </summary>
/// <summary>Drift-compensation figures over one soak interval (see <see cref="DriftCompensator.TakeStats"/>).</summary>
/// <param name="Active">Audio was played (not only priming/silence) during the interval.</param>
/// <param name="Starts">Times playback (re)started after silence.</param>
internal readonly record struct DriftStats(bool Active, double AvgMs, double MinMs, double MaxMs,
    double Ratio, double MinRatio, double MaxRatio, int Starts);

internal sealed class DriftCompensator : ISampleProvider
{
    public const double TargetLatencyMs = 40;
    private const double RatioPerMs = 0.0001;     // 10 ms over target → +0.1 %
    private const double MaxRatioDeviation = 0.005;
    private const double Smoothing = 0.05;        // EMA weight per read (~every 10 ms)
    private const int MinReadFrames = 256;         // upstream read granularity
    private const double MaxPrimeWaitMs = TargetLatencyMs;   // start anyway after this long
    private const int LookAheadFrames = 3;                    // Hermite needs i0-1 … i0+2

    private readonly ISampleProvider _source;
    private readonly BufferedWaveProvider _queue;
    private readonly int _channels;

    private float[] _in = Array.Empty<float>();   // interleaved input window
    private int _inFrames;                        // valid frames in _in
    private int _realEnd;                         // frames before this index are real audio (rest: padding)
    private double _pos;                          // read position in _in (frames)
    private bool _priming = true;
    private double _primeWaitMs;                  // time spent priming since data arrived
    private double _avgMs = TargetLatencyMs;

    // Soak statistics since the last TakeStats() (playback thread writes, a logger reads).
    private readonly object _statsGate = new();
    private double _minMs = double.MaxValue, _maxMs, _minRatio = double.MaxValue, _maxRatio;
    private int _starts;

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
            if (queuedMs <= 0) { _primeWaitMs = 0; buffer.Clear(); return buffer.Length; }
            _primeWaitMs += neededMs;
            if (queuedMs < TargetLatencyMs && _primeWaitMs < MaxPrimeWaitMs) { buffer.Clear(); return buffer.Length; }
            _priming = false;
            _primeWaitMs = 0;
            _inFrames = 0; _realEnd = 0; _pos = 1;   // fresh start: one frame of history is primed below
            _avgMs = queuedMs;
            lock (_statsGate) _starts++;
        }

        _avgMs += Smoothing * (queuedMs - _avgMs);
        double ratio = 1.0 + Math.Clamp((_avgMs - TargetLatencyMs) * RatioPerMs, -MaxRatioDeviation, MaxRatioDeviation);

        // Real audio left = still queued + already read into the window but not played yet. If that
        // doesn't cover this read (plus the interpolator's look-ahead), the source stopped or stalled:
        // play ALL of it out — padded with silence — then re-prime for the next sound.
        double realLeft = QueuedFrames() + Math.Max(0, _realEnd - _pos);
        bool draining = realLeft < outFrames * ratio + LookAheadFrames;
        if (draining) ratio = 1.0;
        else RecordStats(queuedMs, ratio);   // the tail of a sound would skew the range
        Ratio = ratio;

        for (int f = 0; f < outFrames; f++)
        {
            // EnsureFrames may slide the window (and _pos) — take the index afterwards.
            if (!EnsureFrames((int)_pos + LookAheadFrames, allowPadding: draining)) { buffer.Slice(f * _channels).Clear(); break; }
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
        if (draining && _pos >= _realEnd) _priming = true;   // every real sample has been played
        return buffer.Length;
    }

    private void RecordStats(double queuedMs, double ratio)
    {
        lock (_statsGate)
        {
            _minMs = Math.Min(_minMs, queuedMs); _maxMs = Math.Max(_maxMs, queuedMs);
            _minRatio = Math.Min(_minRatio, ratio); _maxRatio = Math.Max(_maxRatio, ratio);
        }
    }

    /// <summary>Figures since the previous call (resets them); safe from any thread.</summary>
    public DriftStats TakeStats()
    {
        lock (_statsGate)
        {
            bool active = _maxRatio > 0;
            var stats = new DriftStats(active, _avgMs, active ? _minMs : 0, _maxMs,
                Ratio, active ? _minRatio : 0, _maxRatio, _starts);
            _minMs = double.MaxValue; _maxMs = 0; _minRatio = double.MaxValue; _maxRatio = 0; _starts = 0;
            return stats;
        }
    }

    /// <summary>Whole frames waiting in the capture queue (same frame count as the converted stream).</summary>
    private int QueuedFrames() => _queue.BufferedBytes / _queue.WaveFormat.BlockAlign;

    /// <summary>
    /// Make frames [0, count) available, sliding the window (keeps one frame of history). Normally
    /// reads only what the queue really holds — the queue would pad a short read with zeros, i.e. a
    /// gap in the middle of the sound. <paramref name="allowPadding"/> (draining) reads past the end.
    /// </summary>
    private bool EnsureFrames(int count, bool allowPadding)
    {
        if (count <= _inFrames) return true;
        int keepFrom = Math.Min(Math.Max(0, (int)_pos - 1), _inFrames);
        int keep = _inFrames - keepFrom;
        if (keepFrom > 0 && keep > 0) Array.Copy(_in, keepFrom * _channels, _in, 0, keep * _channels);
        _inFrames = keep;
        _realEnd = Math.Max(0, _realEnd - keepFrom);
        _pos -= keepFrom;
        count -= keepFrom;
        if (_inFrames == 0)
        {
            // Fresh start: one silent history frame in front of the first real sample.
            EnsureCapacity(1);
            Array.Clear(_in, 0, _channels);
            _inFrames = 1;
            _realEnd = 1;
        }

        int queued = QueuedFrames();
        int want = Math.Max(count - _inFrames, MinReadFrames);
        if (!allowPadding) want = Math.Min(want, queued);
        if (want <= 0) return count <= _inFrames;
        EnsureCapacity(_inFrames + want);
        int got = _source.Read(_in.AsSpan(_inFrames * _channels, want * _channels)) / _channels;
        if (_realEnd == _inFrames) _realEnd += Math.Min(got, queued);   // real audio is contiguous
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

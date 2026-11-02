using System.Diagnostics;

namespace RitschyMirror;

/// <summary>
/// Events-per-second meter for the capture/output FPS readout. Counts ticks and publishes a
/// rate once per <see cref="WindowSeconds"/>; reading <see cref="Fps"/> is lock-free.
/// </summary>
public sealed class FpsCounter
{
    private const double WindowSeconds = 1.0;
    private long _windowStart = Stopwatch.GetTimestamp();
    private int _count;
    private double _fps;

    /// <summary>Last published rate (events per second).</summary>
    public double Fps => Volatile.Read(ref _fps);

    /// <summary>Count one event (a captured frame or a presented frame).</summary>
    public void Tick() => _count++;

    /// <summary>Publish a new rate if the window elapsed; returns true when <see cref="Fps"/> changed.</summary>
    public bool Update()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (elapsed < WindowSeconds) return false;
        Volatile.Write(ref _fps, _count / elapsed);
        _count = 0;
        _windowStart = now;
        return true;
    }

    public void Reset()
    {
        _count = 0;
        _windowStart = Stopwatch.GetTimestamp();
        Volatile.Write(ref _fps, 0);
    }
}

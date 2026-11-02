using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RitschyMirror;

/// <summary>
/// Paces the render loop to <c>target_fps</c> when VSync is off.
///
/// Uses a high-resolution waitable timer (Windows 10 1803+, ~0.5 ms accuracy) instead of
/// <c>Thread.Sleep</c>, whose 1–15.6 ms granularity would make e.g. 144 fps impossible. Older
/// systems fall back to a normal waitable timer. Deadlines advance on a fixed grid so the output
/// cadence stays even; if the loop falls more than one interval behind (stall, capture recovery),
/// the grid is re-anchored instead of bursting frames to "catch up".
/// </summary>
public sealed class FramePacer : IDisposable
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint INFINITE = 0xFFFFFFFF;
    // Upper bound for one wait slice: between slices the caller's abort check and message pump
    // run, so Stop(), closing the window and window messages stay responsive at very low fps.
    private const double MaxSliceMs = 50.0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, in long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _timer;
    private long _nextTicks;   // Stopwatch ticks of the next frame deadline; 0 = not anchored
    private int _fps;

    public FramePacer()
    {
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (_timer == IntPtr.Zero)
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
    }

    /// <summary>Forget the current deadline grid (after a pause, a config change or a disabled limit).</summary>
    public void Reset() => _nextTicks = 0;

    /// <summary>
    /// Block until the next frame slot for <paramref name="fps"/>, or until <paramref name="abort"/>
    /// returns true. <paramref name="betweenSlices"/> (e.g. the window message pump) runs between
    /// wait slices. Call once per loop iteration while pacing is active.
    /// </summary>
    public void WaitForNextFrame(int fps, Func<bool> abort, Action? betweenSlices = null)
    {
        if (fps <= 0) { Reset(); return; }
        long interval = Stopwatch.Frequency / fps;
        long now = Stopwatch.GetTimestamp();
        if (fps != _fps || _nextTicks == 0) { _fps = fps; _nextTicks = now + interval; }

        while (!abort())
        {
            now = Stopwatch.GetTimestamp();
            long remaining = _nextTicks - now;
            if (remaining <= 0) break;
            double ms = Math.Min(remaining * 1000.0 / Stopwatch.Frequency, MaxSliceMs);
            Sleep(ms);
            betweenSlices?.Invoke();
        }

        _nextTicks += interval;
        now = Stopwatch.GetTimestamp();
        if (now - _nextTicks > interval) _nextTicks = now + interval; // fell behind → re-anchor
    }

    private void Sleep(double ms)
    {
        if (_timer == IntPtr.Zero) { Thread.Sleep(Math.Max(1, (int)ms)); return; }
        long due = -(long)(ms * 10_000.0); // negative = relative, in 100 ns units
        if (due == 0) due = -1;
        if (!SetWaitableTimer(_timer, in due, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            Thread.Sleep(Math.Max(1, (int)ms));
            return;
        }
        WaitForSingleObject(_timer, INFINITE);
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero) { CloseHandle(_timer); _timer = IntPtr.Zero; }
    }
}

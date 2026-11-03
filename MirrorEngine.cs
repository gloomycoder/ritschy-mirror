using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace RitschyMirror;

/// <summary>An enumerated display (for GUI dropdowns + agent /displays).</summary>
public sealed class DisplayInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";        // GDI name, e.g. \\.\DISPLAY2
    public string Friendly { get; set; } = "";     // EDID name, e.g. "LG ULTRAGEAR" (empty = unknown)
    public string Key { get; set; } = "";          // stable identity (monitorDevicePath, otherwise fallback)
    public string Resolution { get; set; } = "";
    public bool Hdr { get; set; }
    public string Adapter { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }

    /// <summary>Windows places the primary monitor's desktop origin at (0,0).</summary>
    public bool IsPrimary => Left == 0 && Top == 0;

    /// <summary>Display name: prefers Friendly, otherwise the GDI name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Friendly) ? Name : Friendly;
}

/// <summary>
/// Controllable render engine: start/stop of the monitor mirror on its own thread
/// (own Win32 window + message pump + D3D loop). This used to be the content of
/// Program.Run(); now it can be started on demand (tray menu, local GUI, HTTP agent).
///
/// The configuration comes from mirror_config.json and is reloaded live in the loop
/// (LIVE keys). Structural changes (display/resolution/output_mode) need Restart().
/// </summary>
public sealed class MirrorEngine
{
    public string ConfigPath { get; }
    public string LogPath { get; }

    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _stop;
    private const int StopJoinTimeoutMs = 4000;
    // User-facing error, kept as key + args so the UI shows it in the current language.
    private volatile LocalizedText? _lastError;
    public string LastError => _lastError?.Text ?? "";

    /// <summary>Copies the default playback device to the target monitor's HDMI/DP audio (optional).</summary>
    public AudioMirror Audio { get; }

    // Capture / output frame rates of the running session (0 while stopped).
    private readonly FpsCounter _captureFps = new();
    private readonly FpsCounter _outputFps = new();
    // 0 while waiting/retrying: the counters still hold the last session's rates.
    public double CaptureFps => IsRunning && !_waiting ? _captureFps.Fps : 0;
    public double OutputFps => IsRunning && !_waiting ? _outputFps.Fps : 0;

    private volatile bool _unscaled;
    /// <summary>Running and the last frame was drawn 1:1 (scale_filter not in use); null when stopped.</summary>
    public bool? OutputUnscaled => IsRunning ? _unscaled : null;

    /// <summary>Audio-mirror status line for the UI, given whether audio_mirror is switched on.</summary>
    public string AudioStatusText(bool enabledInConfig)
    {
        if (!enabledInConfig) return L.T("audio.status.off");
        if (!IsRunning || Audio.State == AudioMirrorState.Off) return L.T("audio.status.idle");
        return Audio.StatusText;
    }

    /// <param name="configPath">mirror_config.json (see <see cref="AppPaths.MirrorConfig"/>).</param>
    /// <param name="logPath">This run's log file (see <see cref="AppPaths.LogFile"/>).</param>
    public MirrorEngine(string configPath, string logPath)
    {
        ConfigPath = configPath;
        LogPath = logPath;
        Audio = new AudioMirror(Log);
    }

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Raised for every log line, on the thread that logged (the settings log view listens).</summary>
    public event Action<string>? LogWritten;

    public void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
        LogWritten?.Invoke(line);
    }

    /// <summary>Session-setup detail (display list, steps): skipped during the quiet slow retries.</summary>
    private void SetupLog(string msg)
    {
        if (!_quietSetup) Log(msg);
    }

    // ── Control ─────────────────────────────────────────────────────────────
    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                // Stop() timed out and the old session is still tearing down. A second render thread
                // would share the stop flag, the output window and the audio mirror with it → refuse.
                if (_stop)
                {
                    _lastError = new LocalizedText("error.still_stopping");
                    Log("Start refused: " + _lastError.English);
                }
                return;
            }
            _stop = false;
            _lastError = null;
            _quietSetup = false;
            _waiting = false;
            _lastWaitReason = "";
            _thread = new Thread(RenderThreadMain) { IsBackground = true, Name = "MirrorRender" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { _stop = true; t = _thread; }
        if (t != null && t.IsAlive && Thread.CurrentThread != t && !t.Join(StopJoinTimeoutMs))
            Log($"WARNING: render thread did not stop within {StopJoinTimeoutMs} ms — Start is refused until it has finished.");
        // Keep a thread that is still alive: IsRunning stays true and Start() cannot overlap it.
        lock (_gate) { if (_thread is { IsAlive: false }) _thread = null; }
    }

    public void Restart() { Stop(); Start(); }

    // ── Stable monitor identity ──────────────────────────────────────────
    /// <summary>Stable key for a monitor: preferably the CCD DevicePath, otherwise the
    /// EDID name, otherwise the GDI name. Stored in the config as source_key/target_key.</summary>
    public static string MakeKey(string devicePath, string friendly, string gdiName)
    {
        if (!string.IsNullOrWhiteSpace(devicePath)) return "path:" + devicePath;
        if (!string.IsNullOrWhiteSpace(friendly)) return "edid:" + friendly;
        return "gdi:" + gdiName;
    }

    /// <summary>
    /// Resolves a saved monitor selection against the currently present displays.
    /// Order: exact key (DevicePath) → unique label (EDID name) → (only if NO
    /// identity is saved) the old index as a backward-compat fallback. If an identity is
    /// set but no monitor matches ⇒ idx=-1 + plain-text error, so the wrong screen is NOT
    /// silently mirrored.
    /// </summary>
    public static (int idx, LocalizedText? error) ResolveSelection(
        string key, string label, int index,
        IReadOnlyList<(string Key, string Label)> displays, bool isSource)
    {
        if (displays.Count == 0) return (-1, new LocalizedText("error.no_displays"));
        bool hasIdentity = !string.IsNullOrWhiteSpace(key) || !string.IsNullOrWhiteSpace(label);
        if (hasIdentity)
        {
            if (!string.IsNullOrWhiteSpace(key))
                for (int i = 0; i < displays.Count; i++)
                    if (string.Equals(displays[i].Key, key, StringComparison.OrdinalIgnoreCase)) return (i, null);
            if (!string.IsNullOrWhiteSpace(label))
            {
                int found = -1, count = 0;
                for (int i = 0; i < displays.Count; i++)
                    if (string.Equals(displays[i].Label, label, StringComparison.OrdinalIgnoreCase)) { found = i; count++; }
                if (count == 1) return (found, null);
            }
            string name = string.IsNullOrWhiteSpace(label) ? key : label;
            return (-1, new LocalizedText(isSource ? "error.source_not_connected" : "error.target_not_connected", name));
        }
        // Old config without identity → index. -1 = explicitly nothing selected (cleared by the GUI).
        if (index < 0)
            return (-1, new LocalizedText(isSource ? "error.no_source_selected" : "error.no_target_selected"));
        return (Math.Min(index, displays.Count - 1), null);
    }

    /// <summary>Pre-check (tray/GUI): can the configured source + target be resolved right now?
    /// null = ok, otherwise a plain-text error. Starts nothing.</summary>
    public LocalizedText? Preflight()
    {
        var displays = EnumerateDisplays();
        if (displays.Count == 0) return new LocalizedText("error.no_displays");
        var cfg = MirrorConfig.Load(ConfigPath);
        var ids = displays.Select(d => (Key: d.Key, Label: d.Friendly)).ToList();
        var (dIdx, de) = ResolveSelection(cfg.TargetKey, cfg.TargetLabel, cfg.TargetDisplay, ids, isSource: false);
        if (de != null) return de;
        if (displays[dIdx].IsPrimary && cfg.ResolveOutputMode() != "windowed")
            return new LocalizedText("error.target_is_primary");
        if (cfg.ResolveCaptureMode() != "monitor") return null;   // window source: any monitor may be the target
        var (si, se) = ResolveSelection(cfg.SourceKey, cfg.SourceLabel, cfg.SourceDisplay, ids, isSource: true);
        if (se != null) return se;
        var (di, _) = ResolveSelection(cfg.TargetKey, cfg.TargetLabel, cfg.TargetDisplay, ids, isSource: false);
        if (si == di) return new LocalizedText("error.same_source_target");
        return null;
    }

    // ── Render thread ───────────────────────────────────────────────────────
    /// <summary>Outcome of a render session: stopped cleanly, lost transiently (reinit),
    /// or fatal (config/selection error — a reinit would not help).</summary>
    private enum SessionResult { Stopped, Lost, Fatal, Waiting }

    // A session that mirrored this long without error counts as "ran healthy" → a
    // subsequent loss is transient and resets the reinit budget (only tight
    // failure chains count toward the fast-retry budget). Time-based, because frames are no
    // longer presented while the source is static or the frame limit is low.
    private const long HealthySessionMs = 5000;
    private const int MaxConsecutiveReinit = 8;      // fast attempts; after that: slow, quiet, endless
    private const int SlowReinitMs = 10_000;
    private const int SlowReinitLogEvery = 30;       // one "still lost" line per ~5 min
    private const int SecureDesktopRetryMs = 1000;   // DXGI: re-try the duplication while the desktop is away
    private const int AccessWaitTickMs = 50;         // loop tick while waiting (messages, config, Stop)
    private const int WaitForSourceMs = 1000;        // re-check a missing monitor/window (a few ms per check)

    // Slow retry mode: session setup doesn't log (it would repeat ~15 lines every 10 s).
    private volatile bool _quietSetup;
    private string _lastQuietError = "";
    private string _lastWaitReason = "";
    private volatile bool _waiting;

    /// <summary>Running, but not mirroring right now: waiting for a monitor/window to come back,
    /// or retrying after a loss (<see cref="LastError"/> says which).</summary>
    public bool IsWaiting => IsRunning && _waiting;

    /// <summary>Selection errors that clear up by themselves (a monitor plugged back in, the app
    /// started again): the engine waits for them. Everything else is a real configuration error.</summary>
    public static bool IsWaitable(LocalizedText? error) =>
        error?.Key is "error.source_not_connected" or "error.target_not_connected"
                   or "error.window_not_found" or "error.no_displays";

    // Render-loop cadence (wall clock, independent of the frame rate).
    private const int AcquireTimeoutMs = 16;     // unpaced: wait this long for a new source frame
    private const long ConfigPollMs = 250;       // mtime poll of mirror_config.json
    private const long HdrPollMs = 4000;         // re-read the source's HDR state
    private const long SoakLogMs = 60_000;       // long-run health line (rates, memory, audio drift)

    private void RenderThreadMain()
    {
        int consecutive = 0;
        while (!_stop)
        {
            SessionResult result;
            long healthyMs = 0;
            try
            {
                (result, healthyMs) = RunSession();
            }
            catch (Exception ex)
            {
                // Hard error during setup (e.g. device/capture creation) → treat it like a
                // loss and retry with backoff instead of killing the thread.
                _lastError = new LocalizedText("error.raw", ex.Message);
                if (!_quietSetup) Log("ERROR (render session): " + ex);
                else if (ex.Message != _lastQuietError) Log("ERROR (render session, retrying): " + ex.Message);
                _lastQuietError = ex.Message;
                result = SessionResult.Lost;
            }

            if (_stop || result == SessionResult.Stopped || result == SessionResult.Fatal)
                break;

            if (result == SessionResult.Waiting)
            {
                // A missing monitor/window isn't a failure: look again until it is back (matched by its
                // saved identity only — never another screen) or Stop(). Setup stays quiet meanwhile.
                consecutive = 0;
                _waiting = true;
                _quietSetup = true;
                InterruptibleSleep(WaitForSourceMs);
                continue;
            }

            // If the session ran healthy long enough beforehand, the loss was transient → reset the budget.
            if (healthyMs >= HealthySessionMs) { consecutive = 0; _quietSetup = false; _lastQuietError = ""; }
            consecutive++;

            // Never give up: the mirror runs in the background and a loss (GPU reset, mode change, a
            // device briefly gone…) can clear up any time. Exponential backoff (250 ms → 5 s) for the
            // first attempts, then a quiet retry every SlowReinitMs until it works or Stop().
            bool slow = consecutive > MaxConsecutiveReinit;
            int backoffMs = slow ? SlowReinitMs : Math.Min(5000, 250 * (1 << Math.Min(consecutive - 1, 4)));
            if (!slow)
                Log($"Mirroring lost — full reinit in {backoffMs} ms (attempt {consecutive}/{MaxConsecutiveReinit}).");
            else if ((consecutive - MaxConsecutiveReinit - 1) % SlowReinitLogEvery == 0)
                Log($"Mirroring still lost — retrying every {SlowReinitMs / 1000} s (attempt {consecutive}).");
            if (slow) _lastError = new LocalizedText("error.retrying", SlowReinitMs / 1000);
            _quietSetup = slow;
            _waiting = slow;
            InterruptibleSleep(backoffMs);
        }
        Audio.SetDesired(false, "", new MirrorConfig());  // audio keeps running across a reinit, but not past the mirror
        Log("Render thread finished.");
    }

    /// <summary>
    /// Soak log, once per <see cref="SoakLogMs"/>: frame rates, memory and handles over the interval,
    /// then the audio mirror's drift figures — a long run shows slow leaks or clock drift in the log.
    /// </summary>
    private void LogSoak(long intervalMs, long captured, long presented)
    {
        double sec = intervalMs / 1000.0;
        using var proc = Process.GetCurrentProcess();
        Log($"Soak video {sec:0} s: capture {captured / sec:0.0} fps, output {presented / sec:0.0} fps; " +
            $"private {proc.PrivateMemorySize64 >> 20} MB, GC heap {GC.GetTotalMemory(false) >> 20} MB, {proc.HandleCount} handles.");
        Audio.LogStats((int)Math.Round(sec));
    }

    /// <summary>Sleep that aborts immediately on Stop() (50 ms tick) — so a backoff does not
    /// delay the engine stop by seconds.</summary>
    private void InterruptibleSleep(int ms)
    {
        for (int slept = 0; slept < ms && !_stop; slept += 50)
            Thread.Sleep(Math.Min(50, ms - slept));
    }

    /// <summary>Quick attempt to set the source up again after a loss (ACCESS_LOST / frame pool error)
    /// WITHOUT a full reinit — covers the common transient case (resolution/
    /// mode change at the source, fullscreen app, UAC secure desktop). false = not recovered
    /// → the caller aborts the session and the supervisor rebuilds the whole chain.</summary>
    private bool TryRecoverCapture(ICaptureSource capture)
    {
        for (int attempt = 0; attempt < 5 && !_stop; attempt++)
        {
            if (capture.Recover()) { Log("Capture source recovered."); return true; }
            InterruptibleSleep(120);
        }
        return false;
    }

    /// <summary>
    /// The configured monitor/window can't be used right now. Missing (unplugged, app closed) →
    /// <see cref="SessionResult.Waiting"/>, logged once per reason; a real configuration error →
    /// <see cref="SessionResult.Fatal"/> as before.
    /// </summary>
    private SessionResult SelectionFailed(LocalizedText error)
    {
        if (!IsWaitable(error))
        {
            Log("Start aborted: " + error.English);
            _lastError = error;
            return SessionResult.Fatal;
        }
        _lastError = new LocalizedText("error.waiting", error, WaitForSourceMs / 1000);
        if (error.English != _lastWaitReason)
            Log($"Waiting: {error.English} — checking every {WaitForSourceMs / 1000} s.");
        _lastWaitReason = error.English;
        return SessionResult.Waiting;
    }

    private (SessionResult result, long healthyMs) RunSession()
    {
        _lastError = null;  // optimistic: a successful reinit should not leave a stale message behind
        var cfg = MirrorConfig.Load(ConfigPath);
        SetupLog("RitschyMirror render start.");

        var factory = CreateDXGIFactory2<IDXGIFactory2>(false);

        // Enumerate ALL adapters + outputs (source/target may be on different adapters).
        var meta = MonitorNames.GetMonitorMeta();  // GDI name → {Friendly, DevicePath}
        var adapters = new List<IDXGIAdapter1>();
        var displays = new List<(IDXGIAdapter1 Adapter, IDXGIOutput6 Output,
                                 int L, int T, int R, int B, bool Hdr, string Name, string Friendly, string Key)>();
        // Every early return below — and an exception mid-enumeration — must release what was
        // enumerated so far; with the 1 s waiting loop a leak here would repeat every second.
        void CleanupEnum()
        {
            foreach (var disp in displays) disp.Output.Dispose();
            foreach (var ad in adapters) ad.Dispose();
            factory.Dispose();
        }
        try
        {
            for (uint a = 0; ; a++)
            {
                if (factory.EnumAdapters1(a, out IDXGIAdapter1? ad).Failure || ad is null) break;
                adapters.Add(ad);
                string adName = ad.Description1.Description;
                for (uint o = 0; ; o++)
                {
                    if (ad.EnumOutputs(o, out IDXGIOutput? outp).Failure || outp is null) break;
                    using (outp)
                    {
                        // o6 belongs to the displays list (→ CleanupEnum) only once added; until then
                        // a failing query releases it here.
                        var o6 = outp.QueryInterface<IDXGIOutput6>();
                        OutputDescription1 d;
                        string friendly, key;
                        bool hdr;
                        try
                        {
                            d = o6.Description1;
                            hdr = d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020
                               || d.ColorSpace == ColorSpaceType.RgbFullG10NoneP709;
                            meta.TryGetValue(d.DeviceName, out var mm);
                            friendly = mm.Friendly ?? "";
                            key = MakeKey(mm.DevicePath ?? "", friendly, d.DeviceName);
                        }
                        catch
                        {
                            o6.Dispose();
                            throw;
                        }
                        var dc = d.DesktopCoordinates;
                        displays.Add((ad, o6, dc.Left, dc.Top, dc.Right, dc.Bottom, hdr, $"{d.DeviceName} [{adName}]", friendly, key));
                        SetupLog($"  Display {displays.Count - 1}: {d.DeviceName} ({(friendly.Length > 0 ? friendly : "?")})  {dc.Right - dc.Left}x{dc.Bottom - dc.Top}  HDR={hdr}  [{adName}]");
                    }
                }
            }
        }
        catch
        {
            CleanupEnum();
            throw;
        }
        if (displays.Count == 0) { CleanupEnum(); return (SelectionFailed(new LocalizedText("error.no_displays")), 0); }

        // Always resolve the target monitor via stable identity (both source modes mirror to it);
        // if the configured monitor is missing → clean abort with a plain-text message.
        var ids = displays.Select(x => (Key: x.Key, Label: x.Friendly)).ToList();
        var (dstIdx, dstErr) = ResolveSelection(cfg.TargetKey, cfg.TargetLabel, cfg.TargetDisplay, ids, isSource: false);
        if (dstErr != null)
        {
            CleanupEnum();
            return (SelectionFailed(dstErr), 0);
        }
        var dst = displays[dstIdx];
        string mode = cfg.ResolveOutputMode();
        string captureMode = cfg.ResolveCaptureMode();
        // The mirror covers its target: on the primary monitor that is your own desktop and taskbar
        // (the only way back would be the tray). The windowed test mode is fine anywhere.
        if (mode != "windowed" && dst.L == 0 && dst.T == 0)
        {
            var primary = new LocalizedText("error.target_is_primary");
            Log("Start aborted: " + primary.English);
            _lastError = primary;
            CleanupEnum();
            return (SessionResult.Fatal, 0);
        }

        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        // Everything created from here on is released in the finally below — also when a step throws
        // (e.g. shader compile, swap chain). A leaked Desktop Duplication would make every reinit
        // fail with E_INVALIDARG, because an output can only be duplicated once per process.
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        ICaptureSource? capture = null;
        Win32Window? window = null;
        Renderer? renderer = null;
        bool exclusive = false, keepAwakeOn = false;

        try
        {

            if (captureMode == "window")
            {
                // Window source (WGC): saved identity → current HWND. If the window is missing
                // (app not open) → clean abort with a plain-text message instead of blindly mirroring something.
                var (hwnd, winErr) = WindowEnum.Resolve(cfg.WindowExe, cfg.WindowTitle, cfg.WindowTitleSimilar);
                if (winErr != null || hwnd == IntPtr.Zero)
                {
                    return (SelectionFailed(winErr ?? new LocalizedText("error.no_window_selected")), 0);
                }
                // Device on the TARGET adapter (same-adapter present); WGC delivers the window image
                // across adapters via the DWM — exactly what makes it cross-GPU robust.
                D3D11CreateDevice(dst.Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, featureLevels,
                    out device, out context).CheckError();
                // HDR detection: is the window (center point) on an HDR monitor? → tonemap rolls off highlights.
                bool winHdr = false;
                if (GetWindowRect(hwnd, out RECT wr))
                {
                    int cx = (wr.Left + wr.Right) / 2, cy = (wr.Top + wr.Bottom) / 2;
                    foreach (var d in displays)
                        if (cx >= d.L && cx < d.R && cy >= d.T && cy < d.B) { winHdr = d.Hdr; break; }
                }
                SetupLog($"Source = window '{cfg.WindowTitle}' ({cfg.WindowExe}), target = Display {dstIdx} ({dst.Name}), output_mode={mode}, layout={cfg.LayoutMode}, HDR={winHdr}");
                capture = WgcCapture.ForWindow(device, hwnd, winHdr, cfg.ShowCursor);
            }
            else
            {
                // Monitor source (Desktop Duplication, duplication-bound) → device on the SOURCE adapter.
                var (srcIdx, srcErr) = ResolveSelection(cfg.SourceKey, cfg.SourceLabel, cfg.SourceDisplay, ids, isSource: true);
                if (srcErr != null)
                    return (SelectionFailed(srcErr), 0);
                if (srcIdx == dstIdx)
                {
                    // Mirroring a monitor onto itself only feeds the output back into the capture.
                    var same = new LocalizedText("error.same_source_target");
                    Log("Start aborted: " + same.English);
                    _lastError = same;
                    return (SessionResult.Fatal, 0);
                }
                var src = displays[srcIdx];
                if (cfg.ResolveMonitorApi() == "wgc")
                {
                    // WGC monitor capture: device on the TARGET adapter (same-adapter present); the DWM
                    // hands the monitor image across adapters, like window capture.
                    D3D11CreateDevice(dst.Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, featureLevels,
                        out device, out context).CheckError();
                    var srcOutput = src.Output;
                    SetupLog($"Source = Display {srcIdx} ({src.Name}) via WGC, target = Display {dstIdx} ({dst.Name}), output_mode={mode}, layout={cfg.LayoutMode}");
                    capture = WgcCapture.ForMonitor(device, srcOutput.Description1.Monitor, () => IsHdr(srcOutput), cfg.ShowCursor);
                }
                else
                {
                    // DXGI Desktop Duplication is adapter-bound → device on the SOURCE adapter.
                    D3D11CreateDevice(src.Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, featureLevels,
                        out device, out context).CheckError();
                    SetupLog($"Source = Display {srcIdx} ({src.Name}) via DXGI, target = Display {dstIdx} ({dst.Name}), output_mode={mode}, layout={cfg.LayoutMode}");
                    capture = new DuplicationCapture(device, src.Output);
                }
            }

            // Window geometry by output_mode (always from the TARGET display)
            bool windowed = mode == "windowed";
            int x, y, outW, outH; bool borderless;
            if (windowed)
            {
                x = 100; y = 100; outW = cfg.WindowWidth; outH = cfg.WindowHeight; borderless = false;
            }
            else
            {
                x = dst.L; y = dst.T; outW = dst.R - dst.L; outH = dst.B - dst.T; borderless = true;
            }

            SetupLog("Step: creating window...");
            window = new Win32Window("RitschyMirror", x, y, outW, outH, borderless);
            SetupLog("Step: renderer/swapchain...");
            renderer = new Renderer(factory, device, context, window.Hwnd, outW, outH, cfg.OutputBitDepth);
            SetupLog($"Capture {capture.Width}x{capture.Height} HDR={capture.InputIsHdr} → output {outW}x{outH} ({(cfg.OutputBitDepth >= 10 ? "10bit" : "8bit")})");
            SetupLog(capture.InputIsHdr
                ? $"Source is HDR → tonemapping {(cfg.TonemapEnabled ? "active" : "OFF")} (peak {cfg.SourcePeakNits:F0} nits at white point {cfg.TargetPaperwhite:F0} nits)."
                : "Source is SDR → 1:1 passthrough, no tonemapping (source_peak_nits/target_paperwhite have no effect here).");

            // Mouse lock / exclusive depending on output_mode
            if (!windowed && mode == "exclusive")
            {
                if (renderer.TrySetExclusiveFullscreen(dst.Output)) { exclusive = true; SetupLog("Exclusive fullscreen active."); }
                else { SetupLog("Exclusive not possible (cross-adapter?) → falling back to fullscreen_block."); window.EnableCursorBlock(dst.L, dst.T, dst.R, dst.B); }
            }
            else if (!windowed && mode == "fullscreen_block")
            {
                window.EnableCursorBlock(dst.L, dst.T, dst.R, dst.B);
                SetupLog("Mouse lock active (fullscreen_block).");
            }

            // From here on we really mirror → optionally block screen/sleep (auto-release
            // on stop). Can be toggled live: keep_awake is picked up in the hot reload below.
            if (cfg.KeepAwake) { KeepAwakeBegin(); keepAwakeOn = true; }

            DateTime lastCfgWrite = SafeWriteTime();
            bool lastSrcHdr = capture.InputIsHdr;
            var sessionResult = SessionResult.Stopped;  // clean default: window closed / Stop()

            using var pacer = new FramePacer();
            using var overlay = new FpsOverlay(device, outH);
            _captureFps.Reset();
            _outputFps.Reset();
            Audio.SetDesired(cfg.AudioMirror, dst.Friendly, cfg);
            if (!_quietSetup) LogFramePacing(cfg);

            var clock = Stopwatch.StartNew();
            long nextCfgPollMs = ConfigPollMs, nextHdrPollMs = HdrPollMs;
            long lastSoakMs = 0, soakCaptured = 0, soakPresented = 0, captured = 0;
            long accessWaitSinceMs = -1, nextAccessRetryMs = 0;   // DXGI: waiting out the secure desktop
            if (_quietSetup) Log("Mirroring resumed — rendering again.");
            _quietSetup = false;
            _waiting = false;
            _lastWaitReason = "";
            bool dirty = true;  // something other than a new source frame needs a redraw
            bool overlayFailed = false;
            long presented = 0;

            while (window.Running && !_stop)
            {
                window.PumpMessages();
                long nowMs = clock.ElapsedMilliseconds;

                // Config hot reload (mtime polling). LIVE parameters only; structure needs Restart.
                if (nowMs >= nextCfgPollMs)
                {
                    nextCfgPollMs = nowMs + ConfigPollMs;
                    var t = SafeWriteTime();
                    if (t != lastCfgWrite)
                    {
                        lastCfgWrite = t;
                        var nc = MirrorConfig.Load(ConfigPath);
                        bool pacingChanged = nc.EffectiveTargetFps != cfg.EffectiveTargetFps || nc.Vsync != cfg.Vsync;
                        CopyLiveKeys(nc, cfg);
                        // Sleep-mode lock on/off live, without a render restart.
                        if (cfg.KeepAwake && !keepAwakeOn) { KeepAwakeBegin(); keepAwakeOn = true; }
                        else if (!cfg.KeepAwake && keepAwakeOn) { KeepAwakeEnd(); keepAwakeOn = false; }
                        // Source-side live parameters (e.g. WGC cursor capture in window mode).
                        capture.ApplyLiveConfig(cfg);
                        Audio.SetDesired(cfg.AudioMirror, dst.Friendly, cfg);
                        if (pacingChanged) { pacer.Reset(); LogFramePacing(cfg); }
                        dirty = true;
                        Log("Config reloaded (live parameters).");
                    }
                }

                // Track HDR on/off at the monitor during operation. Without it the shader keeps
                // using the wrong source curve after a toggle: HDR source as SDR = blown out,
                // SDR source as HDR = too dark.
                if (nowMs >= nextHdrPollMs)
                {
                    nextHdrPollMs = nowMs + HdrPollMs;
                    capture.RefreshSourceState();
                    if (capture.InputIsHdr != lastSrcHdr)
                    {
                        lastSrcHdr = capture.InputIsHdr;
                        dirty = true;
                        Log(lastSrcHdr
                            ? "Source switched to HDR → tonemapping path active."
                            : "Source switched to SDR → 1:1 passthrough, no tonemapping.");
                    }
                }

                if (nowMs - lastSoakMs >= SoakLogMs)
                {
                    LogSoak(nowMs - lastSoakMs, captured - soakCaptured, presented - soakPresented);
                    lastSoakMs = nowMs; soakCaptured = captured; soakPresented = presented;
                }

                // Secure desktop (UAC / lock screen): keep window + device, show the last frame and
                // re-try the duplication once a second — back within a second of the desktop returning.
                if (accessWaitSinceMs >= 0)
                {
                    if (nowMs >= nextAccessRetryMs)
                    {
                        if (capture.Recover())
                        {
                            Log($"Capture resumed after {(nowMs - accessWaitSinceMs) / 1000.0:0.0} s (desktop accessible again).");
                            accessWaitSinceMs = -1;
                            pacer.Reset();
                            dirty = true;
                        }
                        else if (!capture.AwaitingAccess)
                        {
                            Log("Capture source still unavailable (not the secure desktop) → full reinit of the render chain.");
                            sessionResult = SessionResult.Lost;
                            break;
                        }
                        else nextAccessRetryMs = nowMs + SecureDesktopRetryMs;
                    }
                    if (accessWaitSinceMs >= 0) { Thread.Sleep(AccessWaitTickMs); continue; }
                }

                // Frame pacing. VSync on or no limit: block until the source delivers a new frame (or
                // the timeout). target_fps with VSync off: sleep until the next slot, THEN take whatever
                // is newest — the capture keeps only the latest image, so a slot never shows a stale frame.
                int fps = cfg.EffectiveTargetFps;
                bool newFrame;
                try
                {
                    if (fps > 0)
                    {
                        pacer.WaitForNextFrame(fps, () => _stop || !window.Running, window.PumpMessages);
                        newFrame = capture.TryAcquire(context, 0);
                    }
                    else
                    {
                        newFrame = capture.TryAcquire(context, AcquireTimeoutMs);
                    }
                }
                catch (Exception ex)
                {
                    // ACCESS_LOST or similar → first a quick recovery of the source (transient case).
                    Log("Capture source lost (" + ex.Message + ") → re-initializing.");
                    if (!TryRecoverCapture(capture))
                    {
                        if (capture.AwaitingAccess)
                        {
                            Log($"Capture paused: desktop not accessible (UAC prompt / lock screen?) — retrying every {SecureDesktopRetryMs / 1000} s, last frame stays on screen.");
                            accessWaitSinceMs = nowMs;
                            nextAccessRetryMs = nowMs + SecureDesktopRetryMs;
                            continue;
                        }
                        // If it stays dead (e.g. device lost, cross-adapter) → do NOT keep spinning,
                        // abort the session instead → the supervisor rebuilds the whole chain.
                        Log("Duplication recovery failed → full reinit of the render chain.");
                        sessionResult = SessionResult.Lost;
                        break;
                    }
                    pacer.Reset();
                    dirty = true;
                    continue;  // fresh duplication → next round acquires cleanly again
                }
                if (newFrame) { _captureFps.Tick(); captured++; }

                // FPS readout: rates are published once per second; re-rasterise only when they change.
                bool ratesChanged = _captureFps.Update() | _outputFps.Update();
                bool showOverlay = cfg.FpsOverlay && !overlayFailed;
                if (showOverlay && (ratesChanged || overlay.Srv is null))
                {
                    try
                    {
                        if (overlay.SetText(FpsOverlay.FormatRates(_captureFps.Fps, _outputFps.Fps))) dirty = true;
                    }
                    catch (Exception ex)
                    {
                        // A diagnostics aid must never take the mirror down (GDI+/texture failure).
                        overlayFailed = true;
                        showOverlay = false;
                        Log("FPS overlay disabled for this session: " + ex.Message);
                    }
                }

                // Nothing new to show → skip render + present entirely (the display keeps the last
                // frame). This is where an idle source or a frame limit saves GPU and copy work.
                if (capture.Srv == null || !(newFrame || dirty)) continue;

                renderer.Render(capture, cfg);
                _unscaled = renderer.Unscaled;
                if (showOverlay) renderer.DrawOverlay(overlay, cfg.FpsOverlayPosition);
                renderer.Present(cfg.Vsync);
                _outputFps.Tick();
                presented++;
                dirty = false;
            }
            // Only a session that actually showed frames counts as healthy (see HealthySessionMs).
            long healthyMs = presented > 0 ? clock.ElapsedMilliseconds : 0;

            Log(sessionResult == SessionResult.Lost
                ? "Render session lost, cleaning up for reinit."
                : "Render finished, cleaning up.");
            return (sessionResult, healthyMs);
        }
        finally
        {
            if (keepAwakeOn) KeepAwakeEnd();
            window?.DisableCursorBlock();
            if (exclusive) renderer?.ExitFullscreen(); // BEFORE swapchain dispose (DXGI requirement)
            capture?.Dispose();
            renderer?.Dispose();
            window?.Destroy();                          // AFTER swapchain dispose: HWND no longer referenced
            context?.Dispose();
            device?.Dispose();
            CleanupEnum();
        }
    }

    /// <summary>HDR state of an output from its current colour space (scRGB or HDR10 = HDR).</summary>
    private static bool IsHdr(IDXGIOutput6 output)
    {
        var cs = output.Description1.ColorSpace;
        return cs == ColorSpaceType.RgbFullG2084NoneP2020 || cs == ColorSpaceType.RgbFullG10NoneP709;
    }

    /// <summary>Copy the LIVE keys of a freshly loaded config into the running one.</summary>
    private static void CopyLiveKeys(MirrorConfig from, MirrorConfig to)
    {
        to.TonemapEnabled = from.TonemapEnabled; to.Operator = from.Operator;
        to.SourcePeakNits = from.SourcePeakNits; to.TargetPaperwhite = from.TargetPaperwhite;
        to.Exposure = from.Exposure; to.Saturation = from.Saturation;
        to.Contrast = from.Contrast; to.Gamma = from.Gamma; to.ContentOffsetY = from.ContentOffsetY;
        to.Vsync = from.Vsync;
        to.ScaleFilter = from.ScaleFilter;
        to.LayoutMode = from.LayoutMode;
        to.CropX = from.CropX; to.CropY = from.CropY; to.CropW = from.CropW; to.CropH = from.CropH;
        to.ShowCursor = from.ShowCursor;
        to.KeepAwake = from.KeepAwake;
        to.TargetFps = from.TargetFps;
        to.FpsOverlay = from.FpsOverlay; to.FpsOverlayPosition = from.FpsOverlayPosition;
        to.AudioMirror = from.AudioMirror; to.AudioSources = from.AudioSources;
        to.AudioRenderGainDb = from.AudioRenderGainDb; to.AudioCaptureGainDb = from.AudioCaptureGainDb;
        to.AudioOutputId = from.AudioOutputId; to.AudioOutputName = from.AudioOutputName;
        to.AudioFollowVolume = from.AudioFollowVolume;
        to.WindowTitleSimilar = from.WindowTitleSimilar;
    }

    private void LogFramePacing(MirrorConfig cfg)
    {
        int fps = cfg.EffectiveTargetFps;
        if (cfg.Vsync)
            Log(cfg.TargetFps > 0
                ? $"Frame pacing: VSync (target_fps {cfg.TargetFps} ignored while VSync is on)."
                : "Frame pacing: VSync.");
        else
            Log(fps > 0
                ? $"Frame pacing: target {fps} fps (VSync off)."
                : "Frame pacing: unlimited (VSync off, no target_fps).");
    }

    private DateTime SafeWriteTime()
    {
        try { return File.GetLastWriteTimeUtc(ConfigPath); } catch { return DateTime.MinValue; }
    }

    // ── Prevent sleep / monitor power-off while mirroring ──
    // Like a video player: ES_DISPLAY_REQUIRED keeps the screen on, ES_SYSTEM_REQUIRED
    // prevents the machine from sleeping. ES_CONTINUOUS makes the state persistent
    // until we revert it (on the SAME thread). When the thread ends, the
    // state reverts automatically — a stopped/crashed mirror therefore never blocks sleep.
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    // Window rectangle (for HDR detection in window capture mode: which monitor is it on?).
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private void KeepAwakeBegin()
    {
        var r = SetThreadExecutionState(
            ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);
        Log(r == 0 ? "WARN: sleep lock failed (SetThreadExecutionState)."
                   : "Sleep + monitor power-off blocked (mirroring is running).");
    }

    private void KeepAwakeEnd()
    {
        // ES_CONTINUOUS only = withdraw the requests, allow normal power behavior again.
        SetThreadExecutionState(ExecutionState.Continuous);
        Log("Sleep lock released.");
    }

    // ── Display enumeration without running render (GUI dropdowns + agent) ──
    public static List<DisplayInfo> EnumerateDisplays()
    {
        var result = new List<DisplayInfo>();
        var meta = MonitorNames.GetMonitorMeta();  // \\.\DISPLAYx → {Friendly, DevicePath}
        IDXGIFactory2? factory = null;
        try
        {
            factory = CreateDXGIFactory2<IDXGIFactory2>(false);
            for (uint a = 0; ; a++)
            {
                if (factory.EnumAdapters1(a, out IDXGIAdapter1? ad).Failure || ad is null) break;
                using var adapter = ad;   // released also when a call below throws
                string adName = ad.Description1.Description;
                for (uint o = 0; ; o++)
                {
                    if (ad.EnumOutputs(o, out IDXGIOutput? outp).Failure || outp is null) break;
                    using var output = outp;
                    using var o6 = outp.QueryInterface<IDXGIOutput6>();
                    var d = o6.Description1;
                    bool hdr = d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020
                            || d.ColorSpace == ColorSpaceType.RgbFullG10NoneP709;
                    var dc = d.DesktopCoordinates;
                    meta.TryGetValue(d.DeviceName, out var mm);
                    string friendlyName = mm.Friendly ?? "";
                    result.Add(new DisplayInfo
                    {
                        Index = result.Count,
                        Name = d.DeviceName,
                        Friendly = friendlyName,
                        Key = MakeKey(mm.DevicePath ?? "", friendlyName, d.DeviceName),
                        Resolution = $"{dc.Right - dc.Left}x{dc.Bottom - dc.Top}",
                        Hdr = hdr,
                        Adapter = adName,
                        Left = dc.Left, Top = dc.Top, Right = dc.Right, Bottom = dc.Bottom,
                    });
                }
            }
        }
        catch { /* empty list = no displays could be determined */ }
        finally { factory?.Dispose(); }
        return result;
    }
}

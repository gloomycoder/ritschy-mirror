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
    public string LastError { get; private set; } = "";

    public MirrorEngine(string baseDir)
    {
        ConfigPath = Path.Combine(baseDir, "mirror_config.json");
        LogPath = Path.Combine(baseDir, "mirror.log");
    }

    public bool IsRunning => _thread is { IsAlive: true };

    public void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    // ── Control ─────────────────────────────────────────────────────────────
    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning) return;
            _stop = false;
            LastError = "";
            _thread = new Thread(RenderThreadMain) { IsBackground = true, Name = "MirrorRender" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { _stop = true; t = _thread; }
        if (t != null && t.IsAlive && Thread.CurrentThread != t)
            t.Join(4000);
        lock (_gate) { _thread = null; }
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
    public static (int idx, string error) ResolveSelection(
        string key, string label, int index,
        IReadOnlyList<(string Key, string Label)> displays, string role)
    {
        if (displays.Count == 0) return (-1, "Keine Displays gefunden");
        bool hasIdentity = !string.IsNullOrWhiteSpace(key) || !string.IsNullOrWhiteSpace(label);
        if (hasIdentity)
        {
            if (!string.IsNullOrWhiteSpace(key))
                for (int i = 0; i < displays.Count; i++)
                    if (string.Equals(displays[i].Key, key, StringComparison.OrdinalIgnoreCase)) return (i, "");
            if (!string.IsNullOrWhiteSpace(label))
            {
                int found = -1, count = 0;
                for (int i = 0; i < displays.Count; i++)
                    if (string.Equals(displays[i].Label, label, StringComparison.OrdinalIgnoreCase)) { found = i; count++; }
                if (count == 1) return (found, "");
            }
            string name = string.IsNullOrWhiteSpace(label) ? key : label;
            return (-1, $"{role}-Monitor '{name}' nicht verbunden");
        }
        return (Math.Clamp(index, 0, displays.Count - 1), ""); // old config without identity
    }

    /// <summary>Pre-check (tray/GUI): can the configured source + target be resolved right now?
    /// null = ok, otherwise a plain-text error. Starts nothing.</summary>
    public string? Preflight()
    {
        var displays = EnumerateDisplays();
        if (displays.Count == 0) return "Keine Displays gefunden";
        var cfg = MirrorConfig.Load(ConfigPath);
        var ids = displays.Select(d => (Key: d.Key, Label: d.Friendly)).ToList();
        var (_, se) = ResolveSelection(cfg.SourceKey, cfg.SourceLabel, cfg.SourceDisplay, ids, "Quell");
        if (se.Length != 0) return se;
        var (_, de) = ResolveSelection(cfg.TargetKey, cfg.TargetLabel, cfg.TargetDisplay, ids, "Ziel");
        if (de.Length != 0) return de;
        return null;
    }

    // ── Render thread ───────────────────────────────────────────────────────
    /// <summary>Outcome of a render session: stopped cleanly, lost transiently (reinit),
    /// or fatal (config/selection error — a reinit would not help).</summary>
    private enum SessionResult { Stopped, Lost, Fatal }

    // A session that rendered this many frames cleanly counts as "ran healthy" → a
    // subsequent loss is transient and resets the reinit budget (only tight
    // failure chains count toward the abort limit). ~5 s at 60 fps.
    private const long HealthyFrameThreshold = 300;
    private const int MaxConsecutiveReinit = 8;

    private void RenderThreadMain()
    {
        int consecutive = 0;
        while (!_stop)
        {
            SessionResult result;
            long frames = 0;
            try
            {
                (result, frames) = RunSession();
            }
            catch (Exception ex)
            {
                // Hard error during setup (e.g. device/capture creation) → treat it like a
                // loss and retry with backoff instead of killing the thread.
                LastError = ex.Message;
                Log("ERROR (render session): " + ex);
                result = SessionResult.Lost;
            }

            if (_stop || result == SessionResult.Stopped || result == SessionResult.Fatal)
                break;

            // If the session ran healthy long enough beforehand, the loss was transient → reset the budget.
            if (frames >= HealthyFrameThreshold) consecutive = 0;
            consecutive++;
            if (consecutive > MaxConsecutiveReinit)
            {
                LastError = "Spiegelung ließ sich nach mehreren Versuchen nicht wiederherstellen — gestoppt.";
                Log("ABORT: " + LastError);
                break;
            }

            // Exponential backoff (250 ms → max 5 s), interruptible by Stop().
            int backoffMs = Math.Min(5000, 250 * (1 << Math.Min(consecutive - 1, 4)));
            Log($"Mirroring lost — full reinit in {backoffMs} ms (attempt {consecutive}/{MaxConsecutiveReinit}).");
            InterruptibleSleep(backoffMs);
        }
        Log("Render thread finished.");
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

    private (SessionResult result, long frames) RunSession()
    {
        LastError = "";  // optimistic: a successful reinit should not leave a stale message behind
        var cfg = MirrorConfig.Load(ConfigPath);
        Log("RitschyMirror render start.");

        var factory = CreateDXGIFactory2<IDXGIFactory2>(false);

        // Enumerate ALL adapters + outputs (source/target may be on different adapters).
        var meta = MonitorNames.GetMonitorMeta();  // GDI name → {Friendly, DevicePath}
        var adapters = new List<IDXGIAdapter1>();
        var displays = new List<(IDXGIAdapter1 Adapter, IDXGIOutput6 Output,
                                 int L, int T, int R, int B, bool Hdr, string Name, string Friendly, string Key)>();
        for (uint a = 0; ; a++)
        {
            if (factory.EnumAdapters1(a, out IDXGIAdapter1? ad).Failure || ad is null) break;
            adapters.Add(ad);
            string adName = ad.Description1.Description;
            for (uint o = 0; ; o++)
            {
                if (ad.EnumOutputs(o, out IDXGIOutput? outp).Failure || outp is null) break;
                var o6 = outp.QueryInterface<IDXGIOutput6>();
                var d = o6.Description1;
                bool hdr = d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020
                        || d.ColorSpace == ColorSpaceType.RgbFullG10NoneP709;
                var dc = d.DesktopCoordinates;
                meta.TryGetValue(d.DeviceName, out var mm);
                string friendly = mm.Friendly ?? "";
                string key = MakeKey(mm.DevicePath ?? "", friendly, d.DeviceName);
                displays.Add((ad, o6, dc.Left, dc.Top, dc.Right, dc.Bottom, hdr, $"{d.DeviceName} [{adName}]", friendly, key));
                outp.Dispose();
                Log($"  Display {displays.Count - 1}: {d.DeviceName} ({(friendly.Length > 0 ? friendly : "?")})  {dc.Right - dc.Left}x{dc.Bottom - dc.Top}  HDR={hdr}  [{adName}]");
            }
        }
        if (displays.Count == 0) { Log("No displays found."); LastError = "Keine Displays gefunden"; factory.Dispose(); return (SessionResult.Fatal, 0); }

        // Always resolve the target monitor via stable identity (both source modes mirror to it);
        // if the configured monitor is missing → clean abort with a plain-text message.
        var ids = displays.Select(x => (Key: x.Key, Label: x.Friendly)).ToList();
        void CleanupEnum()
        {
            foreach (var disp in displays) disp.Output.Dispose();
            foreach (var ad in adapters) ad.Dispose();
            factory.Dispose();
        }
        var (dstIdx, dstErr) = ResolveSelection(cfg.TargetKey, cfg.TargetLabel, cfg.TargetDisplay, ids, "Ziel");
        if (dstErr.Length != 0)
        {
            Log("Start aborted: " + dstErr);
            LastError = dstErr;
            CleanupEnum();
            return (SessionResult.Fatal, 0);
        }
        var dst = displays[dstIdx];
        string mode = cfg.ResolveOutputMode();
        string captureMode = cfg.ResolveCaptureMode();

        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        ID3D11Device device;
        ID3D11DeviceContext context;
        ICaptureSource capture;

        if (captureMode == "window")
        {
            // Window source (WGC): saved identity → current HWND. If the window is missing
            // (app not open) → clean abort with a plain-text message instead of blindly mirroring something.
            var (hwnd, winErr) = WindowEnum.Resolve(cfg.WindowExe, cfg.WindowTitle);
            if (winErr.Length != 0 || hwnd == IntPtr.Zero)
            {
                string e = winErr.Length != 0 ? winErr : "Kein Fenster ausgewählt";
                Log("Start aborted: " + e);
                LastError = e;
                CleanupEnum();
                return (SessionResult.Fatal, 0);
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
            Log($"Source = window '{cfg.WindowTitle}' ({cfg.WindowExe}), target = Display {dstIdx} ({dst.Name}), output_mode={mode}, layout={cfg.LayoutMode}, HDR={winHdr}");
            capture = new WindowCapture(device, hwnd, winHdr, cfg.ShowCursor);
        }
        else
        {
            // Monitor source (Desktop Duplication, duplication-bound) → device on the SOURCE adapter.
            var (srcIdx, srcErr) = ResolveSelection(cfg.SourceKey, cfg.SourceLabel, cfg.SourceDisplay, ids, "Quell");
            if (srcErr.Length != 0)
            {
                Log("Start aborted: " + srcErr);
                LastError = srcErr;
                CleanupEnum();
                return (SessionResult.Fatal, 0);
            }
            var src = displays[srcIdx];
            D3D11CreateDevice(src.Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, featureLevels,
                out device, out context).CheckError();
            Log($"Source = Display {srcIdx} ({src.Name}), target = Display {dstIdx} ({dst.Name}), output_mode={mode}, layout={cfg.LayoutMode}");
            capture = new DuplicationCapture(device, src.Output);
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

        Log("Step: creating window...");
        var window = new Win32Window("RitschyMirror", x, y, outW, outH, borderless);
        Log("Step: renderer/swapchain...");
        var renderer = new Renderer(factory, device, context, window.Hwnd, outW, outH, cfg.OutputBitDepth);
        Log($"Capture {capture.Width}x{capture.Height} HDR={capture.InputIsHdr} → output {outW}x{outH} ({(cfg.OutputBitDepth >= 10 ? "10bit" : "8bit")})");
        Log(capture.InputIsHdr
            ? $"Source is HDR → tonemapping {(cfg.TonemapEnabled ? "active" : "OFF")} (peak {cfg.SourcePeakNits:F0} nits at white point {cfg.TargetPaperwhite:F0} nits)."
            : "Source is SDR → 1:1 passthrough, no tonemapping (source_peak_nits/target_paperwhite have no effect here).");

        // Mouse lock / exclusive depending on output_mode
        bool exclusive = false;
        if (!windowed && mode == "exclusive")
        {
            if (renderer.TrySetExclusiveFullscreen(dst.Output)) { exclusive = true; Log("Exclusive fullscreen active."); }
            else { Log("Exclusive not possible (cross-adapter?) → falling back to fullscreen_block."); window.EnableCursorBlock(dst.L, dst.T, dst.R, dst.B); }
        }
        else if (!windowed && mode == "fullscreen_block")
        {
            window.EnableCursorBlock(dst.L, dst.T, dst.R, dst.B);
            Log("Mouse lock active (fullscreen_block).");
        }

        // From here on we really mirror → optionally block screen/sleep (auto-release
        // on stop). Can be toggled live: keep_awake is picked up in the hot reload below.
        bool keepAwakeOn = false;
        if (cfg.KeepAwake) { KeepAwakeBegin(); keepAwakeOn = true; }

        DateTime lastCfgWrite = SafeWriteTime();
        bool lastSrcHdr = capture.InputIsHdr;
        int frame = 0;
        long framesRendered = 0;
        var sessionResult = SessionResult.Stopped;  // clean default: window closed / Stop()

        while (window.Running && !_stop)
        {
            window.PumpMessages();

            // Config hot reload (mtime polling). LIVE parameters only; structure needs Restart.
            if ((frame++ & 31) == 0)
            {
                var t = SafeWriteTime();
                if (t != lastCfgWrite)
                {
                    lastCfgWrite = t;
                    var nc = MirrorConfig.Load(ConfigPath);
                    cfg.TonemapEnabled = nc.TonemapEnabled; cfg.Operator = nc.Operator;
                    cfg.SourcePeakNits = nc.SourcePeakNits; cfg.TargetPaperwhite = nc.TargetPaperwhite;
                    cfg.Exposure = nc.Exposure; cfg.Saturation = nc.Saturation;
                    cfg.Contrast = nc.Contrast; cfg.Gamma = nc.Gamma; cfg.ContentOffsetY = nc.ContentOffsetY;
                    cfg.Vsync = nc.Vsync;
                    cfg.LayoutMode = nc.LayoutMode;
                    cfg.CropX = nc.CropX; cfg.CropY = nc.CropY; cfg.CropW = nc.CropW; cfg.CropH = nc.CropH;
                    cfg.ShowCursor = nc.ShowCursor;
                    cfg.KeepAwake = nc.KeepAwake;
                    // Sleep-mode lock on/off live, without a render restart.
                    if (cfg.KeepAwake && !keepAwakeOn) { KeepAwakeBegin(); keepAwakeOn = true; }
                    else if (!cfg.KeepAwake && keepAwakeOn) { KeepAwakeEnd(); keepAwakeOn = false; }
                    // Source-side live parameters (e.g. WGC cursor capture in window mode).
                    capture.ApplyLiveConfig(cfg);
                    Log("Config reloaded (live parameters).");
                }
            }

            // Track HDR on/off at the monitor during operation (about every 256 frames). Without it
            // the shader keeps using the wrong source curve after a toggle: HDR source
            // as SDR = blown out, SDR source as HDR = too dark.
            if ((frame & 255) == 0)
            {
                capture.RefreshSourceState();
                if (capture.InputIsHdr != lastSrcHdr)
                {
                    lastSrcHdr = capture.InputIsHdr;
                    Log(lastSrcHdr
                        ? "Source switched to HDR → tonemapping path active."
                        : "Source switched to SDR → 1:1 passthrough, no tonemapping.");
                }
            }

            try { capture.TryAcquire(context); }
            catch (Exception ex)
            {
                // ACCESS_LOST or similar → first a quick recovery of the source (transient case).
                Log("Capture source lost (" + ex.Message + ") → re-initializing.");
                if (!TryRecoverCapture(capture))
                {
                    // If it stays dead (e.g. device lost, cross-adapter) → do NOT keep spinning,
                    // abort the session instead → the supervisor rebuilds the whole chain.
                    Log("Duplication recovery failed → full reinit of the render chain.");
                    sessionResult = SessionResult.Lost;
                    break;
                }
                continue;  // fresh duplication → next round acquires cleanly again
            }

            if (capture.Srv != null)
            {
                renderer.Render(capture, cfg);
                renderer.Present(cfg.Vsync);
                framesRendered++;
            }
        }

        Log(sessionResult == SessionResult.Lost
            ? "Render session lost, cleaning up for reinit."
            : "Render finished, cleaning up.");
        if (keepAwakeOn) KeepAwakeEnd();
        window.DisableCursorBlock();
        if (exclusive) renderer.ExitFullscreen(); // BEFORE swapchain dispose (DXGI requirement)
        capture.Dispose();
        renderer.Dispose();
        window.Destroy();                          // AFTER swapchain dispose: HWND no longer referenced
        context.Dispose();
        device.Dispose();
        foreach (var disp in displays) disp.Output.Dispose();
        foreach (var ad in adapters) ad.Dispose();
        factory.Dispose();
        return (sessionResult, framesRendered);
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
                string adName = ad.Description1.Description;
                for (uint o = 0; ; o++)
                {
                    if (ad.EnumOutputs(o, out IDXGIOutput? outp).Failure || outp is null) break;
                    var o6 = outp.QueryInterface<IDXGIOutput6>();
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
                    o6.Dispose();
                    outp.Dispose();
                }
                ad.Dispose();
            }
        }
        catch { /* empty list = no displays could be determined */ }
        finally { factory?.Dispose(); }
        return result;
    }
}

using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace RitschyMirror;

/// <summary>
/// Capture via <b>Windows.Graphics.Capture</b> (WGC) — of ONE window / fullscreen application
/// (<see cref="ForWindow"/>), or of a whole monitor (<see cref="ForMonitor"/>, the alternative to
/// DXGI Desktop Duplication: works across GPUs, cursor composited by Windows).
///
/// Window capture uses
/// the same OS API that OBS' "Window Capture (Windows 10 1903+)" uses. Unlike OBS' "Game
/// Capture", NOTHING is injected into the game process (no anti-cheat risk, no elevation
/// problems): WGC pulls the image from the DWM compositor. This also makes it robust across GPUs
/// (exactly the cross-adapter case where OBS' Game Capture often stayed black).
///
/// Delivers frames as an R16G16B16A16_Float texture → identical to the duplication path, so it plugs
/// in as an <see cref="ICaptureSource"/> without pipeline changes in renderer/engine.
///
/// Security property: ONLY the window content is mirrored — if you tab out, the
/// app keeps running in the background and the stream still shows it, NOT your desktop.
/// </summary>
public sealed class WgcCapture : ICaptureSource
{
    private readonly ID3D11Device _device;
    private readonly IDirect3DDevice _d3dDevice;   // WinRT view of the same D3D11 device
    private readonly GraphicsCaptureItem _item;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _copyTex;
    private ID3D11ShaderResourceView? _srv;

    private int _poolW, _poolH;
    private volatile bool _sourceLost;   // window closed (Closed event)
    // Newest-frame slot. FrameArrived (free-threaded pool → WGC worker thread) pulls every frame out
    // of the pool immediately and keeps only the newest here. If frames were left queued instead, a
    // full pool makes WGC drop the NEW frames — at a low target_fps the mirror would show frames that
    // are a whole interval old. The render thread takes ownership of the slot in TryAcquire.
    private const int PoolBuffers = 3;   // one in the slot, one being copied, one free for WGC
    private readonly object _frameLock = new();
    private Direct3D11CaptureFrame? _latest;
    // The pool whose frames may enter _latest (guarded by _frameLock). A FrameArrived callback that
    // is already running when StopSession detaches the handler still delivers a frame afterwards;
    // checking the sender against this drops it instead of parking a dead pool's frame in the slot.
    private Direct3D11CaptureFramePool? _activePool;
    private readonly AutoResetEvent _frameArrived = new(false);
    private bool _showCursor;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool InputIsHdr { get; private set; }
    private readonly Func<bool>? _hdrProbe;   // monitor capture: re-reads the monitor's HDR state
    public ID3D11ShaderResourceView? Srv => _srv;

    // Window capture already bakes the cursor (if enabled) into the frame → the renderer's separate cursor pass
    // is skipped here (always "not visible"). Controlled via the WGC session.
    public bool CursorVisible => false;
    public int CursorX => 0;
    public int CursorY => 0;
    public int CursorW => 0;
    public int CursorH => 0;
    public ID3D11ShaderResourceView? CursorSrv => null;

    /// <summary>One window / fullscreen app. <paramref name="inputIsHdr"/>: the monitor it sits on.</summary>
    public static WgcCapture ForWindow(ID3D11Device device, IntPtr hwnd, bool inputIsHdr, bool showCursor) =>
        new(device, CreateItem(hwnd, forMonitor: false), inputIsHdr, null, showCursor);

    /// <summary>A whole monitor (HMONITOR); <paramref name="hdrProbe"/> reads its current HDR state.</summary>
    public static WgcCapture ForMonitor(ID3D11Device device, IntPtr hmonitor, Func<bool> hdrProbe, bool showCursor) =>
        new(device, CreateItem(hmonitor, forMonitor: true), hdrProbe(), hdrProbe, showCursor);

    private WgcCapture(ID3D11Device device, GraphicsCaptureItem item, bool inputIsHdr, Func<bool>? hdrProbe, bool showCursor)
    {
        _device = device;
        InputIsHdr = inputIsHdr;
        _hdrProbe = hdrProbe;
        _showCursor = showCursor;
        _d3dDevice = CreateDirect3DDevice(device);
        _item = item;
        _item.Closed += (_, _) => _sourceLost = true;   // window closed / monitor disconnected
        StartSession();
    }

    private void StartSession()
    {
        var size = _item.Size;
        _poolW = size.Width > 0 ? size.Width : 1;
        _poolH = size.Height > 0 ? size.Height : 1;
        if (Width == 0) { Width = _poolW; Height = _poolH; }   // provisional size until the first frame
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _d3dDevice, DirectXPixelFormat.R16G16B16A16Float, PoolBuffers, new SizeInt32 { Width = _poolW, Height = _poolH });
        _framePool.FrameArrived += OnFrameArrived;
        lock (_frameLock) _activePool = _framePool;
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = _showCursor;
        TryDisableBorder(_session);
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // Raised on a WGC thread; may race with StopSession/Dispose → never let it throw there.
        try
        {
            var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            while (sender.TryGetNextFrame() is { } newer) { frame.Dispose(); frame = newer; }
            Direct3D11CaptureFrame? discard;
            bool current;
            lock (_frameLock)
            {
                current = ReferenceEquals(sender, _activePool);
                if (current) { discard = _latest; _latest = frame; }
                else discard = frame;   // the session was stopped meanwhile
            }
            discard?.Dispose();   // superseded (never shown) or from a stopped pool
            if (current) _frameArrived.Set();
        }
        catch (ObjectDisposedException) { /* pool/session torn down concurrently */ }
        catch (System.Runtime.InteropServices.COMException) { /* same, surfaced as an RO error */ }
    }

    private void DropLatest()
    {
        Direct3D11CaptureFrame? f;
        lock (_frameLock) { f = _latest; _latest = null; }
        f?.Dispose();
    }

    private void StopSession()
    {
        if (_framePool != null) _framePool.FrameArrived -= OnFrameArrived;
        lock (_frameLock) _activePool = null;   // from here on no callback can fill the slot
        DropLatest();   // frames belong to the pool → release before disposing it
        try { _session?.Dispose(); } catch { /* already closed */ }
        try { _framePool?.Dispose(); } catch { }
        _session = null;
        _framePool = null;
    }

    /// <summary>Turn off the yellow WGC capture border where the API supports it (Win11, build 22000+).
    /// On Windows 10 the property does not exist → the border stays (purely cosmetic, not an error).</summary>
    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                && ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                session.IsBorderRequired = false;
        }
        catch { /* not critical — the border just stays visible */ }
    }

    public bool TryAcquire(ID3D11DeviceContext ctx, int timeoutMs = 16)
    {
        if (_sourceLost)
            throw new InvalidOperationException("Capture source (window/monitor) was closed");

        var pool = _framePool;
        if (pool is null) return false;

        using var frame = TakeNewestFrame(timeoutMs);
        if (frame is null) return false; // no new frame within the timeout (not an error)

        using var surface = frame.Surface;
        using var tex = GetTextureFromSurface(surface);
        var desc = tex.Description;

        EnsureTarget(desc);
        ctx.CopyResource(_copyTex!, tex);
        Width = (int)desc.Width;
        Height = (int)desc.Height;

        // Window size changed → adjust the frame pool for SUBSEQUENT frames (this frame is already copied).
        var content = frame.ContentSize;
        if (content.Width > 0 && content.Height > 0 && (content.Width != _poolW || content.Height != _poolH))
        {
            _poolW = content.Width; _poolH = content.Height;
            try { pool.Recreate(_d3dDevice, DirectXPixelFormat.R16G16B16A16Float, PoolBuffers, content); } catch { /* next attempt */ }
        }
        return true;
    }

    /// <summary>
    /// Take ownership of the newest frame, waiting up to <paramref name="timeoutMs"/> if none has
    /// arrived since the last call (0 = just poll). null = no new frame.
    /// </summary>
    private Direct3D11CaptureFrame? TakeNewestFrame(int timeoutMs)
    {
        var frame = TakeLatest();
        if (frame is null && timeoutMs > 0 && _frameArrived.WaitOne(timeoutMs))
            frame = TakeLatest();
        return frame;
    }

    private Direct3D11CaptureFrame? TakeLatest()
    {
        lock (_frameLock)
        {
            var f = _latest;
            _latest = null;
            return f;
        }
    }

    /// <summary>Re-create session/frame pool on the same window (transient errors).
    /// If the window is closed → false: the supervisor does a full reinit, which re-resolves the selection
    /// and then stops cleanly with "window not found" (instead of spinning blindly).</summary>
    /// <summary>WGC's FP16 frames are always linear scRGB.</summary>
    public bool SourceIsSrgbEncoded => false;

    /// <summary>WGC keeps delivering (the last image) across the secure desktop, so a failed recover is real.</summary>
    public bool AwaitingAccess => false;

    public bool Recover()
    {
        if (_sourceLost) return false;
        try
        {
            StopSession();
            StartSession();
            return true;
        }
        catch
        {
            StopSession();
            return false;
        }
    }

    /// <summary>Monitor capture: re-read the monitor's HDR state (toggling HDR needs no restart).
    /// Window capture: no-op — the HDR state comes from the monitor the window was on AT START (the
    /// window choice is a structural parameter anyway); moved to another monitor → render restart.</summary>
    public void RefreshSourceState()
    {
        if (_hdrProbe is null) return;
        try { InputIsHdr = _hdrProbe(); }
        catch (Exception) { /* output mid mode switch → keep the last known state */ }
    }

    /// <summary>Apply live config: toggle the WGC session's cursor capture without a restart.</summary>
    public void ApplyLiveConfig(MirrorConfig cfg)
    {
        if (cfg.ShowCursor == _showCursor) return;
        _showCursor = cfg.ShowCursor;
        try { if (_session != null) _session.IsCursorCaptureEnabled = _showCursor; } catch { }
    }

    private void EnsureTarget(Texture2DDescription src)
    {
        if (_copyTex != null && _copyTex.Description.Width == src.Width && _copyTex.Description.Height == src.Height)
            return;

        _srv?.Dispose();
        _copyTex?.Dispose();

        var desc = src;
        desc.BindFlags = BindFlags.ShaderResource;
        desc.Usage = ResourceUsage.Default;
        desc.CPUAccessFlags = CpuAccessFlags.None;
        desc.MiscFlags = ResourceOptionFlags.None;
        _copyTex = _device.CreateTexture2D(desc);
        _srv = _device.CreateShaderResourceView(_copyTex);
    }

    public void Dispose()
    {
        StopSession();
        _srv?.Dispose();
        _copyTex?.Dispose();
        // _d3dDevice (WinRT wrapper) + _item are released by the GC/CsWinRT; the actual
        // ID3D11Device is owned by the engine, which disposes it itself.
        (_d3dDevice as IDisposable)?.Dispose();
        _frameArrived.Dispose();
    }

    // ── WGC/WinRT interop ────────────────────────────────────────────────────────
    // IID of GraphicsCaptureItem (for the window interop factory call).
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    // IID of ID3D11Texture2D (to pull the D3D11 texture out of the WGC surface).
    private static readonly Guid ID3D11Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [ComImport]
    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [System.Runtime.InteropServices.Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    private static GraphicsCaptureItem CreateItem(IntPtr handle, bool forMonitor)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        IntPtr abi = forMonitor ? interop.CreateForMonitor(handle, ref iid) : interop.CreateForWindow(handle, ref iid);
        var item = GraphicsCaptureItem.FromAbi(abi);
        Marshal.Release(abi);
        return item;
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        using var dxgi = d3dDevice.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr ptr);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        var device = MarshalInterface<IDirect3DDevice>.FromAbi(ptr);
        Marshal.Release(ptr);
        return device;
    }

    private static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid iid = ID3D11Texture2DIid;
        IntPtr p = access.GetInterface(ref iid);
        return new ID3D11Texture2D(p);
    }
}

using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace RitschyMirror;

/// <summary>
/// DXGI Desktop Duplication of an output. Delivers a shader-readable
/// copy (R16G16B16A16_Float) per frame as an SRV. HDR-capable via DuplicateOutput1 with FP16.
///
/// The mouse cursor is NOT part of the desktop image (DXGI delivers it separately:
/// position + shape). It is optionally read here and provided as a BGRA texture,
/// so the renderer can composite it on request (output.show_cursor).
/// </summary>
public sealed class DuplicationCapture : ICaptureSource
{
    private readonly ID3D11Device _device;
    private readonly IDXGIOutput6 _output;   // for Recover() (duplication is output-bound)
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _copyTex;
    private ID3D11ShaderResourceView? _srv;

    // Cursor
    private ID3D11Texture2D? _cursorTex;
    private ID3D11ShaderResourceView? _cursorSrv;
    private byte[]? _shapeBuf;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool InputIsHdr { get; private set; }
    public ID3D11ShaderResourceView? Srv => _srv;

    public bool CursorVisible { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public int CursorW { get; private set; }
    public int CursorH { get; private set; }
    public ID3D11ShaderResourceView? CursorSrv => _cursorSrv;

    public DuplicationCapture(ID3D11Device device, IDXGIOutput6 output)
    {
        _device = device;
        _output = output;
        var desc = output.Description1;
        Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
        Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
        InputIsHdr = ReadHdrState();
        _dup = output.DuplicateOutput1(_device, 1, new[] { Format.R16G16B16A16_Float });
    }

    /// <summary>Read the current HDR state of the output. Not readable (output currently in a
    /// mode switch) → keep the last known state instead of briefly flipping wrongly.</summary>
    private bool ReadHdrState()
    {
        try
        {
            var cs = _output.Description1.ColorSpace;
            return cs == ColorSpaceType.RgbFullG10NoneP709
                || cs == ColorSpaceType.RgbFullG2084NoneP2020;
        }
        catch { return InputIsHdr; }
    }

    /// <summary>HDR on/off at the monitor is tracked during operation — otherwise after a
    /// toggle the shader keeps using the wrong source curve (too dark or blown out).</summary>
    public void RefreshSourceState() => InputIsHdr = ReadHdrState();

    /// <summary>True if a new frame was acquired + copied; false on timeout.
    /// If the duplication is dead (after a failed recreate), the method returns false
    /// instead of throwing on a null reference — the render loop then decides on reinit.</summary>
    public bool TryAcquire(ID3D11DeviceContext ctx, int timeoutMs = 16)
    {
        var dup = _dup;
        if (dup is null) return false;
        Result r = dup.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo frameInfo, out IDXGIResource? resource);
        if (r == Vortice.DXGI.ResultCode.WaitTimeout)
            return false;
        r.CheckError();

        try
        {
            using var tex = resource!.QueryInterface<ID3D11Texture2D>();
            EnsureTarget(tex.Description);
            ctx.CopyResource(_copyTex!, tex);
            UpdateCursor(frameInfo);
        }
        finally
        {
            resource?.Dispose();
            dup.ReleaseFrame();
        }
        return true;
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
        Width = (int)src.Width;
        Height = (int)src.Height;
    }

    // ── Cursor: read position + shape ─────────────────────────────────────
    private void UpdateCursor(OutduplFrameInfo fi)
    {
        var dup = _dup;
        if (dup is null) return;
        try
        {
            if (fi.LastMouseUpdateTime != 0)
            {
                CursorVisible = fi.PointerPosition.Visible;
                CursorX = fi.PointerPosition.Position.X;
                CursorY = fi.PointerPosition.Position.Y;
            }
            if (fi.PointerShapeBufferSize == 0)
                return;

            int size = (int)fi.PointerShapeBufferSize;
            if (_shapeBuf == null || _shapeBuf.Length < size) _shapeBuf = new byte[size];
            var handle = GCHandle.Alloc(_shapeBuf, GCHandleType.Pinned);
            try
            {
                dup.GetFramePointerShape((uint)size, handle.AddrOfPinnedObject(), out uint _, out OutduplPointerShapeInfo info);
                BuildCursorTexture(_shapeBuf, info);
            }
            finally { handle.Free(); }
        }
        catch { /* cursor is optional — on errors just don't draw it */ }
    }

    private void BuildCursorTexture(byte[] buf, OutduplPointerShapeInfo info)
    {
        int type = (int)info.Type;   // 1=MONOCHROME, 2=COLOR, 4=MASKED_COLOR
        int w = (int)info.Width;
        int h = (type == 1) ? (int)info.Height / 2 : (int)info.Height;
        int pitch = (int)info.Pitch;
        if (w <= 0 || h <= 0) return;

        var bgra = new byte[w * h * 4];
        if (type == 2 || type == 4) // COLOR / MASKED_COLOR
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = y * pitch + x * 4;
                    int d = (y * w + x) * 4;
                    bgra[d] = buf[s]; bgra[d + 1] = buf[s + 1]; bgra[d + 2] = buf[s + 2];
                    bgra[d + 3] = (type == 2) ? buf[s + 3] : (byte)255; // MASKED_COLOR → opaque
                }
        }
        else // MONOCHROME (AND mask + XOR mask, 1bpp)
        {
            int xorOff = h * pitch;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int bytePos = x / 8, bit = 7 - (x % 8);
                    int a = (buf[y * pitch + bytePos] >> bit) & 1;
                    int xo = (buf[xorOff + y * pitch + bytePos] >> bit) & 1;
                    int d = (y * w + x) * 4;
                    if (a == 0 && xo == 0) { bgra[d + 3] = 255; }                                   // black opaque
                    else if (a == 0 && xo == 1) { bgra[d] = bgra[d + 1] = bgra[d + 2] = bgra[d + 3] = 255; } // white opaque
                    else if (a == 1 && xo == 0) { bgra[d + 3] = 0; }                                 // transparent
                    else { bgra[d + 3] = 255; }                                                       // invert → black opaque (approximation)
                }
        }

        _cursorSrv?.Dispose();
        _cursorTex?.Dispose();
        var desc = new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.None,
        };
        var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            var data = new SubresourceData(pin.AddrOfPinnedObject(), (uint)(w * 4));
            _cursorTex = _device.CreateTexture2D(desc, new[] { data });
            _cursorSrv = _device.CreateShaderResourceView(_cursorTex);
            CursorW = w; CursorH = h;
        }
        finally { pin.Free(); }
    }

    /// <summary>Re-create the duplication after AccessLost (on the stored output).
    /// true = successfully re-created; false = failed (e.g. device lost / still no
    /// access) — the capture then stays in a CLEAN dead state (`_dup == null`),
    /// instead of triggering a null-ref endless loop in the render loop as a zombie. The caller
    /// should rebuild the whole render chain (device + capture) on false.</summary>
    public bool Recover()
    {
        try { _dup?.Dispose(); } catch { /* already gone — don't care */ }
        _dup = null;
        AwaitingAccess = false;
        try
        {
            _dup = _output.DuplicateOutput1(_device, 1, new[] { Format.R16G16B16A16_Float });
            // The most common trigger for ACCESS_LOST is a mode switch — e.g. HDR on/off.
            // So the source state must be re-read here, not carried over from before.
            RefreshSourceState();
            return true;
        }
        catch (Exception ex)
        {
            _dup = null;
            AwaitingAccess = IsDesktopUnavailable(ex);
            return false;
        }
    }

    public bool AwaitingAccess { get; private set; }

    // Duplication can't be created while the input desktop isn't the user's: the secure desktop of a
    // UAC prompt / the lock screen / Ctrl+Alt+Del (E_ACCESSDENIED — only LOCAL_SYSTEM may duplicate it),
    // a disconnected session, or "not currently available". All clear up by themselves.
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_SESSION_DISCONNECTED = unchecked((int)0x887A0028);

    private static bool IsDesktopUnavailable(Exception ex)
    {
        int hr = ex is SharpGenException sg ? sg.ResultCode.Code : ex.HResult;
        return hr is E_ACCESSDENIED or DXGI_ERROR_NOT_CURRENTLY_AVAILABLE or DXGI_ERROR_ACCESS_LOST
                  or DXGI_ERROR_SESSION_DISCONNECTED;
    }

    /// <summary>No-op: with monitor capture the renderer composites the cursor (separately from DXGI),
    /// so there is no source-side live state to track.</summary>
    public void ApplyLiveConfig(MirrorConfig cfg) { }

    public void Dispose()
    {
        _cursorSrv?.Dispose();
        _cursorTex?.Dispose();
        _srv?.Dispose();
        _copyTex?.Dispose();
        _dup?.Dispose();
    }
}

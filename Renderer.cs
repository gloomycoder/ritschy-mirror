using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;
using Vortice.Mathematics;

namespace RitschyMirror;

/// <summary>
/// Swapchain + shader pipeline. Draws the mirrored (optionally tonemapped) source
/// into the upper area of the output frame, the rest black (21:9 → 16:9 on top).
/// </summary>
public sealed class Renderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderParams
    {
        public float Exposure, SrcPeak, TargetPaperwhite, Saturation, Contrast, Gamma;
        public int OperatorId, TonemapEnabled, SrcIsLinear, OutputIsHdr;
        public float CropMinX, CropMinY, CropMaxX, CropMaxY;
        public float SrcScale, Pad0;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView _rtv = null!;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11PixelShader _psCursor;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _cbuffer;
    private readonly ID3D11BlendState _blend;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public Renderer(IDXGIFactory2 factory, ID3D11Device device, ID3D11DeviceContext ctx,
                    IntPtr hwnd, int width, int height, int bitDepth)
    {
        _device = device;
        _ctx = ctx;
        Width = width;
        Height = height;

        var format = bitDepth >= 10 ? Format.R10G10B10A2_UNorm : Format.B8G8R8A8_UNorm;
        var scDesc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = format,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };
        _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, scDesc);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        CreateRtv();

        // Compile shaders
        Compiler.Compile(Shaders.Hlsl, "VSMain", "tonemap.hlsl", "vs_5_0", out var vsBlob, out var vsErr);
        if (vsBlob is null) throw new Exception("VS-Compile: " + (vsErr?.AsString() ?? "unknown"));
        Compiler.Compile(Shaders.Hlsl, "PSMain", "tonemap.hlsl", "ps_5_0", out var psBlob, out var psErr);
        if (psBlob is null) throw new Exception("PS-Compile: " + (psErr?.AsString() ?? "unknown"));
        Compiler.Compile(Shaders.Hlsl, "PSCursor", "tonemap.hlsl", "ps_5_0", out var pcBlob, out var pcErr);
        if (pcBlob is null) throw new Exception("PSCursor-Compile: " + (pcErr?.AsString() ?? "unknown"));
        _vs = _device.CreateVertexShader(vsBlob.AsBytes());
        _ps = _device.CreatePixelShader(psBlob.AsBytes());
        _psCursor = _device.CreatePixelShader(pcBlob.AsBytes());
        vsBlob.Dispose(); psBlob.Dispose(); pcBlob.Dispose();

        // Alpha blend for the cursor (straight alpha over the finished image).
        _blend = _device.CreateBlendState(new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha));

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        _cbuffer = _device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<ShaderParams>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
    }

    private void CreateRtv()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backBuffer);
    }

    /// <summary>
    /// Draw one frame. Placement (viewport) + source crop come from
    /// <see cref="MirrorConfig.LayoutMode"/> — see <see cref="ComputeLayout"/>.
    /// Optionally the mouse cursor (delivered separately by DXGI) is composited in.
    /// </summary>
    public void Render(ICaptureSource cap, MirrorConfig cfg)
    {
        int srcW = cap.Width, srcH = cap.Height;
        var (vp, cropMinX, cropMinY, cropMaxX, cropMaxY) = ComputeLayout(srcW, srcH, cfg);

        var (srcScale, srcPeak) = ResolveSourceLight(cap.InputIsHdr, cfg);

        var p = new ShaderParams
        {
            Exposure = cfg.Exposure,
            SrcPeak = srcPeak,
            TargetPaperwhite = cfg.TargetPaperwhite,
            Saturation = cfg.Saturation,
            Contrast = cfg.Contrast,
            Gamma = cfg.Gamma,
            OperatorId = cfg.OperatorId,
            TonemapEnabled = cfg.TonemapEnabled ? 1 : 0,
            // Both sources (Desktop Duplication + WGC) deliver R16G16B16A16_FLOAT and thus
            // ALWAYS linear scRGB — regardless of the monitor's HDR mode.
            SrcIsLinear = 1,
            OutputIsHdr = 0, // SDR output (passthrough HDR is phase 2)
            CropMinX = cropMinX, CropMinY = cropMinY,
            CropMaxX = cropMaxX, CropMaxY = cropMaxY,
            SrcScale = srcScale,
        };
        _ctx.UpdateSubresource(p, _cbuffer);

        _ctx.ClearRenderTargetView(_rtv, new Color4(0f, 0f, 0f, 1f));

        // shared pipeline states
        _ctx.OMSetRenderTargets(_rtv);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetSampler(0, _sampler);
        _ctx.PSSetConstantBuffer(0, _cbuffer);

        // Main pass (opaque)
        _ctx.OMSetBlendState(null);
        _ctx.RSSetViewport(vp);
        _ctx.PSSetShader(_ps);
        _ctx.PSSetShaderResource(0, cap.Srv);
        _ctx.Draw(3, 0);

        // Cursor pass (alpha) — map the source cursor position into the output viewport.
        if (cfg.ShowCursor && cap.CursorVisible && cap.CursorSrv != null && cap.CursorW > 0)
        {
            float crW = cropMaxX - cropMinX, crH = cropMaxY - cropMinY;
            float nx = ((float)cap.CursorX / srcW - cropMinX) / crW;
            float ny = ((float)cap.CursorY / srcH - cropMinY) / crH;
            float nw = ((float)cap.CursorW / srcW) / crW;
            float nh = ((float)cap.CursorH / srcH) / crH;
            if (nx < 1f && ny < 1f && nx + nw > 0f && ny + nh > 0f) // at least partly visible
            {
                var cvp = new Viewport(vp.X + nx * vp.Width, vp.Y + ny * vp.Height,
                                       nw * vp.Width, nh * vp.Height, 0f, 1f);
                _ctx.OMSetBlendState(_blend);
                _ctx.RSSetViewport(cvp);
                _ctx.PSSetShader(_psCursor);
                _ctx.PSSetShaderResource(0, cap.CursorSrv);
                _ctx.Draw(3, 0);
                _ctx.OMSetBlendState(null);
            }
        }
    }

    /// <summary>
    /// Viewport (placement in the output frame) + source crop (UV 0..1) from layout_mode:
    ///   fit         — whole source centered, aspect ratio preserved (nothing cropped)
    ///   stretch     — source fills the frame (aspect ratio ignored)
    ///   top_strip   — source scaled to width, top-aligned, rest black (21:9→16:9)
    ///   crop_region — like fit, but only the source rectangle crop_x/y/w/h
    /// content_offset_y shifts vertically.
    /// </summary>
    private (Viewport vp, float cMinX, float cMinY, float cMaxX, float cMaxY)
        ComputeLayout(int srcW, int srcH, MirrorConfig cfg)
    {
        float W = Width, H = Height;
        string mode = (cfg.LayoutMode ?? "top_strip").Trim().ToLowerInvariant();

        // Crop UVs — only crop_region uses them; otherwise the full source (0..1).
        float cMinX = 0f, cMinY = 0f, cMaxX = 1f, cMaxY = 1f;
        float effSrcW = srcW, effSrcH = srcH;
        if (mode == "crop_region")
        {
            cMinX = System.Math.Clamp(cfg.CropX, 0f, 1f);
            cMinY = System.Math.Clamp(cfg.CropY, 0f, 1f);
            cMaxX = System.Math.Clamp(cfg.CropX + cfg.CropW, 0f, 1f);
            cMaxY = System.Math.Clamp(cfg.CropY + cfg.CropH, 0f, 1f);
            if (cMaxX <= cMinX) cMaxX = System.Math.Min(1f, cMinX + 0.01f);
            if (cMaxY <= cMinY) cMaxY = System.Math.Min(1f, cMinY + 0.01f);
            effSrcW = srcW * (cMaxX - cMinX);
            effSrcH = srcH * (cMaxY - cMinY);
        }
        if (effSrcW < 1f) effSrcW = 1f;
        if (effSrcH < 1f) effSrcH = 1f;

        Viewport vp;
        switch (mode)
        {
            case "stretch":
                vp = new Viewport(0f, cfg.ContentOffsetY, W, H, 0f, 1f);
                break;
            case "top_strip":
            {
                float stripH = (float)System.Math.Round(W * (double)effSrcH / effSrcW);
                vp = new Viewport(0f, cfg.ContentOffsetY, W, stripH, 0f, 1f);
                break;
            }
            default: // "fit" and "crop_region": aspect ratio preserved, centered
            {
                double srcAspect = (double)effSrcW / effSrcH;
                double outAspect = (double)W / H;
                float vpW, vpH;
                if (srcAspect > outAspect) { vpW = W; vpH = (float)(W / srcAspect); }
                else                       { vpH = H; vpW = (float)(H * srcAspect); }
                float ox = (W - vpW) / 2f;
                float oy = (H - vpH) / 2f + cfg.ContentOffsetY;
                vp = new Viewport(ox, oy, vpW, vpH, 0f, 1f);
                break;
            }
        }
        return (vp, cMinX, cMinY, cMaxX, cMaxY);
    }

    /// <summary>
    /// From the source's state, derive the two quantities the shader works with —
    /// instead of hard-wiring "HDR" as a special case in the shader:
    ///   Scale — factor from buffer value to working light, where 1.0 is the white point.
    ///           HDR/scRGB: 1.0 in the buffer is 80 nits → normalize to target_paperwhite.
    ///           SDR source: white in the buffer IS already the white point → 1.0 = pass through 1:1.
    ///   Peak  — how far the source extends beyond the white point (tonemap headroom).
    ///           HDR: source_peak_nits / target_paperwhite (at least 1.0).
    ///           SDR: 1.0 = no headroom → tonemapping drops out by itself, instead of
    ///           compressing an already finished SDR image a second time.
    /// </summary>
    private static (float Scale, float Peak) ResolveSourceLight(bool srcIsHdr, MirrorConfig cfg)
    {
        if (!srcIsHdr) return (1f, 1f);
        float paperwhite = System.Math.Max(cfg.TargetPaperwhite, 1f);
        return (80f / paperwhite, System.Math.Max(cfg.SourcePeakNits / paperwhite, 1f));
    }

    public void Present(bool vsync) => _swapChain.Present(vsync ? 1u : 0u, PresentFlags.None);

    /// <summary>
    /// Try true DXGI exclusive fullscreen. Fails if the target output is attached to a
    /// DIFFERENT adapter than the render device (cross-adapter) — then false → caller
    /// falls back to borderless + mouse lock.
    /// </summary>
    public bool TrySetExclusiveFullscreen(IDXGIOutput? output)
    {
        try { _swapChain.SetFullscreenState(true, output); return true; }
        catch { return false; }
    }

    /// <summary>Leave exclusive fullscreen — MUST happen before swapchain dispose (DXGI requirement).</summary>
    public void ExitFullscreen()
    {
        try { _swapChain.SetFullscreenState(false, null); } catch { }
    }

    public void Dispose()
    {
        _rtv?.Dispose();
        _cbuffer?.Dispose();
        _blend?.Dispose();
        _sampler?.Dispose();
        _psCursor?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
        _swapChain?.Dispose();
    }
}

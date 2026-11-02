using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RitschyMirror;

/// <summary>
/// The capture/output FPS readout drawn on top of the mirror (verification aid).
///
/// The text changes at most once per second, so instead of a glyph atlas the whole label is
/// rasterised with GDI+ into a small BGRA bitmap and uploaded as an immutable texture whenever it
/// changes. That keeps it independent of the swap-chain format (D2D can't target R10G10B10A2) and
/// lets the renderer draw it with the existing straight-alpha cursor pass.
/// </summary>
public sealed class FpsOverlay : IDisposable
{
    private const int MinFontPx = 14, MaxFontPx = 64;
    private const int FontDivisor = 40;   // font height = output height / 40 (27 px at 1080p)

    private readonly ID3D11Device _device;
    private readonly int _fontPx;
    private ID3D11Texture2D? _tex;
    private string _text = "";

    public ID3D11ShaderResourceView? Srv { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public FpsOverlay(ID3D11Device device, int outputHeight)
    {
        _device = device;
        _fontPx = Math.Clamp(outputHeight / FontDivisor, MinFontPx, MaxFontPx);
    }

    /// <summary>Label text for the given rates, e.g. "CAP 59.9  OUT 60.0 fps".</summary>
    public static string FormatRates(double captureFps, double outputFps) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "CAP {0,5:0.0}  OUT {1,5:0.0} fps", captureFps, outputFps);

    /// <summary>Re-rasterise only when the text actually changed; true if it did.</summary>
    public bool SetText(string text)
    {
        if (text == _text && Srv != null) return false;
        Upload(Rasterize(text));
        _text = text;
        return true;
    }

    private Bitmap Rasterize(string text)
    {
        using var font = new Font(FontFamily.GenericMonospace, _fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
        Size size;
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
            size = Size.Ceiling(g.MeasureString(text, font));
        int pad = Math.Max(4, _fontPx / 4);
        var bmp = new Bitmap(size.Width + pad * 2, size.Height + pad, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(170, 0, 0, 0)); // translucent plate → readable on any content
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Grayscale AA: ClearType needs an opaque background and would fringe on the plate.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(Color.White);
            g.DrawString(text, font, brush, pad, pad / 2f);
        }
        return bmp;
    }

    private void Upload(Bitmap bmp)
    {
        using (bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var desc = new Texture2DDescription
                {
                    Width = (uint)bmp.Width,
                    Height = (uint)bmp.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, // GDI+ 32bppArgb is BGRA in memory, straight alpha
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                };
                var tex = _device.CreateTexture2D(desc, new[] { new SubresourceData(data.Scan0, (uint)data.Stride) });
                var srv = _device.CreateShaderResourceView(tex);
                Release();
                _tex = tex;
                Srv = srv;
                Width = bmp.Width;
                Height = bmp.Height;
            }
            finally { bmp.UnlockBits(data); }
        }
    }

    private void Release()
    {
        Srv?.Dispose();
        _tex?.Dispose();
        Srv = null;
        _tex = null;
    }

    public void Dispose() => Release();
}

using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace RitschyMirror;

/// <summary>
/// Finds out how a DXGI Desktop Duplication FP16 buffer encodes an SDR desktop.
///
/// Measured on an NVIDIA-driven display: the buffer is linear (sRGB grey 128 reads back as 0.2158 =
/// srgb_to_linear). With a display on an AMD Radeon the same request was reported to hand back the
/// sRGB-encoded values instead, which a linear reading shows washed out. So instead of assuming one,
/// a few pixels of a captured frame are compared with what Windows itself shows (GDI, 8-bit sRGB) at
/// the same spots: linear if they match srgb_to_linear(gdi), sRGB if they match gdi directly.
/// </summary>
internal static class SdrEncodingProbe
{
    public enum Result { Unknown, Linear, Srgb }

    private const int Cols = 6, Rows = 4;              // sample grid (inside a 10 % margin)
    private const float MinTone = 0.08f, MaxTone = 0.92f;   // near black/white both readings agree
    private const float MaxError = 0.03f;
    private const int MinVotes = 8;
    private const double MinAgreement = 0.8;

    /// <summary>
    /// Compare <paramref name="frame"/> (FP16 copy of the output, unrotated) with the screen at
    /// desktop position (<paramref name="left"/>, <paramref name="top"/>). Stalls for one GPU readback
    /// and one GDI copy of the output — call it rarely (session start, after a mode change).
    /// </summary>
    public static (Result result, string detail) Run(ID3D11Device device, ID3D11DeviceContext ctx,
        ID3D11Texture2D frame, int left, int top, int width, int height)
    {
        var points = new List<(int X, int Y)>();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                points.Add(((int)(width * (0.1 + 0.8 * c / (Cols - 1))), (int)(height * (0.1 + 0.8 * r / (Rows - 1)))));

        var fp16 = ReadFrame(device, ctx, frame, points);
        var gdi = ReadScreen(left, top, width, height, points);
        if (gdi is null) return (Result.Unknown, "screen not readable");

        int linear = 0, srgb = 0;
        for (int i = 0; i < points.Count; i++)
            for (int ch = 0; ch < 3; ch++)
            {
                float s = gdi[i][ch], f = fp16[i][ch];
                if (s < MinTone || s > MaxTone) continue;
                float errLinear = MathF.Abs(f - SrgbToLinear(s)), errSrgb = MathF.Abs(f - s);
                if (errLinear < MaxError && errLinear < errSrgb * 0.5f) linear++;
                else if (errSrgb < MaxError && errSrgb < errLinear * 0.5f) srgb++;
            }
        int votes = linear + srgb;
        string detail = $"{linear} linear / {srgb} sRGB of {points.Count * 3} channel samples";
        if (votes < MinVotes) return (Result.Unknown, detail + " (too few mid-tones)");
        if (linear >= votes * MinAgreement) return (Result.Linear, detail);
        if (srgb >= votes * MinAgreement) return (Result.Srgb, detail);
        return (Result.Unknown, detail + " (mixed — screen changing?)");
    }

    private static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>RGB of each point from the FP16 frame (one small staging texture, one readback).</summary>
    private static float[][] ReadFrame(ID3D11Device device, ID3D11DeviceContext ctx, ID3D11Texture2D frame,
        List<(int X, int Y)> points)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)points.Count, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
        };
        using var staging = device.CreateTexture2D(desc);
        for (int i = 0; i < points.Count; i++)
        {
            var (x, y) = points[i];
            var box = new Box(x, y, 0, x + 1, y + 1, 1);
            ctx.CopySubresourceRegion(staging, 0, (uint)i, 0, 0, frame, 0, box);
        }
        var map = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var result = new float[points.Count][];
            for (int i = 0; i < points.Count; i++)
            {
                var px = new float[3];
                for (int ch = 0; ch < 3; ch++)
                    px[ch] = (float)BitConverter.UInt16BitsToHalf((ushort)Marshal.ReadInt16(map.DataPointer, i * 8 + ch * 2));
                result[i] = px;
            }
            return result;
        }
        finally { ctx.Unmap(staging, 0); }
    }

    /// <summary>RGB (0..1) of each point as Windows shows it: one GDI copy of the output area.</summary>
    private static float[][]? ReadScreen(int left, int top, int width, int height, List<(int X, int Y)> points)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFO { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        IntPtr bits;
        IntPtr dib = CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
        try
        {
            if (mem == IntPtr.Zero || dib == IntPtr.Zero) return null;
            IntPtr old = SelectObject(mem, dib);
            bool ok = BitBlt(mem, 0, 0, width, height, screen, left, top, SRCCOPY);
            SelectObject(mem, old);
            if (!ok) return null;
            var result = new float[points.Count][];
            for (int i = 0; i < points.Count; i++)
            {
                int bgra = Marshal.ReadInt32(bits, (points[i].Y * width + points[i].X) * 4);
                result[i] = new[] { ((bgra >> 16) & 0xFF) / 255f, ((bgra >> 8) & 0xFF) / 255f, (bgra & 0xFF) / 255f };
            }
            return result;
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hDc, ref BITMAPINFO bmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int sx, int sy, uint rop);
}

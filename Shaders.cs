namespace RitschyMirror;

/// <summary>
/// HLSL source, compiled at runtime via D3DCompiler. The vertex shader draws
/// a full-screen triangle (no vertex buffer); the pixel shader does
/// HDR→SDR tonemapping + image corrections. The cbuffer fields MUST match the
/// renderer's params struct field by field (same order/size, 16-byte aligned).
/// </summary>
public static class Shaders
{
    public const string Hlsl = @"
Texture2D    Src : register(t0);
SamplerState Smp : register(s0);

cbuffer Params : register(b0)
{
    float Exposure;          // Exposure in stops
    float SrcPeak;           // Source peak luminance in WHITE-POINT units (1.0 = no headroom)
    float TargetPaperwhite;  // SDR white point in nits (only used for the HDR output path now)
    float Saturation;
    float Contrast;
    float Gamma;
    int   OperatorId;        // 0 bt2390, 1 reinhard, 2 hable, 3 aces
    int   TonemapEnabled;    // 0/1
    int   SrcIsLinear;       // 1 = buffer is already linear (FP16/scRGB), 0 = sRGB-encoded
    int   OutputIsHdr;       // 1 = HDR passthrough (no tonemap, scRGB out)
    float2 CropMin;          // Source crop in UV (0..1), default (0,0)
    float2 CropMax;          // Source crop in UV (0..1), default (1,1)
    float SrcScale;          // Buffer value → working light (1.0 = white point)
    float _pad;
};

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2); // (0,0) (2,0) (0,2)
    o.uv  = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

// ── Color helpers ───────────────────────────────────────────────────────────
float3 SrgbToLinear(float3 c)
{
    return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}
float3 LinearToSrgb(float3 c)
{
    c = saturate(c);
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}
float Luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

// ── Tonemap operators (input linear, 1.0 = white point) ──────────────────
float3 TmReinhard(float3 x, float wp)
{
    // Extended Reinhard with white point
    float3 num = x * (1.0 + x / (wp * wp));
    return num / (1.0 + x);
}
float3 TmHable(float3 x)
{
    const float A=0.15,B=0.50,C=0.10,D=0.20,E=0.02,F=0.30;
    return ((x*(A*x+C*B)+D*E)/(x*(A*x+B)+D*F))-E/F;
}
float3 Hable(float3 x, float wp)
{
    float3 w = TmHable(float3(wp,wp,wp));
    return TmHable(x) / w;
}
float3 TmAces(float3 x)
{
    const float a=2.51,b=0.03,c=2.43,d=0.59,e=0.14;
    return saturate((x*(a*x+b))/(x*(c*x+d)+e));
}
// Simplified BT.2390-style roll-off on the luminance
float3 TmBt2390(float3 x, float wp)
{
    float L = max(Luminance(x), 1e-5);
    float Lt = (L * (1.0 + L / (wp * wp))) / (1.0 + L); // soft knee curve
    return x * (Lt / L);
}

float4 PSMain(VSOut i) : SV_Target
{
    // Source crop: map the visible UV (0..1 across the viewport) onto the source's
    // crop rectangle. Without crop CropMin=(0,0)/CropMax=(1,1) → identity.
    float2 suv = CropMin + i.uv * (CropMax - CropMin);
    float3 col = Src.Sample(Smp, suv).rgb;

    // Input color space -> linear working light (1.0 = white point).
    // IMPORTANT: DXGI Desktop Duplication and WGC with R16G16B16A16_FLOAT ALWAYS deliver
    // linear scRGB — even when the monitor runs in SDR mode (measured). sRGB-decoding
    // there would be a SECOND gamma and makes the image dramatically too dark. So only a
    // truly sRGB-encoded buffer (8-bit sources) gets decoded.
    if (SrcIsLinear)
        col = max(col, 0);
    else
        col = SrgbToLinear(saturate(col));
    col *= SrcScale;   // scRGB: 80/white point · SDR source: 1.0 (white is already white)

    col *= exp2(Exposure);

    // Tonemap only if the source has any headroom ABOVE the white point at all.
    // An SDR source (SrcPeak == 1) has none — rolling it off would merely darken an already
    // finished 1:1 image. This way the tonemapper itself notices whether HDR is present.
    if (TonemapEnabled && !OutputIsHdr && SrcPeak > 1.0001)
    {
        float wp = SrcPeak;
        if      (OperatorId == 1) col = TmReinhard(col, wp);
        else if (OperatorId == 2) col = Hable(col, wp);
        else if (OperatorId == 3) col = TmAces(col);
        else                      col = TmBt2390(col, wp);
    }

    // Saturation
    float l = Luminance(col);
    col = lerp(float3(l, l, l), col, Saturation);
    // Contrast around mid-gray
    col = (col - 0.18) * Contrast + 0.18;
    col = max(col, 0);
    // Gamma trim
    col = pow(max(col, 1e-6), 1.0 / max(Gamma, 1e-3));

    if (OutputIsHdr)
        return float4(col * TargetPaperwhite / 80.0, 1); // back to scRGB
    return float4(LinearToSrgb(col), 1);
}

// Mouse cursor: already-decoded BGRA texture (display-referred), output directly with alpha.
float4 PSCursor(VSOut i) : SV_Target
{
    return Src.Sample(Smp, i.uv);
}
";
}

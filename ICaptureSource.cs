using Vortice.Direct3D11;

namespace RitschyMirror;

/// <summary>
/// Common interface for an image source that the <see cref="Renderer"/> mirrors —
/// regardless of HOW the image is produced. Two implementations:
///   • <see cref="DuplicationCapture"/> — a whole monitor (DXGI Desktop Duplication).
///   • <see cref="WgcCapture"/>         — a single window / a fullscreen app, or a whole monitor (WGC)
///                                         (Windows.Graphics.Capture, like OBS' window capture).
///
/// The renderer only consumes this seam (one FP16 SRV + dimensions + cursor info); the whole
/// pipeline after it (tonemap/layout/crop/present) stays identical for both sources. New
/// source types can be plugged in this way without touching the renderer or the engine loop.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>Width/height of the current source in real pixels (monitor or window content).</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>True if the source delivers HDR → the tonemap shader rolls off the highlights.
    /// If false the image is already SDR and passed through 1:1 (no tonemapping).
    /// ⚠ This describes the VALUE RANGE, not the encoding: both sources deliver
    /// R16G16B16A16_FLOAT and therefore always LINEAR scRGB — even in SDR mode.</summary>
    bool InputIsHdr { get; }

    /// <summary>Re-read source-side state that can change during operation — above all
    /// HDR on/off at the monitor. Called periodically and after every <see cref="Recover"/>,
    /// so an HDR toggle does not keep running with the wrong color math until the next
    /// render restart. Sources without changeable state: no-op.</summary>
    void RefreshSourceState();

    /// <summary>The FP16 buffer holds an SDR desktop sRGB-encoded rather than linear — the shader then
    /// decodes it. Only DXGI on some drivers (see <see cref="SdrEncodingProbe"/>); WGC: always false.</summary>
    bool SourceIsSrgbEncoded { get; }

    /// <summary>Shader-readable copy of the last frame (R16G16B16A16_Float). null = no frame yet.</summary>
    ID3D11ShaderResourceView? Srv { get; }

    // ── Mouse cursor (delivered separately by DXGI; only monitor capture uses this, window capture
    //    already bakes the cursor into the frame → permanently reports "not visible" here). ─────────
    bool CursorVisible { get; }
    int CursorX { get; }
    int CursorY { get; }
    int CursorW { get; }
    int CursorH { get; }
    ID3D11ShaderResourceView? CursorSrv { get; }

    /// <summary>Fetch the NEWEST frame + copy it into the SRV, waiting up to <paramref name="timeoutMs"/>
    /// for one (0 = just poll). true = new frame, false = timeout/none.
    /// Throws on a real loss (e.g. ACCESS_LOST / source gone) → the render loop then decides on
    /// recovery (<see cref="Recover"/>) or a full reinit.</summary>
    bool TryAcquire(ID3D11DeviceContext ctx, int timeoutMs = 16);

    /// <summary>Set the source up again after a loss without a full reinit (e.g. re-create the duplication
    /// / re-create the frame pool). true = recovered, false = unrecoverable (the caller
    /// rebuilds the whole render chain). On false it leaves a clean state (no spinning
    /// on a half-dead resource).</summary>
    bool Recover();

    /// <summary>True after a failed <see cref="Recover"/> whose only cause is that the desktop can't be
    /// captured right now — the secure desktop (UAC prompt, lock screen, Ctrl+Alt+Del) or a
    /// disconnected session. Nothing is broken: the session keeps its window and device and simply
    /// calls <see cref="Recover"/> again later. False = a real failure (full reinit).</summary>
    bool AwaitingAccess { get; }

    /// <summary>Apply live config (hot reload) to the source as far as it takes effect source-side.
    /// Example: window capture toggles the WGC session's cursor capture live. Sources without
    /// such state (monitor capture: the renderer handles the cursor) implement this as a no-op.</summary>
    void ApplyLiveConfig(MirrorConfig cfg);
}

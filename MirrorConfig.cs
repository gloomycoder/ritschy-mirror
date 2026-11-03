using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RitschyMirror;

/// <summary>
/// Runtime configuration, read from mirror_config.json.
///
/// Two effect classes (see <see cref="LiveKeys"/> / <see cref="StructKeys"/>):
///   • LIVE   — image/geometry, takes effect immediately via hot reload (FileWatcher in MirrorEngine).
///   • STRUCT — resolution/display/window mode, takes effect only on render restart.
///
/// The file is the SINGLE source of truth: the local GUI, the local HTTP agent and (as a fallback)
/// the cockpit via WinRM all write the same file; the render loop reads it live.
/// </summary>
public sealed class MirrorConfig
{
    // ── Tonemapping / image (live) ────────────────────────────────────────
    [JsonPropertyName("tonemap_enabled")]  public bool TonemapEnabled { get; set; } = true;
    [JsonPropertyName("operator")]         public string Operator { get; set; } = "bt2390";
    [JsonPropertyName("source_peak_nits")] public float SourcePeakNits { get; set; } = 1000f;
    [JsonPropertyName("target_paperwhite")] public float TargetPaperwhite { get; set; } = 200f;
    [JsonPropertyName("exposure")]         public float Exposure { get; set; } = 0f;
    [JsonPropertyName("saturation")]       public float Saturation { get; set; } = 1f;
    [JsonPropertyName("contrast")]         public float Contrast { get; set; } = 1f;
    [JsonPropertyName("gamma")]            public float Gamma { get; set; } = 1f;
    [JsonPropertyName("content_offset_y")] public int ContentOffsetY { get; set; } = 0;

    // ── Layout / geometry (live) ────────────────────────────────────────
    // layout_mode: "fit" (1:1, nothing cropped) | "stretch" | "top_strip" (21:9→16:9 top)
    //              | "crop_region" (freely defined source rectangle)
    [JsonPropertyName("layout_mode")]      public string LayoutMode { get; set; } = "top_strip";
    // Source crop in 0..1 (only for layout_mode="crop_region"): section of the source.
    [JsonPropertyName("crop_x")]           public float CropX { get; set; } = 0f;
    [JsonPropertyName("crop_y")]           public float CropY { get; set; } = 0f;
    [JsonPropertyName("crop_w")]           public float CropW { get; set; } = 1f;
    [JsonPropertyName("crop_h")]           public float CropH { get; set; } = 1f;
    // Composite the mouse cursor into the output (Desktop Duplication delivers it separately).
    [JsonPropertyName("show_cursor")]      public bool ShowCursor { get; set; } = false;
    [JsonPropertyName("vsync")]            public bool Vsync { get; set; } = true;
    // Resampling when source crop and output differ in size (1:1 is never filtered):
    // "bilinear" (default, 1 pass) | "area" | "bicubic" (Mitchell) | "catmull_rom" | "lanczos2" | "lanczos3".
    [JsonPropertyName("scale_filter")]     public string ScaleFilter { get; set; } = "bilinear";
    // Prevent sleep + monitor power-off while mirroring (like a video player).
    // On by default; can be toggled live (takes effect immediately on the running mirror).
    [JsonPropertyName("keep_awake")]       public bool KeepAwake { get; set; } = true;

    // ── Frame pacing / diagnostics / audio (live) ───────────────────────
    // Frame limit while VSync is OFF (0 = unlimited). Ignored with VSync on — the display paces then.
    [JsonPropertyName("target_fps")]       public int TargetFps { get; set; } = 0;
    // Capture/output FPS readout drawn onto the mirror (verification aid; it IS visible on the output monitor / capture card).
    [JsonPropertyName("fps_overlay")]      public bool FpsOverlay { get; set; } = false;
    // Overlay corner: "top_left" | "top_right" | "bottom_left" | "bottom_right".
    [JsonPropertyName("fps_overlay_position")] public string FpsOverlayPosition { get; set; } = "top_left";
    // Copy the default playback device to the target monitor's HDMI/DP audio endpoint.
    [JsonPropertyName("audio_mirror")]     public bool AudioMirror { get; set; } = false;
    // Devices mixed into the audio mirror (checkbox list in Settings), each with its own gain.
    // Missing in older configs → just the default output device, as before.
    [JsonPropertyName("audio_sources")]    public List<AudioSourceConfig> AudioSources { get; set; } = AudioSourceConfig.Defaults();
    // Group gains on top of each source's own gain: effective = group dB + device dB.
    [JsonPropertyName("audio_render_gain_db")]  public float AudioRenderGainDb { get; set; } = 0f;
    [JsonPropertyName("audio_capture_gain_db")] public float AudioCaptureGainDb { get; set; } = 0f;
    // Output-device sources follow that device's Windows volume + mute where the loopback signal
    // doesn't already contain them (see LoopbackVolume). Off = the mirror gets the loopback signal as is.
    [JsonPropertyName("audio_follow_volume")]  public bool AudioFollowVolume { get; set; } = true;
    // Fixed output endpoint for the mix ("" = automatic: the target monitor's HDMI/DP audio by EDID name).
    [JsonPropertyName("audio_output_id")]   public string AudioOutputId { get; set; } = "";
    [JsonPropertyName("audio_output_name")] public string AudioOutputName { get; set; } = "";

    // ── Source: whole monitor OR a window (restart required) ────────
    // capture_mode: "monitor" (whole screen via Desktop Duplication, default) |
    //               "window"  (a window / a fullscreen app via Windows.Graphics.Capture).
    // In window mode ONLY the window is mirrored → the desktop stays invisible when alt-tabbing out.
    [JsonPropertyName("capture_mode")]     public string CaptureMode { get; set; } = "monitor";
    // Monitor capture API (capture_mode="monitor"): "dxgi" (Desktop Duplication, default) |
    // "wgc" (Windows.Graphics.Capture — cross-GPU, cursor composited by Windows).
    [JsonPropertyName("monitor_api")]      public string MonitorApi { get; set; } = "dxgi";
    // Window identity (only capture_mode="window"): exe (primary, stable) + title (secondary, to
    // tell apart multiple windows of the same exe). Analogous to the replug-proof monitor identity.
    [JsonPropertyName("window_exe")]       public string WindowExe { get; set; } = "";
    [JsonPropertyName("window_title")]     public string WindowTitle { get; set; } = "";
    // Resume on a window whose title changed slightly (extended / numbers only, e.g. a game's build
    // number). Off = exact title only — a similar-looking other window is never taken.
    [JsonPropertyName("window_title_similar")] public bool WindowTitleSimilar { get; set; } = false;

    // ── Structure (restart required) ───────────────────────────────────────
    [JsonPropertyName("output_bit_depth")] public int OutputBitDepth { get; set; } = 10;
    // Monitor selection: preferably via stable identity (key = monitorDevicePath, label = EDID name),
    // so that replugging / a changed enumeration order does NOT scramble the selection. The index
    // is kept as a backward-compat fallback (old configs without a key).
    [JsonPropertyName("source_display")]   public int SourceDisplay { get; set; } = 0;
    [JsonPropertyName("target_display")]   public int TargetDisplay { get; set; } = 0;
    [JsonPropertyName("source_key")]       public string SourceKey { get; set; } = "";
    [JsonPropertyName("source_label")]     public string SourceLabel { get; set; } = "";
    [JsonPropertyName("target_key")]       public string TargetKey { get; set; } = "";
    [JsonPropertyName("target_label")]     public string TargetLabel { get; set; } = "";
    // output_mode: "windowed" | "borderless" | "fullscreen_block" (mouse lock) | "exclusive".
    // Empty = derived from the old `windowed` flag (backward compatibility).
    [JsonPropertyName("output_mode")]      public string OutputMode { get; set; } = "";
    [JsonPropertyName("windowed")]         public bool Windowed { get; set; } = true;
    [JsonPropertyName("window_width")]     public int WindowWidth { get; set; } = 1280;
    [JsonPropertyName("window_height")]    public int WindowHeight { get; set; } = 720;
    // 0 = derive from the target display; >0 = override.
    [JsonPropertyName("output_width")]     public int OutputWidth { get; set; } = 0;
    [JsonPropertyName("output_height")]    public int OutputHeight { get; set; } = 0;

    /// <summary>Operator string → shader constant (must match Tonemap.hlsl).</summary>
    public int OperatorId => Operator?.ToLowerInvariant() switch
    {
        "reinhard" => 1,
        "hable"    => 2,
        "aces"     => 3,
        _          => 0, // bt2390 (Default)
    };

    /// <summary>Effective window mode — an empty output_mode falls back to the old windowed flag.</summary>
    public string ResolveOutputMode()
    {
        var m = (OutputMode ?? "").Trim().ToLowerInvariant();
        if (m.Length == 0) return Windowed ? "windowed" : "borderless";
        return m;
    }

    public const int MinTargetFps = 1, MaxTargetFps = 1000;

    /// <summary>scale_filter values in UI order; the id is the shader's FilterId.</summary>
    public static readonly string[] ScaleFilters = { "bilinear", "area", "bicubic", "catmull_rom", "lanczos2", "lanczos3" };
    public const int BilinearFilterId = 1;

    public int ScaleFilterId => (ScaleFilter ?? "").Trim().ToLowerInvariant() switch
    {
        "area"        => 2,
        "bicubic"     => 3,
        "catmull_rom" => 4,
        "lanczos2"    => 5,
        "lanczos3"    => 6,
        _             => BilinearFilterId,
    };
    public static readonly string[] OverlayPositions = { "top_left", "top_right", "bottom_left", "bottom_right" };

    /// <summary>Frame limit actually in effect: only with VSync off (0 = unlimited / VSync paces).</summary>
    public int EffectiveTargetFps => Vsync || TargetFps <= 0 ? 0 : Math.Clamp(TargetFps, MinTargetFps, MaxTargetFps);

    /// <summary>Effective monitor capture API: "wgc" or (default) "dxgi".</summary>
    public string ResolveMonitorApi()
        => (MonitorApi ?? "").Trim().ToLowerInvariant() == "wgc" ? "wgc" : "dxgi";

    /// <summary>Effective source mode: "window" or (default) "monitor".</summary>
    public string ResolveCaptureMode()
        => (CaptureMode ?? "").Trim().ToLowerInvariant() == "window" ? "window" : "monitor";

    // ── Key whitelist (against injection; LIVE takes effect immediately) ─────────
    public static readonly HashSet<string> LiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "tonemap_enabled", "operator", "source_peak_nits", "target_paperwhite",
        "exposure", "saturation", "contrast", "gamma", "content_offset_y", "vsync",
        "layout_mode", "crop_x", "crop_y", "crop_w", "crop_h", "show_cursor", "keep_awake",
        "target_fps", "fps_overlay", "fps_overlay_position", "audio_mirror", "audio_sources", "scale_filter",
        "audio_render_gain_db", "audio_capture_gain_db", "audio_output_id", "audio_output_name",
        "audio_follow_volume", "window_title_similar",
    };
    public static readonly HashSet<string> StructKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "output_bit_depth", "source_display", "target_display",
        "source_key", "source_label", "target_key", "target_label",
        "output_mode", "windowed",
        "window_width", "window_height", "output_width", "output_height",
        "capture_mode", "monitor_api", "window_exe", "window_title",
    };
    public static IEnumerable<string> AllKeys => LiveKeys.Concat(StructKeys);

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static MirrorConfig Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MirrorConfig>(json, ReadOpts) ?? new MirrorConfig();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] Read error ({path}): {ex.Message} — using defaults/last state.");
            return new MirrorConfig();
        }
    }

    /// <summary>Write the whole object (used by the GUI).</summary>
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOpts));

    /// <summary>
    /// Read-modify-write: merge only whitelisted keys from <paramref name="patch"/> into the
    /// existing file (unknown fields are preserved). Mirrors the semantics
    /// of cockpit/services/mirror_control.set_config — so the WinRM path stays compatible.
    /// Returns the fresh JSON object.
    /// </summary>
    public static JsonObject MergePatch(string path, JsonObject patch)
    {
        JsonObject current;
        try { current = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
        catch { current = new JsonObject(); }

        foreach (var kv in patch)
        {
            if (!LiveKeys.Contains(kv.Key) && !StructKeys.Contains(kv.Key)) continue;
            current[kv.Key] = kv.Value?.DeepClone();
        }
        File.WriteAllText(path, current.ToJsonString(WriteOpts));
        return current;
    }
}

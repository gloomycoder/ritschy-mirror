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
    // Prevent sleep + monitor power-off while mirroring (like a video player).
    // On by default; can be toggled live (takes effect immediately on the running mirror).
    [JsonPropertyName("keep_awake")]       public bool KeepAwake { get; set; } = true;

    // ── Frame pacing / diagnostics / audio (live) ───────────────────────
    // Frame limit while VSync is OFF (0 = unlimited). Ignored with VSync on — the display paces then.
    [JsonPropertyName("target_fps")]       public int TargetFps { get; set; } = 0;
    // Capture/output FPS readout drawn onto the mirror (verification aid; it IS visible on the capture card).
    [JsonPropertyName("fps_overlay")]      public bool FpsOverlay { get; set; } = false;
    // Overlay corner: "top_left" | "top_right" | "bottom_left" | "bottom_right".
    [JsonPropertyName("fps_overlay_position")] public string FpsOverlayPosition { get; set; } = "top_left";
    // Copy the default playback device to the target monitor's HDMI/DP audio endpoint.
    [JsonPropertyName("audio_mirror")]     public bool AudioMirror { get; set; } = false;

    // ── Source: whole monitor OR a window (restart required) ────────
    // capture_mode: "monitor" (whole screen via Desktop Duplication, default) |
    //               "window"  (a window / a fullscreen app via Windows.Graphics.Capture).
    // In window mode ONLY the window is mirrored → the desktop stays invisible when alt-tabbing out.
    [JsonPropertyName("capture_mode")]     public string CaptureMode { get; set; } = "monitor";
    // Window identity (only capture_mode="window"): exe (primary, stable) + title (secondary, to
    // tell apart multiple windows of the same exe). Analogous to the replug-proof monitor identity.
    [JsonPropertyName("window_exe")]       public string WindowExe { get; set; } = "";
    [JsonPropertyName("window_title")]     public string WindowTitle { get; set; } = "";

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
    public static readonly string[] OverlayPositions = { "top_left", "top_right", "bottom_left", "bottom_right" };

    /// <summary>Frame limit actually in effect: only with VSync off (0 = unlimited / VSync paces).</summary>
    public int EffectiveTargetFps => Vsync || TargetFps <= 0 ? 0 : Math.Clamp(TargetFps, MinTargetFps, MaxTargetFps);

    /// <summary>Effective source mode: "window" or (default) "monitor".</summary>
    public string ResolveCaptureMode()
        => (CaptureMode ?? "").Trim().ToLowerInvariant() == "window" ? "window" : "monitor";

    // ── Key whitelist (against injection; LIVE takes effect immediately) ─────────
    public static readonly HashSet<string> LiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "tonemap_enabled", "operator", "source_peak_nits", "target_paperwhite",
        "exposure", "saturation", "contrast", "gamma", "content_offset_y", "vsync",
        "layout_mode", "crop_x", "crop_y", "crop_w", "crop_h", "show_cursor", "keep_awake",
        "target_fps", "fps_overlay", "fps_overlay_position", "audio_mirror",
    };
    public static readonly HashSet<string> StructKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "output_bit_depth", "source_display", "target_display",
        "source_key", "source_label", "target_key", "target_label",
        "output_mode", "windowed",
        "window_width", "window_height", "output_width", "output_height",
        "capture_mode", "window_exe", "window_title",
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

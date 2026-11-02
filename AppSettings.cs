using System.Text.Json;
using System.Text.Json.Serialization;

namespace RitschyMirror;

/// <summary>
/// App level (app_settings.json) — deliberately SEPARATE from mirror_config.json (render parameters),
/// so the Cockpit WinRM fallback (which only reads/writes render keys) stays untouched.
/// Here: HTTP control agent (bind/port) + autostart.
/// </summary>
public sealed class AppSettings
{
    // Agent bind: "localhost" (local only) or "+"/"0.0.0.0" (reachable from the network → Cockpit).
    [JsonPropertyName("agent_bind")]    public string AgentBind { get; set; } = "+";
    [JsonPropertyName("agent_port")]    public int AgentPort { get; set; } = 8788;
    [JsonPropertyName("agent_enabled")] public bool AgentEnabled { get; set; } = true;
    // Start mirroring automatically on app start (otherwise only tray idle).
    [JsonPropertyName("autostart_mirror")] public bool AutostartMirror { get; set; } = false;
    // UI language pack: "auto" (Windows UI language, English fallback) or a code such as "ko"/"en"/"de".
    [JsonPropertyName("language")]      public string Language { get; set; } = L.Auto;

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), ReadOpts) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOpts)); } catch { }
    }
}

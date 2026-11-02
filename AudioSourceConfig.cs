using System.Text.Json.Serialization;

namespace RitschyMirror;

/// <summary>
/// One audio source mixed into the audio mirror (<c>mirror_config.json → audio_sources</c>).
/// <see cref="Kind"/> "render" = loopback of what an output device plays (speakers, headset …),
/// "capture" = an input device (microphone …). <see cref="Id"/> is the Windows endpoint id, or
/// <see cref="DefaultId"/> to follow whatever device is currently the default for that kind.
/// </summary>
public sealed class AudioSourceConfig
{
    public const string Render = "render", Capture = "capture", DefaultId = "default";
    public const float MinGainDb = -30f, MaxGainDb = 12f;

    [JsonPropertyName("kind")]    public string Kind { get; set; } = Render;
    [JsonPropertyName("id")]      public string Id { get; set; } = DefaultId;
    // Last known device name — shown while the device is unplugged.
    [JsonPropertyName("name")]    public string Name { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("gain_db")] public float GainDb { get; set; } = 0f;

    [JsonIgnore] public bool IsRender => !string.Equals(Kind, Capture, StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsDefault => string.Equals(Id, DefaultId, StringComparison.OrdinalIgnoreCase);
    /// <summary>Identity of the entry (kind + configured id), e.g. "render:default".</summary>
    [JsonIgnore] public string Key => $"{(IsRender ? Render : Capture)}:{Id}";
    /// <summary>dB → linear factor. Gains are additive in dB (group + device), so the sum is clamped
    /// to twice the slider range.</summary>
    public static float DbToLinear(float db) =>
        (float)Math.Pow(10, Math.Clamp(db, 2 * MinGainDb, 2 * MaxGainDb) / 20.0);

    /// <summary>The pre-1.4.1 behaviour: only the default output device, unity gain.</summary>
    public static List<AudioSourceConfig> Defaults() => new() { new AudioSourceConfig() };

    public AudioSourceConfig Clone() => (AudioSourceConfig)MemberwiseClone();
}

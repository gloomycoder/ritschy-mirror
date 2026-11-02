namespace RitschyMirror;

/// <summary>
/// What an output device's Windows volume and mute still have to do to its loopback signal.
///
/// Measured (ZEUSLAP-HDMI: software volume + hardware mute; Realtek USB headphone: hardware volume
/// + mute): loopback is captured AFTER a volume the audio engine applies in software, but BEFORE a
/// volume or mute the device applies in hardware. So a software volume is already in the signal and
/// must not be applied again; a hardware volume is not. Mute always applies — zeroing a signal that
/// is already silent changes nothing, whichever side of the loopback tap the mute sits on.
/// </summary>
public static class LoopbackVolume
{
    /// <summary>ENDPOINT_HARDWARE_SUPPORT_VOLUME / _MUTE bits of IAudioEndpointVolume::QueryHardwareSupport.</summary>
    public const uint HardwareVolume = 1, HardwareMute = 2;

    public static bool HasHardwareVolume(uint hardwareSupport) => (hardwareSupport & HardwareVolume) != 0;

    /// <returns>Linear factor for the mirror: 0 when muted; for a hardware volume its level in dB
    /// relative to the maximum (100 % = 1, never a boost) and 0 at 0 %; 1 for a software volume.</returns>
    public static float Factor(uint hardwareSupport, bool mute, float scalar, float levelDb, float maxDb)
    {
        if (mute) return 0f;
        if (!HasHardwareVolume(hardwareSupport)) return 1f;   // already applied before the loopback tap
        if (scalar <= 0) return 0f;
        return (float)Math.Pow(10, Math.Min(0, levelDb - maxDb) / 20);
    }
}

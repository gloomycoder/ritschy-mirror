using NAudio.Wave;

namespace RitschyMirror;

/// <summary>
/// Folds a multichannel stream (e.g. a 5.1/7.1 loopback) into stereo for the audio-mirror mixer:
/// front L/R plus centre and surrounds at -3 dB; LFE is dropped. Channel order follows
/// WAVEFORMATEXTENSIBLE (FL FR FC LFE BL BR SL SR).
/// </summary>
internal sealed class DownmixToStereoSampleProvider : ISampleProvider
{
    private const float Minus3Db = 0.7071f;
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _scratch = Array.Empty<float>();

    public DownmixToStereoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        int frames = buffer.Length / 2;
        int needed = frames * _channels;
        if (_scratch.Length < needed) _scratch = new float[needed];
        int got = _source.Read(_scratch.AsSpan(0, needed)) / _channels;
        for (int f = 0; f < got; f++)
        {
            int i = f * _channels;
            float l = _scratch[i], r = _scratch[i + 1];
            if (_channels > 2) { float c = _scratch[i + 2] * Minus3Db; l += c; r += c; }
            if (_channels > 5) { l += _scratch[i + 4] * Minus3Db; r += _scratch[i + 5] * Minus3Db; }
            if (_channels > 7) { l += _scratch[i + 6] * Minus3Db; r += _scratch[i + 7] * Minus3Db; }
            buffer[f * 2] = l;
            buffer[f * 2 + 1] = r;
        }
        return got * 2;
    }
}

using NAudio.Wave;

namespace RitschyMirror;

/// <summary>
/// Folds a multichannel stream into stereo for the audio-mirror mixer, by speaker position.
///
/// The position of each interleaved channel comes from the WAVEFORMATEXTENSIBLE channel mask
/// (channels appear in ascending bit order). Without a usable mask the Windows default layout for
/// the channel count is assumed (quad = FL FR BL BR, 5.0 = FL FR FC BL BR, 5.1 = … LFE …, 7.1 =
/// … SL SR). Centre/rear/side/top channels go in at -3 dB (centre and back/top-centre to both
/// sides), LFE is dropped — so e.g. a quad source keeps both rear channels on their own side.
/// </summary>
internal sealed class DownmixToStereoSampleProvider : ISampleProvider
{
    private const float Minus3Db = 0.7071f;

    // SPEAKER_* bits (ksmedia.h), in channel order.
    private static readonly (int Bit, float L, float R)[] Speakers =
    {
        (0x1, 1f, 0f),               // FRONT_LEFT
        (0x2, 0f, 1f),               // FRONT_RIGHT
        (0x4, Minus3Db, Minus3Db),   // FRONT_CENTER
        (0x8, 0f, 0f),               // LOW_FREQUENCY (dropped)
        (0x10, Minus3Db, 0f),        // BACK_LEFT
        (0x20, 0f, Minus3Db),        // BACK_RIGHT
        (0x40, 1f, 0f),              // FRONT_LEFT_OF_CENTER
        (0x80, 0f, 1f),              // FRONT_RIGHT_OF_CENTER
        (0x100, 0.5f, 0.5f),         // BACK_CENTER
        (0x200, Minus3Db, 0f),       // SIDE_LEFT
        (0x400, 0f, Minus3Db),       // SIDE_RIGHT
        (0x800, 0.5f, 0.5f),         // TOP_CENTER
        (0x1000, Minus3Db, 0f),      // TOP_FRONT_LEFT
        (0x2000, 0.5f, 0.5f),        // TOP_FRONT_CENTER
        (0x4000, 0f, Minus3Db),      // TOP_FRONT_RIGHT
        (0x8000, Minus3Db, 0f),      // TOP_BACK_LEFT
        (0x10000, 0.5f, 0.5f),       // TOP_BACK_CENTER
        (0x20000, 0f, Minus3Db),     // TOP_BACK_RIGHT
    };

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float[] _toLeft, _toRight;   // per input channel
    private float[] _scratch = Array.Empty<float>();

    /// <param name="channelMask">WAVEFORMATEXTENSIBLE dwChannelMask of the source (0 = unknown).</param>
    public DownmixToStereoSampleProvider(ISampleProvider source, int channelMask)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        (_toLeft, _toRight) = BuildMatrix(_channels, channelMask);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Per-channel (L, R) weights from the mask; channels beyond the mask are dropped.</summary>
    internal static (float[] L, float[] R) BuildMatrix(int channels, int channelMask)
    {
        int mask = CountBits(channelMask) >= channels ? channelMask : DefaultMask(channels);
        var l = new float[channels];
        var r = new float[channels];
        int ch = 0;
        foreach (var sp in Speakers)
        {
            if (ch >= channels) break;
            if ((mask & sp.Bit) == 0) continue;
            l[ch] = sp.L;
            r[ch] = sp.R;
            ch++;
        }
        return (l, r);
    }

    /// <summary>KSAUDIO_SPEAKER_* layouts Windows uses for a plain channel count.</summary>
    internal static int DefaultMask(int channels) => channels switch
    {
        1 => 0x4,        // mono = centre
        2 => 0x3,        // stereo
        3 => 0x7,        // FL FR FC
        4 => 0x33,       // quad: FL FR BL BR
        5 => 0x37,       // 5.0: FL FR FC BL BR
        6 => 0x3F,       // 5.1: FL FR FC LFE BL BR
        7 => 0x13F,      // 6.1: 5.1 + BC
        8 => 0x63F,      // 7.1: 5.1 + SL SR
        _ => (int)((1L << Math.Min(channels, 31)) - 1),
    };

    private static int CountBits(int v)
    {
        int n = 0;
        for (uint u = (uint)v; u != 0; u &= u - 1) n++;
        return n;
    }

    public int Read(Span<float> buffer)
    {
        int frames = buffer.Length / 2;
        int needed = frames * _channels;
        if (_scratch.Length < needed) _scratch = new float[needed];
        int got = _source.Read(_scratch.AsSpan(0, needed)) / _channels;
        for (int f = 0; f < got; f++)
        {
            int i = f * _channels;
            float l = 0f, r = 0f;
            for (int c = 0; c < _channels; c++)
            {
                float v = _scratch[i + c];
                l += v * _toLeft[c];
                r += v * _toRight[c];
            }
            buffer[f * 2] = l;
            buffer[f * 2 + 1] = r;
        }
        return got * 2;
    }
}

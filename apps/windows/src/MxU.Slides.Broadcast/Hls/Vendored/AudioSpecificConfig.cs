// Ported from HaishinKit (BSD 3-Clause, Copyright (c) 2015, shogo4405); see LICENSE-HaishinKit.txt.
namespace MxU.Slides.Broadcast.Hls.Vendored;

/// <summary>
/// AAC's two-byte "AudioSpecificConfig": the object type (AAC-LC), sample-rate index and
/// channel count a decoder needs before it can play raw AAC. RTMP sends it once as the AAC
/// sequence header; MPEG-TS instead puts the same facts in a 7-byte ADTS header in front of
/// every frame (<see cref="WriteAdtsHeader"/>). Port of AudioSpecificConfig.swift and
/// AVAudioCompressedBuffer+ADTS.
/// </summary>
internal sealed record AudioSpecificConfig(byte ObjectType, byte FrequencyIndex, byte ChannelConfiguration)
{
    public const int AdtsHeaderSize = 7;

    public const byte AacLc = 2;

    private static readonly int[] SampleRates =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    public int SampleRate => SampleRates[FrequencyIndex];

    public int Channels => ChannelConfiguration == 7 ? 8 : ChannelConfiguration;

    /// <summary>AAC-LC at a standard rate with 1–6 or 8 channels; null for anything AAC can't describe.</summary>
    public static AudioSpecificConfig? AacLowComplexity(int sampleRate, int channels)
    {
        var index = Array.IndexOf(SampleRates, sampleRate);
        if (index < 0) return null;
        byte configuration = channels switch
        {
            >= 1 and <= 6 => (byte)channels,
            8 => 7,
            _ => 0,
        };
        return configuration == 0 ? null : new AudioSpecificConfig(AacLc, (byte)index, configuration);
    }

    public byte[] Bytes =>
    [
        (byte)(ObjectType << 3 | FrequencyIndex >> 1),
        (byte)((FrequencyIndex & 0x1) << 7 | (ChannelConfiguration & 0xF) << 3),
    ];

    /// <summary>Writes the ADTS header for a raw AAC frame of <paramref name="length"/> bytes into the first 7 bytes of <paramref name="data"/>.</summary>
    public void WriteAdtsHeader(Span<byte> data, int length)
    {
        var fullSize = AdtsHeaderSize + length;
        data[0] = 0xFF;
        data[1] = 0xF9;
        data[2] = (byte)((ObjectType - 1) << 6 | FrequencyIndex << 2 | ChannelConfiguration >> 2);
        data[3] = (byte)((ChannelConfiguration & 3) << 6 | fullSize >> 11);
        data[4] = (byte)((fullSize & 0x7FF) >> 3);
        data[5] = (byte)(((fullSize & 7) << 5) + 0x1F);
        data[6] = 0xFC;
    }

    /// <summary>A raw AAC frame with its ADTS header in front: what an MPEG-TS audio packet carries.</summary>
    public byte[] Adts(ReadOnlySpan<byte> rawFrame)
    {
        var data = new byte[AdtsHeaderSize + rawFrame.Length];
        WriteAdtsHeader(data, rawFrame.Length);
        rawFrame.CopyTo(data.AsSpan(AdtsHeaderSize));
        return data;
    }
}

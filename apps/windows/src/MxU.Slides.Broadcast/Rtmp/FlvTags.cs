using MxU.Slides.Broadcast.Hls.Vendored;
using MxU.Slides.Broadcast.Media;

namespace MxU.Slides.Broadcast.Rtmp;

/// <summary>
/// The bodies of RTMP audio and video messages, which are FLV tag bodies:
/// <list type="bullet">
/// <item>Video: one byte of frame type (1 keyframe, 2 other) and codec (7, AVC), one byte of
/// packet type (0 sequence header, 1 NAL units), three bytes of composition-time offset, then
/// either the AVCDecoderConfigurationRecord or the picture's AVCC NAL units.</item>
/// <item>Audio: 0xAF (AAC; FLV says AAC always declares 44 kHz, 16-bit, stereo here and lets
/// the AudioSpecificConfig say the truth), one byte of packet type (0 sequence header, 1 raw),
/// then the AudioSpecificConfig or one raw AAC frame.</item>
/// </list>
/// Port of HaishinKit's RTMPVideoMessage/RTMPAudioMessage payloads.
/// </summary>
internal static class FlvTags
{
    public const byte CodecAvc = 7;
    public const byte SoundFormatAac = 10;
    public const byte AacHeader = SoundFormatAac << 4 | 3 << 2 | 1 << 1 | 1;

    public static byte[] AvcSequenceHeader(AvcParameterSets parameterSets)
    {
        var record = parameterSets.DecoderConfigurationRecord();
        var body = new byte[5 + record.Length];
        body[0] = 1 << 4 | CodecAvc;
        body[1] = 0;
        record.CopyTo(body, 5);
        return body;
    }

    public static byte[] AvcPicture(EncodedVideoFrame frame, int compositionTimeMs = 0)
    {
        var body = new byte[5 + frame.Avcc.Length];
        body[0] = (byte)((frame.IsKeyframe ? 1 : 2) << 4 | CodecAvc);
        body[1] = 1;
        body[2] = (byte)(compositionTimeMs >> 16);
        body[3] = (byte)(compositionTimeMs >> 8);
        body[4] = (byte)compositionTimeMs;
        frame.Avcc.CopyTo(body, 5);
        return body;
    }

    public static byte[] AacSequenceHeader(AudioSpecificConfig config) => [AacHeader, 0, .. config.Bytes];

    public static byte[] AacRaw(ReadOnlySpan<byte> frame) => [AacHeader, 1, .. frame];

    /// <summary>
    /// The "@setDataFrame onMetaData" data message servers read the stream's shape from.
    /// Same keys as HaishinKit's makeMetadata, plus the usual stereo/sample-size/encoder ones.
    /// </summary>
    public static byte[] OnMetaData(int width, int height, int frameRate, int videoKbps, int audioKbps, int sampleRate, int channels)
    {
        var metadata = new Amf0EcmaArray
        {
            ["duration"] = 0,
            ["width"] = width,
            ["height"] = height,
            ["videocodecid"] = (double)CodecAvc,
            ["videodatarate"] = videoKbps,
            ["framerate"] = frameRate,
            ["audiocodecid"] = (double)SoundFormatAac,
            ["audiodatarate"] = audioKbps,
            ["audiosamplerate"] = sampleRate,
            ["audiosamplesize"] = 16,
            ["stereo"] = channels > 1,
            ["encoder"] = "MxU Slides",
        };
        return Amf0.Encode("@setDataFrame", "onMetaData", metadata);
    }
}

using MxU.Slides.Broadcast.Media;

namespace MxU.Slides.Broadcast.Recording;

/// <summary>
/// The recording codecs a preset can name (its "recordCodec" string uses these raw values).
/// Windows records MP4: H.264, or HEVC where the PC has an HEVC encoder. ProRes has no
/// Windows encoder and HDR has no Windows program feed, so <see cref="RecordingConfiguration.EffectiveCodec"/>
/// maps those to what this PC can make.
/// </summary>
public enum RecordingCodec
{
    H264,
    Hevc,
    Hevc10,
    ProRes422,
    ProRes4444,
}

public static class RecordingCodecs
{
    public static string Raw(this RecordingCodec codec) => codec switch
    {
        RecordingCodec.H264 => "h264",
        RecordingCodec.Hevc => "hevc",
        RecordingCodec.Hevc10 => "hevc10",
        RecordingCodec.ProRes422 => "proRes422",
        _ => "proRes4444",
    };

    /// <summary>Reads a preset's recordCodec; unknown or missing is H.264, as on the Mac.</summary>
    public static RecordingCodec Parse(string? raw) => raw switch
    {
        "hevc" => RecordingCodec.Hevc,
        "hevc10" => RecordingCodec.Hevc10,
        "proRes422" => RecordingCodec.ProRes422,
        "proRes4444" => RecordingCodec.ProRes4444,
        _ => RecordingCodec.H264,
    };

    /// <summary>Keeps transparency (ProRes 4444 on the Mac). Never true on Windows output.</summary>
    public static bool PreservesAlpha(this RecordingCodec codec) => codec == RecordingCodec.ProRes4444;

    /// <summary>Wants an HDR (HLG) source on the Mac. Windows records SDR.</summary>
    public static bool WantsHdrSource(this RecordingCodec codec) => codec == RecordingCodec.Hevc10;

    /// <summary>The Mac writes PCM audio next to ProRes; Windows always writes AAC in MP4.</summary>
    public static bool UsesPcmAudio(this RecordingCodec codec) => codec is RecordingCodec.ProRes422 or RecordingCodec.ProRes4444;
}

/// <summary>Why a recording ended.</summary>
public enum RecordingStopReason
{
    Requested,

    /// <summary>Free space fell below <see cref="RecordingConfiguration.MinimumFreeDiskBytes"/>.</summary>
    DiskFull,

    WriterFailed,
}

/// <summary>
/// What a recording should be: codec, picture size and rate, bitrate, audio, how much disk to
/// keep free, and how often the file is made safe (each fragment is complete on disk, so a
/// crash loses at most that much). Port of RecordingConfiguration.swift.
/// </summary>
public sealed record RecordingConfiguration(
    RecordingCodec Codec,
    int Width,
    int Height,
    int FrameRate = 60,
    int? AverageBitrate = null,
    bool IncludesAudio = false,
    int AudioSampleRate = 48_000,
    int AudioChannels = 2,
    long MinimumFreeDiskBytes = 2L << 30,
    double FragmentInterval = 2)
{
    public int EncodedWidth => Math.Max(2, Width & ~1);

    public int EncodedHeight => Math.Max(2, Height & ~1);

    /// <summary>The bitrate used: the configured one, or about 0.1 bit per pixel per frame (6 Mbps for 1080p30).</summary>
    public int EffectiveBitrate => AverageBitrate is { } bitrate and > 0
        ? bitrate
        : (int)Math.Clamp((long)EncodedWidth * EncodedHeight * Math.Max(1, FrameRate) / 10, 1_000_000, 80_000_000);

    /// <summary>Keyframes (and so MP4 fragments) come every <see cref="FragmentInterval"/> seconds.</summary>
    public int GopFrames => Math.Max(1, (int)Math.Round(Math.Max(0.5, FragmentInterval) * Math.Max(1, FrameRate)));

    /// <summary>What this PC records for the requested codec: HEVC only when an HEVC encoder exists, else H.264.</summary>
    public RecordingCodec EffectiveCodec(bool hevcAvailable) => Codec switch
    {
        RecordingCodec.Hevc or RecordingCodec.Hevc10 when hevcAvailable => RecordingCodec.Hevc,
        _ => RecordingCodec.H264,
    };

    /// <summary>The AAC bitrate. The Mac records 256 kbps; the Windows AAC encoder tops out at 192.</summary>
    public int AudioBitrateKbps => 192;

    /// <summary>A graphics-card H.264 encoder when there is one (the default), or always the software one.</summary>
    public EncoderPreference Encoder { get; init; } = EncoderPreference.Automatic;
}

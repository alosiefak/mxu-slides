using System.Runtime.InteropServices;

namespace MxU.Slides.VideoIO.Ndi;

// The C structs of the NDI SDK (Processing.NDI.*.h), laid out exactly as the 64-bit
// runtime expects them. A C "bool" is one byte, so it is a byte here (a C# bool field
// would be marshalled as a four-byte Windows BOOL). Sequential layout inserts the same
// padding a C compiler does; NdiLayoutTests checks every size.

/// <summary>NDIlib_find_create_t: settings for a source finder.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiFindCreate
{
    public byte ShowLocalSources;
    public nint Groups;
    public nint ExtraIps;
}

/// <summary>NDIlib_source_t: a source on the network (pointers into the finder's own memory).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiSource
{
    public nint NdiName;
    public nint UrlAddress;
}

/// <summary>NDIlib_recv_create_v3_t: settings for a receiver.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiRecvCreateV3
{
    public NdiSource SourceToConnectTo;
    public int ColorFormat;
    public int Bandwidth;
    public byte AllowVideoFields;
    public nint NdiRecvName;
}

/// <summary>NDIlib_send_create_t: settings for a sender.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiSendCreate
{
    public nint NdiName;
    public nint Groups;
    public byte ClockVideo;
    public byte ClockAudio;
}

/// <summary>NDIlib_video_frame_v2_t: one video frame going out or coming in.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiVideoFrameV2
{
    public int Xres;
    public int Yres;
    public int FourCC;
    public int FrameRateN;
    public int FrameRateD;
    public float PictureAspectRatio;
    public int FrameFormatType;
    public long Timecode;
    public nint Data;
    public int LineStrideInBytes;
    public nint Metadata;
    public long Timestamp;
}

/// <summary>NDIlib_audio_frame_v2_t: planar 32-bit float audio, one channel after another.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiAudioFrameV2
{
    public int SampleRate;
    public int NoChannels;
    public int NoSamples;
    public long Timecode;
    public nint Data;
    public int ChannelStrideInBytes;
    public nint Metadata;
    public long Timestamp;
}

/// <summary>Enum values and four-character codes from the NDI headers.</summary>
internal static class NdiConstants
{
    /// <summary>NDI_LIB_FOURCC(a, b, c, d): the four letters packed little-endian.</summary>
    public static int FourCC(char a, char b, char c, char d) => a | (b << 8) | (c << 16) | (d << 24);

    public static readonly int FourCCBgra = FourCC('B', 'G', 'R', 'A');
    public static readonly int FourCCBgrx = FourCC('B', 'G', 'R', 'X');

    public const int FrameTypeVideo = 1;
    public const int FrameFormatProgressive = 1;
    public const int RecvColorFormatBgrxBgra = 0;
    public const int RecvBandwidthHighest = 100;

    /// <summary>NDIlib_send_timecode_synthesize: let the runtime make up a timecode.</summary>
    public const long TimecodeSynthesize = long.MaxValue;
}

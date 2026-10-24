using System.Runtime.InteropServices;
using MxU.Slides.Core.Media;

namespace MxU.Slides.VideoIO.Ndi;

/// <summary>
/// An NDI source on the network: send it BGRA frames and float audio. It doesn't clock
/// itself (the program feed already paces frames), so a send returns as soon as NDI has
/// taken the data. Sends are serialized by a lock; <see cref="NdiOutput"/> calls them from
/// its own worker thread so the program feed never waits. Port of NDIKit/NDISender.swift,
/// plus audio, which the Mac's sender doesn't send.
/// </summary>
public sealed unsafe class NdiSender : IDisposable
{
    private readonly Lock gate = new();
    private readonly NdiLibrary library;
    private nint instance;
    private float[] planar = [];
    private long framesSent;
    private long audioSent;

    private NdiSender(NdiLibrary library, nint instance, string name)
    {
        this.library = library;
        this.instance = instance;
        Name = name;
    }

    /// <summary>The source name receivers see (NDI prefixes the PC's name: "STUDIO-PC (MxU Program)").</summary>
    public string Name { get; }

    /// <summary>Video frames sent so far.</summary>
    public long Sent => Interlocked.Read(ref framesSent);

    /// <summary>Audio buffers sent so far.</summary>
    public long AudioSent => Interlocked.Read(ref audioSent);

    /// <summary>A sender, or null when NDI isn't available or refused the name.</summary>
    public static NdiSender? Create(NdiLibrary? library, string name)
    {
        if (library is null) return null;
        NdiLibrary.RetainInstance();
        var utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var settings = new NdiSendCreate { NdiName = utf8, ClockVideo = 0, ClockAudio = 0 };
            var instance = ((delegate* unmanaged<NdiSendCreate*, nint>)library.Table.SendCreate)(&settings);
            if (instance != 0) return new NdiSender(library, instance, name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
        NdiLibrary.ReleaseInstance();
        return null;
    }

    /// <summary>Sends one program frame (BGRA, progressive) at the given frame rate.</summary>
    public void Send(VideoFrame frame, int frameRateNumerator = 30_000, int frameRateDenominator = 1_000) =>
        Send(frame.Pixels.Span, frame.Width, frame.Height, frame.Stride, frameRateNumerator, frameRateDenominator);

    public void Send(ReadOnlySpan<byte> bgra, int width, int height, int stride, int frameRateNumerator = 30_000, int frameRateDenominator = 1_000)
    {
        if (width <= 0 || height <= 0 || bgra.Length < stride * (height - 1) + width * 4) return;
        fixed (byte* pixels = bgra)
        {
            var frame = new NdiVideoFrameV2
            {
                Xres = width,
                Yres = height,
                FourCC = NdiConstants.FourCCBgra,
                FrameRateN = frameRateNumerator,
                FrameRateD = frameRateDenominator,
                FrameFormatType = NdiConstants.FrameFormatProgressive,
                Timecode = NdiConstants.TimecodeSynthesize,
                Data = (nint)pixels,
                LineStrideInBytes = stride,
            };
            lock (gate)
            {
                if (instance == 0) return;
                ((delegate* unmanaged<nint, NdiVideoFrameV2*, void>)library.Table.SendSendVideoV2)(instance, &frame);
                framesSent++;
            }
        }
    }

    /// <summary>
    /// Sends program audio. The app's buffers are interleaved (L R L R …); NDI wants each
    /// channel's samples together (L L … R R …), so they are rearranged first.
    /// </summary>
    public void SendAudio(AudioBuffer buffer)
    {
        var frames = buffer.FrameCount;
        if (frames == 0 || buffer.Channels <= 0) return;
        lock (gate)
        {
            if (instance == 0) return;
            if (planar.Length < frames * buffer.Channels) planar = new float[frames * buffer.Channels];
            Deinterleave(buffer.Samples.Span[..(frames * buffer.Channels)], buffer.Channels, planar);
            fixed (float* samples = planar)
            {
                var audio = new NdiAudioFrameV2
                {
                    SampleRate = buffer.SampleRate,
                    NoChannels = buffer.Channels,
                    NoSamples = frames,
                    Timecode = NdiConstants.TimecodeSynthesize,
                    Data = (nint)samples,
                    ChannelStrideInBytes = frames * sizeof(float),
                };
                ((delegate* unmanaged<nint, NdiAudioFrameV2*, void>)library.Table.SendSendAudioV2)(instance, &audio);
            }
            audioSent++;
        }
    }

    /// <summary>Interleaved (frame by frame) to planar (channel by channel): sample f of channel c moves from f×channels+c to c×frames+f.</summary>
    internal static void Deinterleave(ReadOnlySpan<float> interleaved, int channels, Span<float> planar)
    {
        var frames = interleaved.Length / channels;
        for (var channel = 0; channel < channels; channel++)
        {
            var target = planar.Slice(channel * frames, frames);
            for (var frame = 0; frame < frames; frame++) target[frame] = interleaved[frame * channels + channel];
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (instance == 0) return;
            ((delegate* unmanaged<nint, void>)library.Table.SendDestroy)(instance);
            instance = 0;
        }
        NdiLibrary.ReleaseInstance();
        GC.SuppressFinalize(this);
    }

    ~NdiSender() => Dispose();
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MxU.Slides.VideoIO.Ndi;

/// <summary>
/// One received frame, copied out of NDI: BGRA (or BGRX, alpha undefined, when the
/// source has no alpha), rows <see cref="BytesPerRow"/> apart.
/// </summary>
public sealed record NdiVideoFrame(int Width, int Height, int BytesPerRow, byte[] Data, bool HasAlpha);

/// <summary>Receives a borrowed frame: <paramref name="pixels"/> belong to NDI and are only valid during the call.</summary>
internal delegate void NdiFrameHandler(ReadOnlySpan<byte> pixels, int width, int height, int stride, bool hasAlpha);

/// <summary>
/// Connects to the first NDI source whose name contains a fragment ("STUDIO-PC (Program)"
/// matches "Program") and pulls its video as BGRA at full bandwidth. Audio and metadata
/// aren't asked for, so NDI drops them. One thread at a time; <see cref="NdiInputSource"/>
/// wraps it in a reconnecting loop. Port of NDIKit/NDIReceiver.swift.
/// </summary>
public sealed unsafe class NdiReceiver : IDisposable
{
    private readonly Lock gate = new();
    private readonly NdiLibrary library;
    private nint finder;
    private nint receiver;

    private NdiReceiver(NdiLibrary library, nint finder)
    {
        this.library = library;
        this.finder = finder;
    }

    /// <summary>A receiver (with its own finder), or null when NDI isn't available.</summary>
    public static NdiReceiver? Create(NdiLibrary? library)
    {
        if (library is null) return null;
        NdiLibrary.RetainInstance();
        var settings = new NdiFindCreate { ShowLocalSources = 1 };
        var finder = ((delegate* unmanaged<NdiFindCreate*, nint>)library.Table.FindCreateV2)(&settings);
        if (finder != 0) return new NdiReceiver(library, finder);
        NdiLibrary.ReleaseInstance();
        return null;
    }

    /// <summary>
    /// Looks for up to <paramref name="timeout"/> for a source whose name contains
    /// <paramref name="fragment"/> and connects to it. Returns the source's full name, or null.
    /// </summary>
    public string? Connect(string fragment, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        lock (gate)
        {
            while (finder != 0 && clock.Elapsed < timeout)
            {
                ((delegate* unmanaged<nint, uint, byte>)library.Table.FindWaitForSources)(finder, 500);
                uint count = 0;
                var sources = ((delegate* unmanaged<nint, uint*, NdiSource*>)library.Table.FindGetCurrentSources)(finder, &count);
                if (sources == null) continue;
                for (var i = 0; i < count; i++)
                {
                    var name = Marshal.PtrToStringUTF8(sources[i].NdiName) ?? "";
                    if (!name.Contains(fragment, StringComparison.Ordinal)) continue;
                    var settings = new NdiRecvCreateV3
                    {
                        SourceToConnectTo = sources[i],
                        ColorFormat = NdiConstants.RecvColorFormatBgrxBgra,
                        Bandwidth = NdiConstants.RecvBandwidthHighest,
                    };
                    DestroyReceiver();
                    receiver = ((delegate* unmanaged<NdiRecvCreateV3*, nint>)library.Table.RecvCreateV3)(&settings);
                    return receiver == 0 ? null : name;
                }
            }
            return null;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the next video frame and returns a copy of it.</summary>
    public NdiVideoFrame? CaptureVideoFrame(TimeSpan timeout)
    {
        NdiVideoFrame? copy = null;
        CaptureVideo(timeout, (pixels, width, height, stride, hasAlpha) =>
            copy = new NdiVideoFrame(width, height, stride, pixels[..(stride * height)].ToArray(), hasAlpha));
        return copy;
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the next BGRA/BGRX video frame and lends
    /// it to <paramref name="handler"/> without copying. True if one arrived.
    /// </summary>
    internal bool CaptureVideo(TimeSpan timeout, NdiFrameHandler handler)
    {
        var clock = Stopwatch.StartNew();
        lock (gate)
        {
            while (receiver != 0 && clock.Elapsed < timeout)
            {
                var wait = (uint)Math.Clamp((timeout - clock.Elapsed).TotalMilliseconds, 1, 1000);
                var video = new NdiVideoFrameV2();
                var kind = ((delegate* unmanaged<nint, NdiVideoFrameV2*, void*, void*, uint, int>)library.Table.RecvCaptureV3)(
                    receiver, &video, null, null, wait);
                if (kind != NdiConstants.FrameTypeVideo) continue;
                try
                {
                    var bgra = video.FourCC == NdiConstants.FourCCBgra;
                    if (video.Data == 0 || (!bgra && video.FourCC != NdiConstants.FourCCBgrx)) continue;
                    var stride = video.LineStrideInBytes;
                    handler(new ReadOnlySpan<byte>((void*)video.Data, stride * video.Yres), video.Xres, video.Yres, stride, bgra);
                    return true;
                }
                finally
                {
                    ((delegate* unmanaged<nint, NdiVideoFrameV2*, void>)library.Table.RecvFreeVideoV2)(receiver, &video);
                }
            }
            return false;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (finder == 0) return;
            DestroyReceiver();
            ((delegate* unmanaged<nint, void>)library.Table.FindDestroy)(finder);
            finder = 0;
        }
        NdiLibrary.ReleaseInstance();
        GC.SuppressFinalize(this);
    }

    ~NdiReceiver() => Dispose();

    private void DestroyReceiver()
    {
        if (receiver == 0) return;
        ((delegate* unmanaged<nint, void>)library.Table.RecvDestroy)(receiver);
        receiver = 0;
    }
}

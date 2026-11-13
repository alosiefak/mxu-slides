using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Media.Interop;

namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// Reads a video clip's pictures one after another, as fast as they're asked for (not in real
/// time), for a montage that's being drawn frame by frame (Phase 13). It's the picture half of
/// <see cref="MediaTranscoder"/>: Windows' own decoders (the Media Foundation source reader) turn
/// the file into BGRA pictures.
/// <para>
/// <see cref="FrameAt"/> gives the picture showing at a moment of the clip. Moments are asked
/// for in order, so it only ever reads forward; asking for an earlier one starts the clip again.
/// Past the clip's end it keeps giving the last picture. Use from one thread.
/// </para>
/// </summary>
public sealed class VideoClipReader : IDisposable
{
    private readonly string path;
    private IMFSourceReader? reader;
    private MediaTranscoder.VideoFormat format = null!;
    private byte[] current = [];
    private byte[] next = [];
    private double currentTime = double.NegativeInfinity;
    private double nextTime = double.NegativeInfinity;
    private long? origin;
    private bool ended;

    /// <exception cref="IOException">Windows can't open or decode the file; the message says why.</exception>
    public VideoClipReader(string path)
    {
        this.path = path;
        Open();
    }

    /// <summary>The picture's size (it can change part-way through a file; <see cref="FrameAt"/> reports each picture's own).</summary>
    public int Width => format.Width;

    public int Height => format.Height;

    /// <summary>The clip's length in seconds, as its header gives it (0 when unknown).</summary>
    public double Seconds { get; private set; }

    private void Open()
    {
        Close();
        try
        {
            MediaFoundation.EnsureStarted();
            reader = MediaTranscoder.OpenVideo(path, out format, out _, out var duration);
            Seconds = duration;
        }
        catch (Exception e) when (e is MediaTranscoder.TranscodeException or MediaFoundationException or COMException or InvalidCastException)
        {
            throw new IOException($"Windows can't read “{Path.GetFileName(path)}”: {e.Message}", e);
        }
        origin = null;
        ended = false;
        currentTime = nextTime = double.NegativeInfinity;
    }

    /// <summary>
    /// The picture showing <paramref name="seconds"/> into the clip: BGRA, top row first,
    /// <paramref name="width"/> × <paramref name="height"/>, 4 bytes a pixel. The array is the
    /// reader's own and is overwritten by the next call. Null when the clip has no picture to give.
    /// </summary>
    public byte[]? FrameAt(double seconds, out int width, out int height)
    {
        if (seconds < currentTime && !double.IsNegativeInfinity(currentTime)) Open();
        // Read on until the picture after the one wanted is in hand: the one before it is showing.
        while (!ended && nextTime <= seconds)
        {
            if (!double.IsNegativeInfinity(nextTime))
            {
                (current, next) = (next, current);
                currentTime = nextTime;
            }
            ReadNext();
        }
        if (ended && !double.IsNegativeInfinity(nextTime) && nextTime <= seconds)
        {
            (current, next) = (next, current);
            currentTime = nextTime;
            nextTime = double.NegativeInfinity;
        }
        (width, height) = (format.Width, format.Height);
        if (double.IsNegativeInfinity(currentTime))
        {
            // Asked for a moment before the first picture's own time: the first picture stands in.
            if (double.IsNegativeInfinity(nextTime)) return null;
            return next.Length == width * height * 4 ? next : null;
        }
        return current.Length == width * height * 4 ? current : null;
    }

    private void ReadNext()
    {
        var source = reader!;
        while (true)
        {
            var hr = source.ReadSample(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out var flags, out var time, out var sample);
            try
            {
                if (hr < 0 || (flags & MF.MF_SOURCE_READERF_ERROR) != 0)
                {
                    ended = true;
                    nextTime = double.NegativeInfinity;
                    return;
                }
                if ((flags & MF.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0) format = MediaTranscoder.ReadVideoFormat(source);
                if (sample is not null && MediaTranscoder.CopyPicture(sample, format, ref next))
                {
                    origin ??= time;
                    nextTime = Math.Max(0, time - origin.Value) / 10_000_000.0;
                    if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0) ended = true;
                    return;
                }
                if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    ended = true;
                    nextTime = double.NegativeInfinity;
                    return;
                }
            }
            finally
            {
                if (sample is not null) Marshal.ReleaseComObject(sample);
            }
        }
    }

    private void Close()
    {
        if (reader is not null) Marshal.FinalReleaseComObject(reader);
        reader = null;
    }

    public void Dispose() => Close();
}

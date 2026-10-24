using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Media.Interop;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>What Windows' own media reader (the Media Foundation source reader) finds in a file.</summary>
internal sealed record MediaProbe(
    double DurationSeconds,
    int Width,
    int Height,
    Guid VideoSubtype,
    int VideoSamples,
    double LastVideoTime,
    bool HasAudio,
    Guid AudioSubtype,
    int AudioSamples,
    double LastAudioTime)
{
    public static MediaProbe Read(string path)
    {
        MediaFoundation.EnsureStarted();
        MF.Check(MF.MFCreateSourceReaderFromURL(path, null, out var reader), "open " + Path.GetFileName(path));
        try
        {
            var duration = new PropVariant();
            double seconds = 0;
            if (reader.GetPresentationAttribute(MF.MF_SOURCE_READER_MEDIASOURCE, MF.MF_PD_DURATION, ref duration) >= 0)
            {
                seconds = duration.hVal / 10_000_000.0;
                MF.PropVariantClear(ref duration);
            }

            MF.Check(reader.GetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var videoType), "video type");
            videoType.GetUINT64(MF.MF_MT_FRAME_SIZE, out var size);
            videoType.GetGUID(MF.MF_MT_SUBTYPE, out var videoSubtype);
            Marshal.ReleaseComObject(videoType);

            var hasAudio = reader.GetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, out var audioType) >= 0;
            var audioSubtype = Guid.Empty;
            if (hasAudio)
            {
                audioType.GetGUID(MF.MF_MT_SUBTYPE, out audioSubtype);
                Marshal.ReleaseComObject(audioType);
            }

            var (videoSamples, lastVideo) = Count(reader, MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
            var (audioSamples, lastAudio) = hasAudio ? Count(reader, MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM) : (0, 0);
            return new MediaProbe(seconds, (int)(size >> 32), (int)(size & 0xFFFFFFFF), videoSubtype, videoSamples, lastVideo,
                hasAudio, audioSubtype, audioSamples, lastAudio);
        }
        finally
        {
            Marshal.FinalReleaseComObject(reader);
        }
    }

    /// <summary>Reads a stream's compressed samples to the end (rewinding first).</summary>
    private static (int Count, double Last) Count(IMFSourceReader reader, uint stream)
    {
        var start = new PropVariant { vt = PropVariant.VT_I8, hVal = 0 };
        reader.SetCurrentPosition(Guid.Empty, start);
        var count = 0;
        double last = 0;
        while (true)
        {
            var hr = reader.ReadSample(stream, 0, out _, out var flags, out var time, out var sample);
            if (hr < 0) break;
            if (sample is not null)
            {
                count++;
                last = Math.Max(last, time / 10_000_000.0);
                Marshal.ReleaseComObject(sample);
            }
            if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
        }
        return (count, last);
    }
}

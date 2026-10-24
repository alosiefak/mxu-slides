using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Media.Interop;

namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// What Windows can tell about a video file without playing it: whether it opens with a video
/// track, how long it is and its picture size. Used when a finished recording is filed into
/// the media library. Port of the probe half of the Mac's MediaImporter.probeVideo.
/// </summary>
public sealed record MediaFileProbe(bool IsPlayable, double? DurationSeconds, int? Width, int? Height, string Detail)
{
    public static MediaFileProbe Read(string path)
    {
        try
        {
            MediaFoundation.EnsureStarted();
            var hr = MF.MFCreateSourceReaderFromURL(path, null, out var reader);
            if (hr < 0) return new MediaFileProbe(false, null, null, null, $"Windows can't open this file (0x{hr:X8})");
            try
            {
                double? duration = null;
                var value = new PropVariant();
                if (reader.GetPresentationAttribute(MF.MF_SOURCE_READER_MEDIASOURCE, MF.MF_PD_DURATION, ref value) >= 0)
                {
                    duration = value.hVal / 10_000_000.0;
                    MF.PropVariantClear(ref value);
                }
                if (reader.GetNativeMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out var type) < 0)
                    return new MediaFileProbe(false, duration, null, null, "No video track");
                try
                {
                    type.GetUINT64(MF.MF_MT_FRAME_SIZE, out var size);
                    var width = (int)(size >> 32);
                    var height = (int)(size & 0xFFFFFFFF);
                    return new MediaFileProbe(true, duration, width > 0 ? width : null, height > 0 ? height : null, "");
                }
                finally
                {
                    MediaFoundation.Release(type);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(reader);
            }
        }
        catch (Exception e) when (e is MediaFoundationException or COMException or DllNotFoundException)
        {
            return new MediaFileProbe(false, null, null, null, e.Message);
        }
    }
}

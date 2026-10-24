using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Media.Interop;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// Looks inside a file being imported, with Windows' own decoders, so a file this PC can't
/// play is flagged when it comes in rather than failing during the service. Port of the Mac's
/// MediaImporter.probeVideo and MediaRelink.probe.
/// <list type="bullet">
/// <item>A picture is opened with Windows' imaging codecs (the ones Photos uses) for its size.
/// One Windows can't decode, such as an iPhone HEIC photo without the HEIF extension, is
/// skipped with the reason, as the Mac skips an unreadable image.</item>
/// <item>A video is opened with Media Foundation: its length, picture size, codec, whether it
/// has sound, and (the real test) whether its first picture and sound actually decode. See
/// <see cref="MediaPlayability"/> for how that becomes "ready" or "needs transcode".</item>
/// </list>
/// Safe to call from any thread; it never throws.
/// </summary>
public sealed class MediaImportProbe : IMediaProber
{
    public MediaProbe? Probe(string path) => LibraryStore.MediaKindOf(path) switch
    {
        MediaKind.Image => ProbeImage(path),
        MediaKind.Video => ProbeVideo(path),
        _ => null,
    };

    public static MediaProbe ProbeImage(string path)
    {
        try
        {
            using var stream = global::Windows.Storage.Streams.FileRandomAccessStream
                .OpenAsync(Path.GetFullPath(path), global::Windows.Storage.FileAccessMode.Read).AsTask().GetAwaiter().GetResult();
            var decoder = global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            return new MediaProbe(MediaKind.Image, MediaFileStatus.Ready, "", null, (int)decoder.PixelWidth, (int)decoder.PixelHeight) { HasVideo = true };
        }
        catch (Exception e)
        {
            var heic = Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif";
            var reason = heic
                ? "Windows can't read this HEIC photo. Install “HEIF Image Extensions” from the Microsoft Store, or export it as JPEG"
                : $"Windows can't read this picture ({e.Message.Trim().TrimEnd('.')})";
            return new MediaProbe(MediaKind.Image, MediaFileStatus.NeedsTranscode, reason, null, null, null) { SkipReason = reason };
        }
    }

    public static MediaProbe ProbeVideo(string path)
    {
        var (findings, duration, width, height) = Inspect(path);
        var (status, detail) = MediaPlayability.Judge(findings);
        return new MediaProbe(MediaKind.Video, status, detail,
            findings.OpenError is null && findings.HasVideo ? duration : null, width, height)
        {
            HasVideo = findings.HasVideo,
            HasAudio = findings.HasAudio,
        };
    }

    /// <summary>Opens the file and tries one picture and one piece of sound.</summary>
    internal static (MediaPlayability.Findings Found, double? Duration, int? Width, int? Height) Inspect(string path)
    {
        IMFSourceReader? reader = null;
        try
        {
            MediaFoundation.EnsureStarted();
            var attributes = MediaFoundation.CreateAttributes(2);
            int hr;
            try
            {
                // Lets the reader turn the decoder's pictures into RGB, as playback and transcoding do.
                attributes.SetUINT32(MF.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
                hr = MF.MFCreateSourceReaderFromURL(path, attributes, out reader);
            }
            finally
            {
                MediaFoundation.Release(attributes);
            }
            if (hr < 0) return (new MediaPlayability.Findings(Describe(hr), false, "", false, false, "", false), null, null, null);

            double? duration = null;
            var value = new PropVariant();
            if (reader.GetPresentationAttribute(MF.MF_SOURCE_READER_MEDIASOURCE, MF.MF_PD_DURATION, ref value) >= 0)
            {
                duration = value.hVal / 10_000_000.0;
                MF.PropVariantClear(ref value);
            }

            var (hasVideo, codec, width, height) = NativeVideo(reader);
            var (hasAudio, audioCodec) = NativeAudio(reader);
            var videoDecodes = hasVideo && Decodes(reader, MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, MF.MFMediaType_Video, MF.MFVideoFormat_RGB32);
            var audioDecodes = hasAudio && AudioDecodes(path);
            return (new MediaPlayability.Findings(null, hasVideo, codec, videoDecodes, hasAudio, audioCodec, audioDecodes), duration, width, height);
        }
        catch (Exception e) when (e is MediaFoundationException or COMException or DllNotFoundException or InvalidCastException)
        {
            return (new MediaPlayability.Findings(e.Message, false, "", false, false, "", false), null, null, null);
        }
        finally
        {
            if (reader is not null) Marshal.FinalReleaseComObject(reader);
        }
    }

    private static (bool Has, string Codec, int? Width, int? Height) NativeVideo(IMFSourceReader reader)
    {
        if (reader.GetNativeMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out var type) < 0) return (false, "", null, null);
        try
        {
            type.GetGUID(MF.MF_MT_SUBTYPE, out var subtype);
            int? width = null, height = null;
            if (type.GetUINT64(MF.MF_MT_FRAME_SIZE, out var size) >= 0 && size >> 32 > 0 && (size & 0xFFFFFFFF) > 0)
            {
                width = (int)(size >> 32);
                height = (int)(size & 0xFFFFFFFF);
            }
            return (true, CodecName(subtype), width, height);
        }
        finally
        {
            MediaFoundation.Release(type);
        }
    }

    private static (bool Has, string Codec) NativeAudio(IMFSourceReader reader)
    {
        if (reader.GetNativeMediaType(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, out var type) < 0) return (false, "");
        try
        {
            type.GetGUID(MF.MF_MT_SUBTYPE, out var subtype);
            return (true, CodecName(subtype));
        }
        finally
        {
            MediaFoundation.Release(type);
        }
    }

    /// <summary>Asks for decoded output on one stream and reads its first sample; true when that works.</summary>
    private static bool Decodes(IMFSourceReader reader, uint stream, Guid major, Guid decoded)
    {
        reader.SetStreamSelection(MF.MF_SOURCE_READER_ALL_STREAMS, 0);
        if (reader.SetStreamSelection(stream, 1) < 0) return false;
        var type = MediaFoundation.CreateMediaType();
        try
        {
            type.SetGUID(MF.MF_MT_MAJOR_TYPE, major);
            type.SetGUID(MF.MF_MT_SUBTYPE, decoded);
            if (reader.SetCurrentMediaType(stream, 0, type) < 0) return false;
        }
        finally
        {
            MediaFoundation.Release(type);
        }
        // A few reads: some decoders hand back an empty "stream tick" or a format change first.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var hr = reader.ReadSample(stream, 0, out _, out var flags, out _, out var sample);
            if (sample is not null) Marshal.ReleaseComObject(sample);
            if (hr < 0 || (flags & MF.MF_SOURCE_READERF_ERROR) != 0) return false;
            if (sample is not null) return true;
            if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0) return false;
        }
        return false;
    }

    /// <summary>The sound is tried on a reader of its own, as playback opens it (a video reader's settings don't carry over).</summary>
    private static bool AudioDecodes(string path)
    {
        if (MF.MFCreateSourceReaderFromURL(path, null, out var reader) < 0) return false;
        try
        {
            return Decodes(reader, MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, MF.MFMediaType_Audio, MF.MFAudioFormat_Float);
        }
        finally
        {
            Marshal.FinalReleaseComObject(reader);
        }
    }

    /// <summary>
    /// The codec's four-letter code ("H264", "apch"), from a Media Foundation subtype: those are
    /// the code in the first four bytes of a fixed GUID. A few uncompressed formats use numbers
    /// instead, and MPEG-2 has a GUID of its own.
    /// </summary>
    internal static string CodecName(Guid subtype)
    {
        var bytes = subtype.ToByteArray();
        if (subtype == new Guid("e06d8026-db46-11cf-b4d1-00805f6cbbea")) return "MPEG-2";
        if (bytes.AsSpan(4).SequenceEqual(FourCcTail))
        {
            var code = BitConverter.ToUInt32(bytes, 0);
            switch (code)
            {
                case 20: return "RGB24";
                case 21: return "ARGB32";
                case 22: return "RGB32";
                case 23: return "RGB565";
                case 24: return "RGB555";
                case 1: return "PCM";
                case 3: return "Float";
                case 0x50: return "MP3";
                case 0x161 or 0x162 or 0x163: return "WMA";
                case 0x2000: return "AC-3";
                case 0x1610: return "AAC";
                case 0x1600: return "AAC (ADTS)";
                case 0x6C61: return "ALAC";
            }
            var text = System.Text.Encoding.ASCII.GetString(bytes, 0, 4);
            if (text.All(c => c is >= ' ' and <= '~')) return text.TrimEnd();
        }
        return subtype.ToString();
    }

    private static readonly byte[] FourCcTail = new Guid("00000000-0000-0010-8000-00AA00389B71").ToByteArray()[4..];

    private static string Describe(int hr) => Marshal.GetExceptionForHR(hr)?.Message is { Length: > 0 } message
        ? $"{message.TrimEnd('.', ' ')} (0x{hr:X8})"
        : $"0x{hr:X8}";
}

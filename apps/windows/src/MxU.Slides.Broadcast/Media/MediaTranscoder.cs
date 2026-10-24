using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Media.Interop;
using MxU.Slides.Broadcast.Recording;

namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// Makes an H.264 MP4 copy of a video Windows can decode but which isn't in a codec the app
/// plays well (see <see cref="MediaPlayability"/>): Motion JPEG, MPEG-2, uncompressed AVI,
/// VP9 and so on. The Mac's TranscodeQueue makes HEVC with AVFoundation; Windows' own
/// encoders make H.264 cleanly on every PC, so that's the target here (with AAC sound), written
/// by the same MP4 writer the recorder uses.
/// <para>
/// It reads as fast as the PC can decode, not in real time: one Media Foundation reader for
/// the pictures (as BGRA, turned into the encoder's NV12) and one for the sound (as 48 kHz
/// stereo), taking whichever is behind next so the two stay interleaved in the file. Runs on
/// the caller's thread, which should be a background one; it can take minutes for a long video.
/// </para>
/// </summary>
public static class MediaTranscoder
{
    /// <summary>The biggest picture made; larger videos are scaled down to fit (4K UHD).</summary>
    private const int MaxWidth = 3840;
    private const int MaxHeight = 2160;
    private const int MinSide = 96;
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    public sealed class TranscodeException(string message) : Exception(message);

    /// <summary>Transcodes <paramref name="source"/> to an H.264 MP4 at <paramref name="destination"/>. Progress is 0–1.</summary>
    /// <exception cref="TranscodeException">The file can't be decoded or encoded; the message says why.</exception>
    public static void ToMp4(string source, string destination, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        IMFSourceReader? video = null;
        IMFSourceReader? audio = null;
        Mp4SinkWriter? writer = null;
        var finished = false;
        try
        {
            MediaFoundation.EnsureStarted();
            video = OpenVideo(source, out var format, out var frameRate, out var duration);
            audio = OpenAudio(source, out var audioRate, out var audioChannels);
            var (width, height) = Fit(format.Width, format.Height);
            var bitrate = (int)Math.Clamp(width * (long)height * frameRate * 0.12, 2_000_000, 40_000_000);
            var settings = new Mp4Settings(width, height, frameRate, bitrate, GopFrames: frameRate * 2,
                AudioSampleRate: audio is null ? null : SampleRate, AudioChannels: Channels, AudioBitrateKbps: 192);
            writer = Mp4SinkWriter.CreateEncoding(destination, settings, hevc: false, allowHardware: true);

            var converter = new Nv12Converter(width, height);
            var nv12 = new byte[converter.FrameSize];
            var pcm = audio is null ? null : new PcmConverter(audioRate, audioChannels, SampleRate, Channels);
            var pixels = Array.Empty<byte>();
            var sound = Array.Empty<float>();
            // Times are kept from the file, less where its first picture is (most files start at 0).
            long? origin = null;
            long? audioStart = null;
            long videoAt = 0, audioAt = audio is null ? long.MaxValue : 0;
            bool videoDone = false, audioDone = audio is null;
            var frameTicks = 10_000_000L / frameRate;
            long writtenAudioFrames = 0;
            while (!videoDone || !audioDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Whichever stream is behind goes next, so pictures and sound arrive together.
                var takeVideo = !videoDone && (audioDone || videoAt <= audioAt);
                var reader = takeVideo ? video : audio!;
                var stream = takeVideo ? MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM : MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM;
                var hr = reader.ReadSample(stream, 0, out _, out var flags, out var time, out var sample);
                try
                {
                    if (hr < 0 || (flags & MF.MF_SOURCE_READERF_ERROR) != 0)
                    {
                        if (takeVideo) throw new TranscodeException($"decoding stopped: {Describe(hr)}");
                        audioDone = true; // Sound that breaks off leaves the rest of the video silent, as replay does.
                        continue;
                    }
                    if (takeVideo && (flags & MF.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0) format = ReadVideoFormat(video);
                    if (sample is not null && takeVideo)
                    {
                        origin ??= time;
                        videoAt = Math.Max(0, time - origin.Value);
                        if (CopyPicture(sample, format, ref pixels))
                        {
                            converter.Convert(pixels, format.Width, format.Height, format.Width * 4, nv12);
                            var length = sample.GetSampleDuration(out var ticks) >= 0 && ticks > 0 ? ticks : frameTicks;
                            writer.WriteVideo(nv12, videoAt, length);
                        }
                        if (duration > 0) progress?.Report(Math.Clamp(videoAt / 10_000_000.0 / duration, 0, 1));
                    }
                    else if (sample is not null)
                    {
                        audioAt = Math.Max(0, time - (origin ?? 0));
                        var count = CopySound(sample, ref sound);
                        var shorts = pcm!.Convert(sound.AsSpan(0, count));
                        if (shorts.Length > 0)
                        {
                            // Stamped by how much sound has been written since the first piece, so resampling
                            // never leaves a gap or an overlap.
                            audioStart ??= audioAt;
                            var frames = shorts.Length / Channels;
                            var start = audioStart.Value + writtenAudioFrames * 10_000_000L / SampleRate;
                            writer.WriteAudio(shorts, start, frames * 10_000_000L / SampleRate);
                            writtenAudioFrames += frames;
                        }
                    }
                    if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                    {
                        if (takeVideo) videoDone = true;
                        else audioDone = true;
                    }
                }
                finally
                {
                    if (sample is not null) Marshal.ReleaseComObject(sample);
                }
            }
            if (origin is null) throw new TranscodeException("the video has no pictures");
            writer.Finish();
            writer = null;
            progress?.Report(1);
            finished = true;
        }
        catch (TranscodeException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is MediaFoundationException or COMException or InvalidCastException or ArgumentException)
        {
            throw new TranscodeException(e.Message);
        }
        finally
        {
            writer?.Dispose();
            if (video is not null) Marshal.FinalReleaseComObject(video);
            if (audio is not null) Marshal.FinalReleaseComObject(audio);
            if (!finished)
            {
                try { File.Delete(destination); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// The output size: the video's own, made even (H.264 needs that), shrunk to fit 4K if it's
    /// bigger, and grown if it's tiny: Windows' H.264 decoder won't play a picture under about
    /// 64 pixels a side, so a copy that small would be no better than the original.
    /// </summary>
    internal static (int Width, int Height) Fit(int width, int height)
    {
        var scale = Math.Min((double)MaxWidth / width, (double)MaxHeight / height);
        scale = Math.Min(Math.Max(1.0, (double)MinSide / Math.Min(width, height)), scale);
        return (Math.Max(2, (int)Math.Round(width * scale) & ~1), Math.Max(2, (int)Math.Round(height * scale) & ~1));
    }

    private sealed record VideoFormat(int Width, int Height, int Pitch, int CropX, int CropY, int FullHeight);

    private static IMFSourceReader OpenVideo(string path, out VideoFormat format, out int frameRate, out double duration)
    {
        var attributes = MediaFoundation.CreateAttributes(2);
        IMFSourceReader reader;
        try
        {
            attributes.SetUINT32(MF.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
            var hr = MF.MFCreateSourceReaderFromURL(path, attributes, out reader);
            if (hr < 0) throw new TranscodeException($"Windows can't open the file: {Describe(hr)}");
        }
        finally
        {
            MediaFoundation.Release(attributes);
        }
        try
        {
            duration = 0;
            var value = new PropVariant();
            if (reader.GetPresentationAttribute(MF.MF_SOURCE_READER_MEDIASOURCE, MF.MF_PD_DURATION, ref value) >= 0)
            {
                duration = value.hVal / 10_000_000.0;
                MF.PropVariantClear(ref value);
            }
            frameRate = 30;
            if (reader.GetNativeMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out var native) < 0)
                throw new TranscodeException("it has no video track");
            try
            {
                if (native.GetUINT64(MF.MF_MT_FRAME_RATE, out var rate) >= 0 && (rate & 0xFFFFFFFF) > 0)
                    frameRate = (int)Math.Clamp(Math.Round((double)(rate >> 32) / (rate & 0xFFFFFFFF)), 1, 120);
                native.GetGUID(MF.MF_MT_SUBTYPE, out var subtype);
                reader.SetStreamSelection(MF.MF_SOURCE_READER_ALL_STREAMS, 0);
                reader.SetStreamSelection(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 1);
                var type = MediaFoundation.CreateMediaType();
                try
                {
                    type.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
                    type.SetGUID(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_RGB32);
                    if (reader.SetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, type) < 0)
                        throw new TranscodeException(MediaPlayability.NoDecoder(MediaImportProbe.CodecName(subtype)));
                }
                finally
                {
                    MediaFoundation.Release(type);
                }
            }
            finally
            {
                MediaFoundation.Release(native);
            }
            format = ReadVideoFormat(reader);
            return reader;
        }
        catch
        {
            Marshal.FinalReleaseComObject(reader);
            throw;
        }
    }

    private static VideoFormat ReadVideoFormat(IMFSourceReader reader)
    {
        MF.Check(reader.GetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var type), "video format");
        try
        {
            type.GetUINT64(MF.MF_MT_FRAME_SIZE, out var size);
            var width = (int)(size >> 32);
            var height = (int)(size & 0xFFFFFFFF);
            var pitch = type.GetUINT32(MF.MF_MT_DEFAULT_STRIDE, out var stride) >= 0 ? unchecked((int)stride) : width * 4;
            // A 1080p video is often decoded as 1088 rows with the real picture marked inside.
            var (cropX, cropY, cropWidth, cropHeight) = (0, 0, width, height);
            if (MediaFoundation.GetBlob(type, MF.MF_MT_MINIMUM_DISPLAY_APERTURE) is { Length: >= 16 } area)
            {
                var x = BitConverter.ToInt16(area, 2);
                var y = BitConverter.ToInt16(area, 6);
                var w = BitConverter.ToInt32(area, 8);
                var h = BitConverter.ToInt32(area, 12);
                if (w > 0 && h > 0 && x >= 0 && y >= 0 && x + w <= width && y + h <= height) (cropX, cropY, cropWidth, cropHeight) = (x, y, w, h);
            }
            if (cropWidth <= 0 || cropHeight <= 0) throw new TranscodeException("the video's picture size is unknown");
            return new VideoFormat(cropWidth, cropHeight, pitch, cropX, cropY, height);
        }
        finally
        {
            MediaFoundation.Release(type);
        }
    }

    /// <summary>A second reader for the sound, as float (48 kHz stereo if Windows will convert it, else the file's own rate). Null without usable sound.</summary>
    private static IMFSourceReader? OpenAudio(string path, out int rate, out int channels)
    {
        rate = 0;
        channels = 0;
        if (MF.MFCreateSourceReaderFromURL(path, null, out var reader) < 0) return null;
        try
        {
            reader.SetStreamSelection(MF.MF_SOURCE_READER_ALL_STREAMS, 0);
            if (reader.SetStreamSelection(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 1) < 0
                || (!TrySetAudioType(reader, exact: true) && !TrySetAudioType(reader, exact: false)))
            {
                Marshal.FinalReleaseComObject(reader);
                return null;
            }
            MF.Check(reader.GetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, out var type), "audio format");
            try
            {
                type.GetUINT32(MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, out var samplesPerSecond);
                type.GetUINT32(MF.MF_MT_AUDIO_NUM_CHANNELS, out var channelCount);
                rate = (int)samplesPerSecond;
                channels = (int)channelCount;
            }
            finally
            {
                MediaFoundation.Release(type);
            }
            if (rate > 0 && channels > 0) return reader;
            Marshal.FinalReleaseComObject(reader);
            return null;
        }
        catch (Exception e) when (e is MediaFoundationException or COMException)
        {
            Marshal.FinalReleaseComObject(reader);
            return null;
        }
    }

    private static bool TrySetAudioType(IMFSourceReader reader, bool exact)
    {
        var type = MediaFoundation.CreateMediaType();
        try
        {
            type.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio);
            type.SetGUID(MF.MF_MT_SUBTYPE, MF.MFAudioFormat_Float);
            if (exact)
            {
                type.SetUINT32(MF.MF_MT_AUDIO_NUM_CHANNELS, Channels);
                type.SetUINT32(MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
                type.SetUINT32(MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 32);
                type.SetUINT32(MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, Channels * 4);
                type.SetUINT32(MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, SampleRate * Channels * 4);
            }
            return reader.SetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, type) >= 0;
        }
        finally
        {
            MediaFoundation.Release(type);
        }
    }

    /// <summary>Copies a decoded picture out, top row first; false if its buffer can't be read.</summary>
    private static unsafe bool CopyPicture(IMFSample sample, VideoFormat format, ref byte[] pixels)
    {
        var rowBytes = format.Width * 4;
        var size = rowBytes * format.Height;
        if (pixels.Length != size) pixels = new byte[size];
        if (sample.GetBufferByIndex(0, out var buffer) < 0) return false;
        try
        {
            nint scanline0 = 0;
            var pitch = 0;
            var twoD = buffer as IMF2DBuffer;
            var locked2D = twoD is not null && twoD.Lock2D(out scanline0, out pitch) >= 0;
            if (!locked2D)
            {
                if (buffer.Lock(out scanline0, out _, out var length) < 0) return false;
                pitch = format.Pitch;
                // A negative stride means the rows are stored bottom-up.
                if (pitch < 0) scanline0 += (nint)(-(long)pitch * (format.FullHeight - 1));
                if (Math.Abs((long)pitch) * format.FullHeight > length)
                {
                    buffer.Unlock();
                    return false;
                }
            }
            try
            {
                fixed (byte* destination = pixels)
                {
                    for (var row = 0; row < format.Height; row++)
                    {
                        var from = (byte*)scanline0 + (long)(row + format.CropY) * pitch + format.CropX * 4;
                        Buffer.MemoryCopy(from, destination + row * rowBytes, rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                if (locked2D) twoD!.Unlock2D();
                else buffer.Unlock();
            }
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    private static unsafe int CopySound(IMFSample sample, ref float[] samples)
    {
        MF.Check(sample.ConvertToContiguousBuffer(out var buffer), "IMFSample.ConvertToContiguousBuffer");
        try
        {
            MF.Check(buffer.Lock(out var pointer, out _, out var length), "IMFMediaBuffer.Lock");
            try
            {
                var count = (int)(length / sizeof(float));
                if (samples.Length < count) samples = new float[count];
                new ReadOnlySpan<float>((void*)pointer, count).CopyTo(samples);
                return count;
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    private static string Describe(int hr) => Marshal.GetExceptionForHR(hr)?.Message is { Length: > 0 } message
        ? $"{message.TrimEnd('.', ' ')} (0x{hr:X8})"
        : $"0x{hr:X8}";
}

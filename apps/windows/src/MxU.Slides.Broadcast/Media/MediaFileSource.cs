using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Media.Interop;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// Plays a video file out as pictures and sound in real time, so a stream can carry a
/// pre-recorded service ("replay") instead of the live program. Windows' own decoders (the
/// Media Foundation source reader) turn the file into BGRA pictures and float sound; one
/// thread hands each picture over when it's due, another does the same for the sound, a
/// little early (50 ms) so the encoder never runs dry. A picture more than a quarter of a
/// second late is skipped and counted, so a slow PC catches up instead of drifting; on a PC
/// that decodes slower than the file plays, <see cref="ReplayPacer"/> still shows a picture
/// every half second and skips ahead rather than freezing.
/// <para>
/// When the file plays to its end (or its out point) <c>onEnded(null)</c> is called once;
/// when it can't be read or decoding fails, <c>onEnded(reason)</c>. <see cref="Stop"/> never
/// reports an end. Callbacks come on the source's own threads (never the thread pool). Port
/// of MediaFileSource.swift, which uses AVAssetReader.
/// </para>
/// </summary>
public sealed class MediaFileSource
{
    private const double AudioLeadSeconds = 0.05;
    private const int WantedSampleRate = 48_000;
    private const int WantedChannels = 2;

    private readonly Action<VideoFrame> frameSink;
    private readonly Action<AudioBuffer> audioSink;
    private readonly Action<string?> onEnded;
    private readonly double? inPoint;
    private readonly double? outPoint;
    private readonly ManualResetEventSlim stopSignal = new(false);
    private volatile bool running;
    private int started;
    private int ended;
    private int delivered;
    private int dropped;

    public MediaFileSource(string path, double? inPoint, double? outPoint,
        Action<VideoFrame> frameSink, Action<AudioBuffer> audioSink, Action<string?> onEnded)
    {
        Path = path;
        this.inPoint = inPoint is > 0 ? inPoint : null;
        this.outPoint = outPoint;
        this.frameSink = frameSink;
        this.audioSink = audioSink;
        this.onEnded = onEnded;
    }

    public string Path { get; }

    public int FramesDelivered => Volatile.Read(ref delivered);

    public int FramesDropped => Volatile.Read(ref dropped);

    /// <summary>Starts playing (once; later calls do nothing). Returns at once: opening the file happens on the source's thread.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        running = true;
        new Thread(Run) { IsBackground = true, Name = "MxU replay video", Priority = ThreadPriority.AboveNormal }.Start();
    }

    /// <summary>Stops playing. Never reports an end; safe to call more than once.</summary>
    public void Stop()
    {
        Volatile.Write(ref ended, 1);
        running = false;
        stopSignal.Set();
    }

    private void End(string? reason)
    {
        if (Interlocked.Exchange(ref ended, 1) == 1) return;
        running = false;
        stopSignal.Set();
        try { onEnded(reason); }
        catch (Exception e) { BroadcastLog.Error("replay", $"onEnded handler threw: {e.Message}"); }
    }

    private double Start0 => inPoint ?? 0;

    // MARK: Video (and the start of everything)

    /// <summary>The picture's size (after cropping), the row stride the format declares, the crop's corner and the decoded height.</summary>
    private sealed record VideoFormat(int Width, int Height, int Pitch, int CropX, int CropY, int FullHeight);

    private void Run()
    {
        IMFSourceReader? reader = null;
        var opening = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            MediaFoundation.EnsureStarted();
            string? problem;
            (reader, problem) = OpenVideo();
            if (reader is null)
            {
                End(problem);
                return;
            }
            var format = ReadVideoFormat(reader);
            Seek(reader);
            var audioReader = OpenAudio(out var audioRate, out var audioChannels);
            if (audioReader is not null) Seek(audioReader);
            // Decoders start on their first read, which can take a while on a busy PC. The first
            // picture and sound are decoded before the clock starts, so nothing is late at the start
            // and sound and pictures share one zero.
            var firstPicture = Read(reader, MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
            var firstSound = audioReader is null ? null : Read(audioReader, MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM);
            if (!running)
            {
                firstPicture.Release();
                firstSound?.Release();
                if (audioReader is not null) Marshal.FinalReleaseComObject(audioReader);
                return;
            }
            BroadcastLog.Info("replay", $"{System.IO.Path.GetFileName(Path)}: {format.Width}x{format.Height}, first picture at {firstPicture.Time / 10_000_000.0:0.000} s decoded in {opening.ElapsedMilliseconds} ms"
                + (audioReader is null ? ", no sound" : $", sound {audioRate} Hz"));
            var anchor = ShowState.HostClock();
            if (audioReader is not null)
            {
                new Thread(() => PumpAudio(audioReader, firstSound, anchor, audioRate, audioChannels))
                { IsBackground = true, Name = "MxU replay audio", Priority = ThreadPriority.AboveNormal }.Start();
            }
            PumpVideo(reader, firstPicture, format, anchor);
        }
        catch (Exception e)
        {
            End($"decode stopped: {e.Message}");
        }
        finally
        {
            if (reader is not null) Marshal.FinalReleaseComObject(reader);
        }
    }

    private (IMFSourceReader? Reader, string? Problem) OpenVideo()
    {
        var attributes = MediaFoundation.CreateAttributes(2);
        IMFSourceReader reader;
        try
        {
            // Lets the reader turn the decoder's YUV pictures into the BGRA the encoders take.
            attributes.SetUINT32(MF.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
            var hr = MF.MFCreateSourceReaderFromURL(Path, attributes, out reader);
            if (hr < 0) return (null, $"unreadable file: {Describe(hr)}");
        }
        finally
        {
            MediaFoundation.Release(attributes);
        }
        reader.SetStreamSelection(MF.MF_SOURCE_READER_ALL_STREAMS, 0);
        if (reader.SetStreamSelection(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 1) < 0)
        {
            Marshal.FinalReleaseComObject(reader);
            return (null, "no video track");
        }
        var type = MediaFoundation.CreateMediaType();
        try
        {
            type.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            type.SetGUID(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_RGB32);
            if (reader.SetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, type) < 0)
            {
                Marshal.FinalReleaseComObject(reader);
                return (null, "video track not decodable");
            }
        }
        finally
        {
            MediaFoundation.Release(type);
        }
        return (reader, null);
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
            // A 1080p H.264 file is often decoded as 1088 rows with the real picture marked inside.
            var (cropX, cropY, cropWidth, cropHeight) = (0, 0, width, height);
            if (MediaFoundation.GetBlob(type, MF.MF_MT_MINIMUM_DISPLAY_APERTURE) is { Length: >= 16 } area)
            {
                var x = BitConverter.ToInt16(area, 2);
                var y = BitConverter.ToInt16(area, 6);
                var w = BitConverter.ToInt32(area, 8);
                var h = BitConverter.ToInt32(area, 12);
                if (w > 0 && h > 0 && x >= 0 && y >= 0 && x + w <= width && y + h <= height) (cropX, cropY, cropWidth, cropHeight) = (x, y, w, h);
            }
            return new VideoFormat(cropWidth, cropHeight, pitch, cropX, cropY, height);
        }
        finally
        {
            MediaFoundation.Release(type);
        }
    }

    private void Seek(IMFSourceReader reader)
    {
        if (inPoint is { } start) SeekTo(reader, start);
    }

    /// <summary>Moves the reader to <paramref name="seconds"/> into the file; its next picture is the key frame at or before it.</summary>
    private static void SeekTo(IMFSourceReader reader, double seconds)
    {
        var position = new PropVariant { vt = PropVariant.VT_I8, hVal = (long)Math.Round(seconds * 10_000_000) };
        reader.SetCurrentPosition(Guid.Empty, position);
    }

    /// <summary>One read from a source reader: its result, flags, time (100 ns units) and sample (null at a gap or the end).</summary>
    private sealed record SampleRead(int Hr, uint Flags, long Time, IMFSample? Sample)
    {
        public void Release()
        {
            if (Sample is not null) Marshal.ReleaseComObject(Sample);
        }
    }

    private static SampleRead Read(IMFSourceReader reader, uint stream)
    {
        var hr = reader.ReadSample(stream, 0, out _, out var flags, out var time, out var sample);
        return new SampleRead(hr, flags, time, sample);
    }

    private void PumpVideo(IMFSourceReader reader, SampleRead? first, VideoFormat format, double anchor)
    {
        var pixels = Array.Empty<byte>();
        var pacer = new ReplayPacer();
        while (running)
        {
            var (hr, flags, time, sample) = first ?? Read(reader, MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
            first = null;
            if (hr < 0)
            {
                End($"decode stopped: {Describe(hr)}");
                return;
            }
            try
            {
                if ((flags & MF.MF_SOURCE_READERF_ERROR) != 0)
                {
                    End("decode stopped: the decoder reported an error");
                    return;
                }
                if ((flags & MF.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0) format = ReadVideoFormat(reader);
                if (sample is not null)
                {
                    var pts = time / 10_000_000.0;
                    if (outPoint is { } end && pts >= end)
                    {
                        End(null);
                        return;
                    }
                    // Pictures before the in point (the seek lands on the keyframe before it) aren't shown.
                    if (pts + 0.001 >= Start0)
                    {
                        var target = anchor + Math.Max(0, pts - Start0);
                        if (!WaitUntil(target)) return;
                        switch (pacer.Decide(ShowState.HostClock(), target, pts))
                        {
                            case ReplayPacer.Decision.Show:
                                if (CopyPicture(sample, format, ref pixels))
                                {
                                    frameSink(new VideoFrame(format.Width, format.Height, format.Width * 4, pixels, ShowState.HostClock()));
                                    Interlocked.Increment(ref delivered);
                                }
                                break;
                            case ReplayPacer.Decision.Drop:
                                Interlocked.Increment(ref dropped);
                                break;
                            case ReplayPacer.Decision.SkipAhead:
                                Interlocked.Increment(ref dropped);
                                if (pacer.Skips == 1)
                                    BroadcastLog.Warning("replay", $"{System.IO.Path.GetFileName(Path)}: this PC decodes it slower than it plays, so the pictures skip ahead to stay with the sound");
                                SeekTo(reader, pacer.SkipTo);
                                break;
                        }
                    }
                }
                if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    End(null);
                    return;
                }
            }
            finally
            {
                if (sample is not null) Marshal.ReleaseComObject(sample);
            }
        }
        first?.Release();
    }

    /// <summary>Copies the decoded picture into <paramref name="pixels"/> top row first, fully opaque. False if the buffer is unusable.</summary>
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
                // A negative stride means the rows are stored bottom-up: row 0 is the last one in memory.
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
                        var to = destination + row * rowBytes;
                        Buffer.MemoryCopy(from, to, rowBytes, rowBytes);
                        // RGB32's fourth byte is "unused"; frames are BGRA, so make it opaque.
                        var words = (uint*)to;
                        for (var x = 0; x < format.Width; x++) words[x] |= 0xFF000000;
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

    // MARK: Sound

    /// <summary>A second reader for the sound, as 48 kHz stereo float (or float at the file's own rate if Windows can't convert). Null when there's no usable sound.</summary>
    private IMFSourceReader? OpenAudio(out int rate, out int channels)
    {
        rate = 0;
        channels = 0;
        if (MF.MFCreateSourceReaderFromURL(Path, null, out var reader) < 0) return null;
        try
        {
            reader.SetStreamSelection(MF.MF_SOURCE_READER_ALL_STREAMS, 0);
            if (reader.SetStreamSelection(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 1) < 0)
            {
                Marshal.FinalReleaseComObject(reader);
                return null;
            }
            if (!TrySetAudioType(reader, exact: true) && !TrySetAudioType(reader, exact: false))
            {
                BroadcastLog.Warning("replay", $"{System.IO.Path.GetFileName(Path)}: its sound can't be decoded — replaying pictures only");
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
            if (rate <= 0 || channels <= 0)
            {
                Marshal.FinalReleaseComObject(reader);
                return null;
            }
            return reader;
        }
        catch (Exception)
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
                type.SetUINT32(MF.MF_MT_AUDIO_NUM_CHANNELS, WantedChannels);
                type.SetUINT32(MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, WantedSampleRate);
                type.SetUINT32(MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 32);
                type.SetUINT32(MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, WantedChannels * 4);
                type.SetUINT32(MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, WantedSampleRate * WantedChannels * 4);
            }
            return reader.SetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, type) >= 0;
        }
        finally
        {
            MediaFoundation.Release(type);
        }
    }

    private void PumpAudio(IMFSourceReader reader, SampleRead? first, double anchor, int rate, int channels)
    {
        var samples = Array.Empty<float>();
        try
        {
            while (running)
            {
                var (hr, flags, time, sample) = first ?? Read(reader, MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM);
                first = null;
                if (hr < 0 || (flags & MF.MF_SOURCE_READERF_ERROR) != 0) return; // The pictures decide when replay ends.
                if (sample is not null)
                {
                    try
                    {
                        var pts = time / 10_000_000.0;
                        var count = CopySound(sample, ref samples);
                        var frames = count / channels;
                        // Trim to the in and out points.
                        var skip = pts < Start0 ? (int)Math.Min(frames, Math.Round((Start0 - pts) * rate)) : 0;
                        var keep = outPoint is { } end ? (int)Math.Clamp(Math.Round((end - pts) * rate), 0, frames) : frames;
                        if (keep > skip)
                        {
                            var from = pts + skip / (double)rate;
                            var target = anchor + Math.Max(0, from - Start0);
                            if (!WaitUntil(target - AudioLeadSeconds)) return;
                            audioSink(new AudioBuffer(rate, channels, samples.AsMemory(skip * channels, (keep - skip) * channels), target));
                        }
                        if (outPoint is { } stop && pts + frames / (double)rate >= stop) return;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sample);
                    }
                }
                if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0) return;
            }
        }
        catch (Exception e)
        {
            BroadcastLog.Warning("replay", $"{System.IO.Path.GetFileName(Path)}: sound stopped — {e.Message}");
        }
        finally
        {
            first?.Release();
            Marshal.FinalReleaseComObject(reader);
        }
    }

    /// <summary>Copies a sound sample's floats out; returns how many.</summary>
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

    // MARK: Helpers

    /// <summary>Sleeps until <paramref name="hostTime"/> (ShowState.HostClock); false if stopped meanwhile.</summary>
    private bool WaitUntil(double hostTime)
    {
        while (running)
        {
            var remaining = hostTime - ShowState.HostClock();
            if (remaining <= 0) return true;
            if (remaining > 0.002) stopSignal.Wait(TimeSpan.FromSeconds(remaining - 0.001));
            else Thread.Yield();
        }
        return false;
    }

    private static string Describe(int hr) => Marshal.GetExceptionForHR(hr)?.Message is { Length: > 0 } message
        ? $"{message.TrimEnd('.', ' ')} (0x{hr:X8})"
        : $"0x{hr:X8}";
}

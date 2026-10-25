using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Hls.Vendored;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Media.Interop;

namespace MxU.Slides.Broadcast.Recording;

/// <summary>What the MP4 writer is asked to make.</summary>
internal sealed record Mp4Settings(
    int Width,
    int Height,
    int FrameRate,
    int Bitrate,
    int GopFrames,
    int? AudioSampleRate,
    int AudioChannels,
    int AudioBitrateKbps);

/// <summary>
/// Writes an MP4 file with Media Foundation's sink writer. It works two ways:
/// <list type="bullet">
/// <item><see cref="CreateForEncoded"/>: the recorder encodes (with the same <see cref="H264Encoder"/>
/// and <see cref="AacEncoder"/> streams use, which hold their bitrate) and the sink writer only
/// packs the H.264 and AAC into the file. This is the normal path.</item>
/// <item><see cref="CreateEncoding"/>: NV12 and PCM go in and the sink writer runs the encoder
/// itself; used for HEVC, which only hardware encoders offer here.</item>
/// </list>
/// Either way the file is a fragmented MP4 — a header, then self-contained fragments of one
/// keyframe interval each — so if the app or PC dies mid-recording, everything up to the last
/// finished fragment still plays (the Mac gets the same from AVAssetWriter's
/// movieFragmentInterval). Where Windows can't make fragmented MP4 it falls back to a plain MP4
/// (<see cref="Fragmented"/> false), playable only once finished. Use from one thread.
/// </summary>
internal sealed class Mp4SinkWriter : IDisposable
{
    private IMFSinkWriter? writer;
    private readonly uint videoStream;
    private readonly uint audioStream;
    private readonly bool hasAudio;
    private readonly byte[]? parameterSetsAnnexB;

    private Mp4SinkWriter(IMFSinkWriter writer, uint videoStream, uint? audioStream, bool fragmented, byte[]? parameterSetsAnnexB)
    {
        this.writer = writer;
        this.videoStream = videoStream;
        this.audioStream = audioStream ?? 0;
        hasAudio = audioStream is not null;
        Fragmented = fragmented;
        this.parameterSetsAnnexB = parameterSetsAnnexB;
    }

    public bool Fragmented { get; }

    /// <summary>
    /// A writer that packs already-encoded H.264 (with these parameter sets) and AAC described by
    /// <paramref name="audioType"/> — the AAC encoder's own output type, which the MP4 sink accepts
    /// as it is (null for no audio). <paramref name="crashSafe"/> false makes a plain MP4 straight
    /// away: for a file that's only used once it's finished (a rendered motion background), whose
    /// header then states its exact length (a fragmented file's can leave out the last fragment).
    /// </summary>
    public static Mp4SinkWriter CreateForEncoded(string path, Mp4Settings settings, AvcParameterSets parameterSets, IMFMediaType? audioType,
        bool crashSafe = true) =>
        Create(path, crashSafe, fragmented => Open(path, fragmented, hardware: false, writer =>
        {
            var video = AddEncodedVideo(writer, settings, parameterSets);
            uint? sound = audioType is null ? null : AddEncodedAudio(writer, audioType);
            return (video, sound);
        }, AnnexB(parameterSets)));

    /// <summary>A writer that encodes NV12 and 16-bit PCM itself (HEVC, or H.264 if <paramref name="hevc"/> is false).</summary>
    public static Mp4SinkWriter CreateEncoding(string path, Mp4Settings settings, bool hevc, bool allowHardware) =>
        Create(path, crashSafe: true, fragmented =>
        {
            var failures = new List<Exception>();
            foreach (var hardware in allowHardware ? new[] { true, false } : [false])
            {
                try
                {
                    return Open(path, fragmented, hardware, writer =>
                    {
                        var video = AddRawVideo(writer, settings, hevc);
                        uint? sound = settings.AudioSampleRate is null ? null : AddRawAudio(writer, settings);
                        return (video, sound);
                    }, null);
                }
                catch (Exception e) when (e is MediaFoundationException or COMException)
                {
                    failures.Add(e);
                    TryDelete(path);
                }
            }
            throw failures[^1];
        });

    private static Mp4SinkWriter Create(string path, bool crashSafe, Func<bool, Mp4SinkWriter> open)
    {
        MediaFoundation.EnsureStarted();
        var failures = new List<string>();
        foreach (var fragmented in crashSafe ? new[] { true, false } : [false])
        {
            try
            {
                return open(fragmented);
            }
            catch (Exception e) when (e is MediaFoundationException or COMException)
            {
                var attempt = $"{(fragmented ? "fragmented MP4" : "MP4")}: {e.Message}";
                failures.Add(attempt);
                BroadcastLog.Warning("recording", $"MP4 writer attempt failed — {attempt}");
                TryDelete(path);
            }
        }
        throw new MediaFoundationException("Couldn't start the MP4 writer (" + string.Join("; ", failures) + ")", unchecked((int)0xC00D5212));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static Mp4SinkWriter Open(string path, bool fragmented, bool hardware, Func<IMFSinkWriter, (uint Video, uint? Audio)> addStreams, byte[]? parameterSets)
    {
        var attributes = MediaFoundation.CreateAttributes();
        IMFSinkWriter? writer = null;
        try
        {
            MF.Check(attributes.SetGUID(MF.MF_TRANSCODE_CONTAINERTYPE,
                fragmented ? MF.MFTranscodeContainerType_FMPEG4 : MF.MFTranscodeContainerType_MPEG4), "container type");
            MF.Check(attributes.SetUINT32(MF.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, hardware ? 1u : 0u), "hardware transforms");
            MF.Check(MF.MFCreateSinkWriterFromURL(path, 0, attributes, out writer), "MFCreateSinkWriterFromURL");
            var (video, audio) = addStreams(writer);
            MF.Check(writer.BeginWriting(), "BeginWriting");
            return new Mp4SinkWriter(writer, video, audio, fragmented, parameterSets);
        }
        catch
        {
            if (writer is not null) Marshal.FinalReleaseComObject(writer);
            throw;
        }
        finally
        {
            MediaFoundation.Release(attributes);
        }
    }

    private static void SetVideoShape(IMFMediaType type, Mp4Settings settings)
    {
        type.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
        type.SetUINT64(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)settings.Width, (uint)settings.Height));
        type.SetUINT64(MF.MF_MT_FRAME_RATE, MF.Pack((uint)settings.FrameRate, 1));
        type.SetUINT64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
        type.SetUINT32(MF.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
    }

    private static byte[] AnnexB(AvcParameterSets parameterSets) =>
        [0, 0, 0, 1, .. parameterSets.Sps, 0, 0, 0, 1, .. parameterSets.Pps];

    /// <summary>An H.264 track whose samples arrive encoded: in and out types are the same, so nothing is re-encoded.</summary>
    private static uint AddEncodedVideo(IMFSinkWriter writer, Mp4Settings settings, AvcParameterSets parameterSets)
    {
        var type = MediaFoundation.CreateMediaType();
        try
        {
            SetVideoShape(type, settings);
            type.SetGUID(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_H264);
            type.SetUINT32(MF.MF_MT_AVG_BITRATE, (uint)settings.Bitrate);
            if (parameterSets.Sps.Length > 1) type.SetUINT32(MF.MF_MT_MPEG2_PROFILE, parameterSets.Sps[1]);
            var header = AnnexB(parameterSets);
            MF.Check(type.SetBlob(MF.MF_MT_MPEG_SEQUENCE_HEADER, header, (uint)header.Length), "sequence header");
            MF.Check(writer.AddStream(type, out var stream), "AddStream (video)");
            MF.Check(writer.SetInputMediaType(stream, type, null), "SetInputMediaType (video)");
            return stream;
        }
        finally
        {
            MediaFoundation.Release(type);
        }
    }

    /// <summary>An AAC track fed raw AAC frames, typed exactly as the AAC encoder describes its output.</summary>
    private static uint AddEncodedAudio(IMFSinkWriter writer, IMFMediaType type)
    {
        MF.Check(writer.AddStream(type, out var stream), "AddStream (audio)");
        MF.Check(writer.SetInputMediaType(stream, type, null), "SetInputMediaType (audio)");
        return stream;
    }

    private static uint AddRawVideo(IMFSinkWriter writer, Mp4Settings settings, bool hevc)
    {
        var output = MediaFoundation.CreateMediaType();
        var input = MediaFoundation.CreateMediaType();
        var encoding = MediaFoundation.CreateAttributes();
        try
        {
            SetVideoShape(output, settings);
            output.SetGUID(MF.MF_MT_SUBTYPE, hevc ? MF.MFVideoFormat_HEVC : MF.MFVideoFormat_H264);
            output.SetUINT32(MF.MF_MT_AVG_BITRATE, (uint)settings.Bitrate);
            MF.Check(writer.AddStream(output, out var stream), "AddStream (video)");

            SetVideoShape(input, settings);
            input.SetGUID(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_NV12);
            input.SetUINT32(MF.MF_MT_DEFAULT_STRIDE, (uint)settings.Width);
            input.SetUINT32(MF.MF_MT_VIDEO_NOMINAL_RANGE, MF.MFNominalRange_16_235);
            input.SetUINT32(MF.MF_MT_YUV_MATRIX, MF.MFVideoTransferMatrix_BT709);

            // As the Mac's videoSettings(): average bitrate, a keyframe each fragment, no B-frames.
            encoding.SetUINT32(MF.CODECAPI_AVEncCommonRateControlMode, MF.eAVEncCommonRateControlMode_CBR);
            encoding.SetUINT32(MF.CODECAPI_AVEncCommonMeanBitRate, (uint)settings.Bitrate);
            encoding.SetUINT32(MF.CODECAPI_AVEncMPVGOPSize, (uint)settings.GopFrames);
            encoding.SetUINT32(MF.CODECAPI_AVEncMPVDefaultBPictureCount, 0);
            if (writer.SetInputMediaType(stream, input, encoding) < 0)
                MF.Check(writer.SetInputMediaType(stream, input, null), "SetInputMediaType (video)");
            return stream;
        }
        finally
        {
            MediaFoundation.Release(output);
            MediaFoundation.Release(input);
            MediaFoundation.Release(encoding);
        }
    }

    private static uint AddRawAudio(IMFSinkWriter writer, Mp4Settings settings)
    {
        var output = MediaFoundation.CreateMediaType();
        var input = MediaFoundation.CreateMediaType();
        try
        {
            var rate = (uint)settings.AudioSampleRate!.Value;
            var channels = (uint)settings.AudioChannels;
            output.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio);
            output.SetGUID(MF.MF_MT_SUBTYPE, MF.MFAudioFormat_AAC);
            output.SetUINT32(MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            output.SetUINT32(MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
            output.SetUINT32(MF.MF_MT_AUDIO_NUM_CHANNELS, channels);
            output.SetUINT32(MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(settings.AudioBitrateKbps * 1000 / 8));
            output.SetUINT32(MF.MF_MT_AAC_PAYLOAD_TYPE, 0);
            output.SetUINT32(MF.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
            MF.Check(writer.AddStream(output, out var stream), "AddStream (audio)");

            input.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio);
            input.SetGUID(MF.MF_MT_SUBTYPE, MF.MFAudioFormat_PCM);
            input.SetUINT32(MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            input.SetUINT32(MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
            input.SetUINT32(MF.MF_MT_AUDIO_NUM_CHANNELS, channels);
            input.SetUINT32(MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * 2);
            input.SetUINT32(MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, rate * channels * 2);
            MF.Check(writer.SetInputMediaType(stream, input, null), "SetInputMediaType (audio)");
            return stream;
        }
        finally
        {
            MediaFoundation.Release(output);
            MediaFoundation.Release(input);
        }
    }

    /// <summary>Raw path: one NV12 picture.</summary>
    public void WriteVideo(ReadOnlySpan<byte> nv12, long time, long duration) => Write(videoStream, nv12, time, duration, keyframe: null);

    /// <summary>Raw path: 16-bit PCM.</summary>
    public void WriteAudio(ReadOnlySpan<short> pcm, long time, long duration)
    {
        if (hasAudio) Write(audioStream, MemoryMarshal.AsBytes(pcm), time, duration, keyframe: null);
    }

    /// <summary>
    /// Encoded path: one H.264 picture, as the byte stream Windows' encoders emit (start codes,
    /// parameter sets in front of keyframes), marked as a sync point when it is a keyframe.
    /// </summary>
    public void WriteEncodedVideo(EncodedVideoFrame frame, long time, long duration)
    {
        var picture = IsoTypeBufferUtil.ToByteStream(frame.Avcc);
        byte[] data = frame.IsKeyframe && parameterSetsAnnexB is { } sets ? [.. sets, .. picture] : picture;
        Write(videoStream, data, time, duration, frame.IsKeyframe);
    }

    /// <summary>Encoded path: one raw AAC frame.</summary>
    public void WriteEncodedAudio(ReadOnlySpan<byte> frame, long time, long duration)
    {
        if (hasAudio) Write(audioStream, frame, time, duration, keyframe: true);
    }

    private void Write(uint stream, ReadOnlySpan<byte> data, long time, long duration, bool? keyframe)
    {
        var target = writer ?? throw new ObjectDisposedException(nameof(Mp4SinkWriter));
        var sample = MediaFoundation.CreateSample(data, time, duration);
        try
        {
            if (keyframe is { } clean) sample.SetUINT32(MF.MFSampleExtension_CleanPoint, clean ? 1u : 0u);
            MF.Check(target.WriteSample(stream, sample), "WriteSample");
        }
        finally
        {
            MediaFoundation.Release(sample);
        }
    }

    /// <summary>Finishes the file (flushes, writes the last fragment) and closes it.</summary>
    public void Finish()
    {
        var target = writer;
        if (target is null) return;
        try
        {
            MF.Check(target.DoFinalize(), "Finalize");
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>Lets go of the file without finishing it (what a crash leaves).</summary>
    public void Dispose()
    {
        var target = Interlocked.Exchange(ref writer, null);
        if (target is not null) Marshal.FinalReleaseComObject(target);
    }
}

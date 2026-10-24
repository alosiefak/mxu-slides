using MxU.Slides.Broadcast.Media.Interop;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Streaming;

namespace MxU.Slides.Broadcast.Recording;

/// <summary>What the recorder thread hands pictures and sound to. Times are 100 ns units from the recording's start.</summary>
internal interface IRecordingPipeline : IDisposable
{
    /// <summary>The encoder in use, for the log.</summary>
    string Description { get; }

    /// <summary>True unless Windows could only write a plain MP4 (known once the file exists).</summary>
    bool Fragmented { get; }

    void WriteVideo(ReadOnlySpan<byte> nv12, long time, long duration);

    void WriteAudio(PendingAudioBuffer buffer, long time);

    /// <summary>Pictures the encoder had to leave out (a busy hardware encoder).</summary>
    int DroppedPictures { get; }

    /// <summary>Encodes what is still held, finishes and closes the file.</summary>
    void Finish();
}

/// <summary>
/// The usual pipeline: H.264 and AAC from our own encoders (hardware first, constant bitrate,
/// a keyframe each fragment interval), muxed by the sink writer without re-encoding. The file is
/// opened when the first picture comes out of the encoder, because the MP4 header needs that
/// picture's SPS/PPS; audio encoded before then waits. When the program goes quiet (no audio
/// buffers at all), <see cref="AudioGapFiller"/> keeps the sound track going with silence, because
/// an MP4 whose audio track has no samples won't open.
/// </summary>
internal sealed class EncodedRecordingPipeline : IRecordingPipeline
{
    private readonly string path;
    private readonly Mp4Settings settings;
    private readonly H264Encoder video;
    private readonly IMFMediaType? audioType;
    private readonly int audioRate;
    private readonly int audioChannels;
    private (int Rate, int Channels)? audioInput;
    private readonly long frameDuration;
    private readonly List<(byte[] Data, long Time)> pendingAudio = [];
    private AacEncoder? audio;
    private bool audioUnavailable;
    private Mp4SinkWriter? writer;
    private readonly AudioGapFiller gaps = new();
    private long lastAudioTime = long.MinValue;
    private double lastVideoEnd;

    public EncodedRecordingPipeline(string path, Mp4Settings settings, double keyframeInterval, EncoderPreference preference)
    {
        this.path = path;
        this.settings = settings;
        frameDuration = 10_000_000L / Math.Max(1, settings.FrameRate);
        video = H264Encoder.Create(new H264Settings(settings.Width, settings.Height, settings.FrameRate,
            Math.Max(100, settings.Bitrate / 1000), keyframeInterval), preference);
        try
        {
            if (settings.AudioSampleRate is { } rate)
            {
                // The track's format is fixed now (the file header needs it); incoming audio is
                // converted to it. Assume it already matches; a different input gets its own encoder later.
                (audioRate, audioChannels) = AacEncoder.SupportedOutput(rate, settings.AudioChannels);
                try
                {
                    audio = AacEncoder.Create(audioRate, audioChannels, audioRate, audioChannels, settings.AudioBitrateKbps);
                    audioInput = (audioRate, audioChannels);
                    audioType = audio.CopyOutputType();
                }
                catch (MediaFoundationException e)
                {
                    audioUnavailable = true;
                    BroadcastLog.Error("recording", $"{Path.GetFileName(path)}: no AAC encoder — recording without sound ({e.Message})");
                }
            }
            // Fail now, not at the first picture, if the folder can't be written.
            File.WriteAllBytes(path, []);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string Description => video.Name + (video.IsHardware ? " (hardware)" : "");

    public bool Fragmented => writer?.Fragmented ?? true;

    public int DroppedPictures => video.DroppedInputs;

    public void WriteVideo(ReadOnlySpan<byte> nv12, long time, long duration)
    {
        foreach (var frame in video.Encode(nv12, time / 10_000_000.0)) Write(frame);
        lastVideoEnd = (time + duration) / 10_000_000.0;
        FillSilence(final: false);
    }

    private void FillSilence(bool final)
    {
        if (audio is null || audioUnavailable || audioInput is not { } input) return;
        if (gaps.Gap(lastVideoEnd, input.Rate, final) is not { } gap) return;
        var silence = new float[gap.Frames * input.Channels];
        foreach (var packet in audio.Encode(silence, gap.Start)) Queue(packet);
    }

    private void Write(EncodedVideoFrame frame)
    {
        if (writer is null)
        {
            writer = Mp4SinkWriter.CreateForEncoded(path, settings, frame.ParameterSets, audioUnavailable ? null : audioType);
            foreach (var (data, time) in pendingAudio) WriteAudioFrame(data, time);
            pendingAudio.Clear();
        }
        writer.WriteEncodedVideo(frame, H264Encoder.ToHns(frame.PresentationTime), frameDuration);
    }

    public void WriteAudio(PendingAudioBuffer buffer, long time)
    {
        if (audio is null || audioUnavailable) return;
        if (audioInput != (buffer.SampleRate, buffer.Channels))
        {
            // The program's audio isn't the track's format: re-make the encoder with a converter in
            // front (same output, so the track's header still holds). Happens once, before any sound.
            audio.Dispose();
            audio = AacEncoder.Create(buffer.SampleRate, buffer.Channels, audioRate, audioChannels, settings.AudioBitrateKbps);
            audioInput = (buffer.SampleRate, buffer.Channels);
        }
        var start = time / 10_000_000.0;
        if (!gaps.Accept(start, (double)buffer.Length / buffer.Channels / buffer.SampleRate)) return;
        foreach (var packet in audio.Encode(buffer.Span, start)) Queue(packet);
    }

    private void Queue(EncodedAudioFrame packet)
    {
        var time = H264Encoder.ToHns(packet.PresentationTime);
        // Audio must only move forward in the file.
        if (time <= lastAudioTime) return;
        lastAudioTime = time;
        if (writer is null)
        {
            if (pendingAudio.Count < 512) pendingAudio.Add((packet.Data, time));
            return;
        }
        WriteAudioFrame(packet.Data, time);
    }

    private void WriteAudioFrame(byte[] data, long time) =>
        writer!.WriteEncodedAudio(data, time, (long)Math.Round(AacPacketClock.PacketFrames * 10_000_000 / audioRate));

    public void Finish()
    {
        foreach (var frame in video.Drain()) Write(frame);
        FillSilence(final: true);
        if (audio is not null && !audioUnavailable) foreach (var packet in audio.Drain()) Queue(packet);
        if (writer is null) throw new MediaFoundationException("No pictures were recorded", unchecked((int)0x80004005));
        writer.Finish();
    }

    public void Dispose()
    {
        video.Dispose();
        audio?.Dispose();
        writer?.Dispose();
        MediaFoundation.Release(audioType);
    }
}

/// <summary>
/// The HEVC pipeline: NV12 and 16-bit PCM go to the sink writer, which runs Windows' HEVC and
/// AAC encoders itself (HEVC encoders here are hardware ones).
/// </summary>
internal sealed class EncodingRecordingPipeline(string path, Mp4Settings settings, bool allowHardware) : IRecordingPipeline
{
    private readonly Mp4SinkWriter writer = Mp4SinkWriter.CreateEncoding(path, settings, hevc: true, allowHardware);
    private readonly AudioGapFiller gaps = new();
    private PcmConverter? converter;
    private double lastVideoEnd;

    public string Description => "Media Foundation HEVC";

    public bool Fragmented => writer.Fragmented;

    public int DroppedPictures => 0;

    public void WriteVideo(ReadOnlySpan<byte> nv12, long time, long duration)
    {
        writer.WriteVideo(nv12, time, duration);
        lastVideoEnd = (time + duration) / 10_000_000.0;
        FillSilence(final: false);
    }

    private void FillSilence(bool final)
    {
        if (settings.AudioSampleRate is not { } rate || gaps.Gap(lastVideoEnd, rate, final) is not { } gap) return;
        var silence = new short[gap.Frames * settings.AudioChannels];
        writer.WriteAudio(silence, (long)Math.Round(gap.Start * 10_000_000), gap.Frames * 10_000_000L / rate);
    }

    public void WriteAudio(PendingAudioBuffer buffer, long time)
    {
        if (settings.AudioSampleRate is not { } rate) return;
        if (!gaps.Accept(time / 10_000_000.0, (double)buffer.Length / buffer.Channels / buffer.SampleRate)) return;
        converter ??= new PcmConverter(buffer.SampleRate, buffer.Channels, rate, settings.AudioChannels);
        var pcm = converter.Convert(buffer.Span);
        var frames = pcm.Length / converter.OutputChannels;
        if (frames > 0) writer.WriteAudio(pcm, time, frames * 10_000_000L / rate);
    }

    public void Finish()
    {
        FillSilence(final: true);
        writer.Finish();
    }

    public void Dispose() => writer.Dispose();
}

/// <summary>
/// Keeps a recording's sound track unbroken when the program sends no audio (nothing playing,
/// or the audio tap off): once the picture is a full second ahead of the last sound, silence is
/// made up to half a second behind the picture, so sound that arrives late still fits after it.
/// At the end the track is filled to the last picture. Real audio that would land entirely in
/// time already covered is dropped, so the track only moves forward. Times are seconds from the
/// recording's start.
/// </summary>
internal sealed class AudioGapFiller
{
    public const double Patience = 1.0;
    public const double Lag = 0.5;

    private double end;

    /// <summary>Real audio from <paramref name="start"/> lasting <paramref name="duration"/>: false when it is all behind the track.</summary>
    public bool Accept(double start, double duration)
    {
        if (start + duration <= end + 0.001) return false;
        end = Math.Max(end, start + duration);
        return true;
    }

    /// <summary>Silence to make now (whole 1024-sample AAC frames), or null when none is due.</summary>
    public (double Start, int Frames)? Gap(double videoEnd, int sampleRate, bool final)
    {
        if (!final && videoEnd - end < Patience) return null;
        var until = final ? videoEnd : videoEnd - Lag;
        var frames = (int)((until - end) * sampleRate) / 1024 * 1024;
        if (frames <= 0) return null;
        var start = end;
        end += frames / (double)sampleRate;
        return (start, frames);
    }
}

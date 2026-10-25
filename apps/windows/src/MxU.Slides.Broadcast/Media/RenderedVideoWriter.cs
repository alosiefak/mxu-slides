using MxU.Slides.Broadcast.Recording;
using MxU.Slides.Core.Motion;

namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// Writes frames that are drawn one at a time (a motion background), rather than captured
/// in real time, to an H.264 MP4. It's the recorder's own pipeline: <see cref="H264Encoder"/>
/// (High profile, constant bitrate, no B-frames) and <see cref="Mp4SinkWriter"/> to pack the file.
/// <para>
/// Three differences from recording, all because nothing here is live:
/// <list type="bullet">
/// <item>It always uses Microsoft's <b>software</b> encoder. A graphics-card encoder that is
/// slow to ask for the next picture would make the recorder drop one rather than fall
/// behind; a drawn loop can't lose a frame, and the software encoder never drops any (it
/// takes each picture before it returns). It also makes the same file on every PC.</item>
/// <item>Each frame's time stamp is worked out from its number (<see cref="LoopTiming.TimeHns"/>),
/// not read from a clock, so frame i is exactly i ÷ frame rate seconds in.</item>
/// <item>It's a plain MP4, not the recorder's crash-safe fragments: a fragmented file's header can
/// leave out its last fragment (a 10-second loop said 9), and a player looping by the stated
/// length would then cut the loop short. The file is only used once it's finished anyway.</item>
/// </list>
/// Use from one thread.
/// </para>
/// </summary>
public sealed class RenderedVideoWriter : IDisposable
{
    private readonly string path;
    private readonly int frameRate;
    private readonly Mp4Settings settings;
    private readonly H264Encoder encoder;
    private Mp4SinkWriter? writer;
    private bool finished;

    /// <param name="path">The MP4 to write (replaced if it exists).</param>
    /// <param name="bitrate">Bits a second (see <see cref="LoopTiming.Bitrate"/>).</param>
    public RenderedVideoWriter(string path, int width, int height, int frameRate, int bitrate)
    {
        this.path = path;
        this.frameRate = frameRate;
        MediaFoundation.EnsureStarted();
        // A keyframe each second: a player looping back to the start, or someone scrubbing, never waits long.
        settings = new Mp4Settings(width, height, frameRate, bitrate, GopFrames: frameRate, AudioSampleRate: null, AudioChannels: 0, AudioBitrateKbps: 0);
        encoder = H264Encoder.Create(new H264Settings(width, height, frameRate, Math.Max(100, bitrate / 1000), KeyframeIntervalSeconds: 1),
            EncoderPreference.Software);
    }

    /// <summary>Frames written into the file so far.</summary>
    public int FramesWritten { get; private set; }

    /// <summary>The encoder's name, for the log.</summary>
    public string EncoderName => encoder.Name;

    /// <summary>Encodes frame number <paramref name="frame"/> (an NV12 picture of the writer's size).</summary>
    public void Write(ReadOnlySpan<byte> nv12, long frame)
    {
        ObjectDisposedException.ThrowIf(finished, this);
        foreach (var encoded in encoder.Encode(nv12, LoopTiming.TimeHns(frame, frameRate) / (double)LoopTiming.TicksPerSecond))
            Pack(encoded);
    }

    private void Pack(EncodedVideoFrame encoded)
    {
        // The MP4 header needs the encoder's first SPS/PPS, so the file is opened with the first picture out.
        // A plain MP4, not the recorder's crash-safe fragments: its header then gives the exact length,
        // which players that loop by the stated length (the Mac's) rely on. The file is only used once
        // it's finished, so crash safety buys nothing here.
        writer ??= Mp4SinkWriter.CreateForEncoded(path, settings, encoded.ParameterSets, null, crashSafe: false);
        var frame = (long)Math.Round(encoded.PresentationTime * frameRate);
        writer.WriteEncodedVideo(encoded, LoopTiming.TimeHns(frame, frameRate), LoopTiming.DurationHns(frame, frameRate));
        FramesWritten++;
    }

    /// <summary>Encodes what the encoder still holds and closes the file, which is then complete.</summary>
    public void Finish()
    {
        if (finished) return;
        foreach (var encoded in encoder.Drain()) Pack(encoded);
        finished = true;
        var file = writer ?? throw new InvalidOperationException("No frames were written");
        writer = null;
        file.Finish();
        encoder.Dispose();
    }

    /// <summary>Stops without finishing (cancelled or failed); the caller deletes the file.</summary>
    public void Dispose()
    {
        finished = true;
        writer?.Dispose();
        writer = null;
        encoder.Dispose();
    }
}
